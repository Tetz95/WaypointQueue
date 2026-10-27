using GalaSoft.MvvmLight.Messaging;
using Game;
using Game.Events;
using Game.Messages;
using Game.State;
using HarmonyLib;
using Model;
using Model.AI;
using Network;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.State;
using WaypointQueue.UI;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    [HarmonyPatch]
    public class WaypointQueueController : MonoBehaviour
    {
        private Coroutine _coroutine;

        private WaypointResolver _waypointResolver;
        private RefuelService _refuelService;
        private ICarService _carService;
        private AutoEngineerService _autoEngineerService;

        private static WaypointQueueController _shared;

        public static WaypointQueueController Shared
        {
            get
            {
                if (_shared == null)
                {
                    _shared = FindObjectOfType<WaypointQueueController>();
                }
                return _shared;
            }
        }

        public static float WaypointTickInterval = 0.5f;
        private readonly Dictionary<string, float> _timeSinceLastRerouteByLocoId = [];
        private readonly Queue<string> _rerouteRequestLocoIdQueue = [];
        private readonly HashSet<string> _rerouteRequestLocoIdsQueued = [];

        private void Awake()
        {
            Messenger.Default.Register<MapWillUnloadEvent>(this, OnMapWillUnload);
            _waypointResolver = Loader.ServiceProvider.GetService<WaypointResolver>();
            _refuelService = Loader.ServiceProvider.GetService<RefuelService>();
            _carService = Loader.ServiceProvider.GetService<ICarService>();
            _autoEngineerService = Loader.ServiceProvider.GetService<AutoEngineerService>();
        }

        private void OnMapWillUnload(MapWillUnloadEvent @event)
        {
            if (_coroutine != null)
            {
                Loader.LogDebug($"OnMapWillUnload stopping coroutine in WaypointQueueController OnMapWillUnload");
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        private IEnumerator Ticker()
        {
            WaitForSeconds t = new(WaypointTickInterval);
            while (true)
            {
                yield return t;
                Tick();
            }
        }

        private void Tick()
        {
            try
            {
                using (StateManager.TransactionScope())
                {
                    DoQueueTickUpdate();
                    HandleDelayedBleedAir();
                    HandleRerouteRequestQueue();
                }
            }
            catch (Exception e)
            {
                Loader.LogError(e.ToString());
                ErrorModalController.Shared.ShowTickErrorModal(e.Message);
                StopCoroutine(_coroutine);
                _coroutine = null;
            }
        }

        private void DoQueueTickUpdate()
        {
            HandleLoopingRoutes();

            List<LocoWaypointState> listForRemoval = [];

            List<LocoWaypointState> statesToProcess = [.. ModStateManager.Shared.LocoWaypointStates.Values];

            foreach (LocoWaypointState entry in statesToProcess)
            {
                List<ManagedWaypoint> waypointList = entry.Waypoints;

                if (entry.Locomotive == null)
                {
                    Loader.LogError($"Removing orphaned queue for missing locomotive id {entry.LocomotiveId}");
                    ModStateManager.Shared.RemoveLocoWaypointState(entry.LocomotiveId);
                    continue;
                }

                AutoEngineerOrdersHelper ordersHelper = _autoEngineerService.GetOrdersHelper(entry.Locomotive);

                if (!_autoEngineerService.IsInWaypointMode(ordersHelper))
                {
                    if (entry.UnresolvedWaypoint != null)
                    {
                        entry.UnresolvedWaypoint = null;
                        ModStateManager.Shared.SaveLocoWaypointState(entry.LocomotiveId, entry);
                    }
                    continue;
                }

                if (_carService.ShouldPeriodicReroute(entry.Locomotive) && _autoEngineerService.HasActiveWaypoint(ordersHelper))
                {
                    if (_timeSinceLastRerouteByLocoId.TryGetValue(entry.LocomotiveId, out float elapsed))
                    {
                        _timeSinceLastRerouteByLocoId[entry.LocomotiveId] = elapsed + WaypointTickInterval;
                    }
                    else
                    {
                        _timeSinceLastRerouteByLocoId[entry.LocomotiveId] = 0;
                    }

                    // Queue each loco once; it stays due every tick until its reroute runs.
                    if (_timeSinceLastRerouteByLocoId[entry.LocomotiveId] > 4f && _rerouteRequestLocoIdsQueued.Add(entry.LocomotiveId))
                    {
                        _rerouteRequestLocoIdQueue.Enqueue(entry.LocomotiveId);
                    }
                }

                // Send past waypoint without stopping: switch to the clear point while still approaching
                if (entry.UnresolvedWaypoint != null && _autoEngineerService.HasActiveWaypoint(ordersHelper)
                    && _waypointResolver.TryOrderClearBeyondEarly(entry.UnresolvedWaypoint, ordersHelper))
                {
                    continue;
                }

                if (!IsReadyToResolve(entry, ordersHelper))
                {
                    continue;
                }

                // Resolve waypoint order
                /**
                 * Unresolved waypoint should be the latest waypoint that this coroutine sent to the loco.
                 * We can't simply always resolve the first waypoint because we wouldn't know whether the loco has 
                 * actually performed the AE move order yet.
                 */
                if (entry.UnresolvedWaypoint != null)
                {
                    if (!_waypointResolver.HandleUnresolvedWaypoint(entry.UnresolvedWaypoint, ordersHelper, WaypointTickInterval))
                    {
                        continue;
                    }
                    else
                    {
                        //Loader.Log($"Finish resolving waypoint {state.UnresolvedWaypoint.Id} {state.UnresolvedWaypoint.Location} for {state.UnresolvedWaypoint.Locomotive.Ident}");
                        RemoveWaypoint(entry.UnresolvedWaypoint);
                    }
                }

                _timeSinceLastRerouteByLocoId.Remove(entry.LocomotiveId);

                // Send next waypoint
                if (waypointList.Count > 0)
                {
                    SendToFirstWaypoint(entry, ordersHelper);
                }

                // Mark if empty
                if (waypointList.Count == 0)
                {
                    var (assignedRouteId, loop) = RouteAssignmentRegistry.Get(entry.Locomotive.id);
                    if (loop && !string.IsNullOrEmpty(assignedRouteId))
                    {
                        var assignedRoute = RouteRegistry.GetById(assignedRouteId);
                        if (assignedRoute != null)
                        {
                            Loader.Log($"Loco {entry.Locomotive.Ident}: queue empty & looping enabled → reassigning route '{assignedRoute.Name}' (apply mode).");
                            // Re-apply the saved route, but we already know the waypoint list is currently empty so just append
                            AddWaypointsFromRoute(entry.Locomotive, assignedRoute, append: true);

                            // After reassigning, continue to next loco without marking for removal
                            continue;
                        }
                    }
                    listForRemoval.Add(entry);
                }
            }

            // Update list of states
            foreach (var entry in listForRemoval)
            {
                ModStateManager.Shared.RemoveLocoWaypointState(entry.LocomotiveId);
            }
        }

        private void HandleDelayedBleedAir()
        {
            var entries = ModStateManager.Shared.GetDelayedBleedAirCars();

            if (entries.Count == 0)
            {
                return;
            }

            List<DelayedBleedAirCutEntry> entriesToPersist = [];
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (TimeWeather.Now.TotalSeconds >= entry.DelayBleedUntilGameTotalSeconds)
                {
                    List<Car> nonnullCars = [.. entry.CarIds.Select(id => TrainController.Shared.CarForId(id)).Where(c => c != null)];
                    _carService.BleedAirOnCut(nonnullCars);
                }
                else
                {
                    entriesToPersist.Add(entry);
                }
            }
            ModStateManager.Shared.OverwriteDelayedBleedAirCars(entriesToPersist);
        }

        private void HandleRerouteRequestQueue()
        {
            if (_rerouteRequestLocoIdQueue.Count > 0)
            {
                string locoId = _rerouteRequestLocoIdQueue.Dequeue();
                _rerouteRequestLocoIdsQueued.Remove(locoId);
                _timeSinceLastRerouteByLocoId[locoId] = 0;
                RerouteCurrentWaypoint(locoId);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AutoEngineerPlanner), "SendMessageToRouteRequester")]
        static bool SendMessageToRouteRequesterPrefix(AutoEngineerPlanner __instance, Network.Messages.AlertLevel alertLevel)
        {
            // A periodic reroute stays quiet about the route it set, but not about a waypoint it can't reach: otherwise
            // "Unable to find a path to waypoint" never reached the player (a turntable not lined to a stall, say).
            if (alertLevel == Network.Messages.AlertLevel.Error)
            {
                return true;
            }
            BaseLocomotive loco = Traverse.Create(__instance).Field("_locomotive").GetValue<BaseLocomotive>();
            if (Shared._timeSinceLastRerouteByLocoId.TryGetValue(loco.id, out float elapsed) && elapsed == 0)
            {
                return false;
            }
            return true;
        }

        private bool IsReadyToResolve(LocoWaypointState entry, AutoEngineerOrdersHelper ordersHelper)
        {
            try
            {
                bool readyToResolve = !_autoEngineerService.HasActiveWaypoint(ordersHelper) && _autoEngineerService.IsInWaypointMode(ordersHelper);
                return readyToResolve || NeedsForceResolve(entry);
            }
            catch (Exception e)
            {
                throw new QueueTickException($"Exception while checking if waypoint for {entry.Locomotive.Ident} is ready to resolve: {e.Message}", e);
            }
        }

        private bool NeedsForceResolve(LocoWaypointState entry)
        {
            if (entry.UnresolvedWaypoint == null)
            {
                return false;
            }

            bool atEndOfTrack = _autoEngineerService.AtEndOfTrack(entry.Locomotive as BaseLocomotive);
            bool isNearWaypoint = _autoEngineerService.IsNearWaypoint(entry.UnresolvedWaypoint);
            bool isTrainStopped = _waypointResolver.IsTrainStopped(entry.UnresolvedWaypoint);
            bool needsEndOfTrackResolve = atEndOfTrack && isNearWaypoint && isTrainStopped;

            bool needsResolveFromWaitingSkipped = entry.UnresolvedWaypoint?.WaitingWasSkipped ?? false;

            bool needsAlreadyCoupledResolve = IsUnresolvedWaypointAlreadyCoupled(entry.UnresolvedWaypoint);

            return needsEndOfTrackResolve || needsAlreadyCoupledResolve || needsResolveFromWaitingSkipped;
        }

        /**
         * A coupling waypoint whose car is already in the train before the waypoint starts. Once the waypoint has
         * actually coupled, HasResolvedBrakeSystemOnCouple is set, so a waypoint that is re-sent after coupling
         * (for example after leaving and re-entering waypoint mode) isn't caught.
         */
        private bool IsCouplingTargetAlreadyInTrain(ManagedWaypoint wp, out Car car)
        {
            car = null;
            if (!wp.IsCoupling || wp.HasResolvedBrakeSystemOnCouple || !wp.TryResolveCoupleToCar(out car))
            {
                return false;
            }
            Car target = car;
            return wp.Locomotive.EnumerateCoupled().Any(c => c.id == target.id);
        }

        private bool IsUnresolvedWaypointAlreadyCoupled(ManagedWaypoint wp)
        {
            if (wp.IsCoupling && wp.TryResolveCoupleToCar(out Car car))
            {
                List<Car> consist = [.. wp.Locomotive.EnumerateCoupled()];
                if (consist.Contains(car))
                {
                    return true;
                }
            }
            return false;
        }

        public void AddWaypoint(BaseLocomotive loco, Location location, string coupleToCarId, bool isReplacing, bool isInsertingNext, QueuedPose pose = null)
        {
            Location clampedLocation = location.Clamped();
            bool isCoupling = coupleToCarId != null && coupleToCarId.Length > 0;
            string couplingLogSegment = isCoupling ? $"coupling to ${coupleToCarId}" : "no coupling";
            string actionName = "add";
            if (isReplacing) actionName = "replace";
            if (isInsertingNext) actionName = "insert next";
            Loader.Log($"Trying to {actionName} waypoint for loco {loco.Ident} to {clampedLocation} with {couplingLogSegment}");

            LocoWaypointState entry = ModStateManager.Shared.GetLocoWaypointState(loco.id);
            ManagedWaypoint waypoint = new ManagedWaypoint(loco, clampedLocation, coupleToCarId) { Pose = pose };
            _refuelService.CheckNearbyFuelLoaders(waypoint);

            if (isReplacing && entry.Waypoints.Count > 0)
            {
                waypoint.StatusLabel = "Running to waypoint";
                if (entry.Waypoints[0].Id == entry.UnresolvedWaypoint?.Id)
                {
                    _waypointResolver.CleanupBeforeRemovingWaypoint(entry.UnresolvedWaypoint);
                    entry.UnresolvedWaypoint = waypoint;
                }
                entry.Waypoints[0] = waypoint;
                ModStateManager.Shared.SaveLocoWaypointState(loco.id, entry);
                SendToFirstWaypoint(entry, _autoEngineerService.GetOrdersHelper(loco));
                Loader.LogDebug($"Replaced waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
                RestartCoroutine();
                return;
            }
            else if (isInsertingNext && entry.Waypoints.Count > 0)
            {
                entry.Waypoints.Insert(1, waypoint);
            }
            else
            {
                entry.Waypoints.Add(waypoint);
            }

            Loader.LogDebug($"Saving loco queue state after adding waypoint for loco id: {loco.id}");
            ModStateManager.Shared.SaveLocoWaypointState(loco.id, entry);
            Loader.Log($"Added waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location}");
            RestartCoroutine();
        }

        public void InsertWaypoint(BaseLocomotive loco, Location location, string coupledToCarId, string beforeWaypointId)
        {
            Location clampedLocation = location.Clamped();
            LocoWaypointState locoState = ModStateManager.Shared.GetLocoWaypointState(loco.id); ;
            ManagedWaypoint waypoint = new ManagedWaypoint(loco, clampedLocation, coupledToCarId);
            _refuelService.CheckNearbyFuelLoaders(waypoint);

            int beforeWaypointIndex = locoState.Waypoints?.FindIndex(w => w.Id == beforeWaypointId) ?? 0;
            if (beforeWaypointIndex < 0)
            {
                // The waypoint to insert before was resolved or removed meanwhile; put the new one first.
                Loader.LogDebug($"Waypoint {beforeWaypointId} to insert before no longer exists for {loco.Ident}; inserting at the front");
                beforeWaypointIndex = 0;
            }

            if (beforeWaypointIndex == 0)
            {
                waypoint.StatusLabel = "Running to waypoint";
            }

            locoState.Waypoints.Insert(beforeWaypointIndex, waypoint);

            locoState.UnresolvedWaypoint = locoState.Waypoints.FirstOrDefault();

            Loader.LogDebug($"Saving loco queue state after inserting waypoint for loco id: {loco.id}");
            ModStateManager.Shared.SaveLocoWaypointState(loco.id, locoState);
            Loader.Log($"Inserted waypoint for {waypoint.Locomotive.Ident} to {waypoint.Location} at index {beforeWaypointIndex}");
            RestartCoroutine();
        }

        public void AddWaypointsFromRoute(BaseLocomotive loco, RouteDefinition route, bool append)
        {
            if (loco == null || route == null) return;

            if (route.Waypoints == null || route.Waypoints.Count == 0) return;

            Loader.LogDebug($"Adding waypoints from {route.Name} to {loco.Ident} queue");

            var state = ModStateManager.Shared.GetLocoWaypointState(loco.id);

            int validWaypointsAdded = 0;
            foreach (var rw in route.Waypoints)
            {
                if (rw.TryCopyForRoute(out ManagedWaypoint copy, loco: loco))
                {
                    state.Waypoints.Add(copy);
                    validWaypointsAdded++;
                }
                else
                {
                    Loader.LogDebug($"Failed to add waypoint {rw.Id} from route {route.Name} to {loco.Ident} queue");
                }
            }
            Loader.LogDebug($"Saving loco queue state after adding waypoints from route id {route.Id} for loco id: {loco.id}");
            ModStateManager.Shared.SaveLocoWaypointState(loco.id, state);
            Loader.Log($"Added {validWaypointsAdded} waypoints for {loco.Ident} from route {route.Name}");
            RestartCoroutine();
        }

        private void HandleLoopingRoutes()
        {
            try
            {
                TryHandleLoopingRoutes();
            }
            catch (Exception e)
            {
                throw new QueueTickException("Failed to handle looping routes", e);
            }
        }

        private void TryHandleLoopingRoutes()
        {
            // Copied, since assignments that no longer resolve are removed from the collection below.
            List<RouteAssignment> assignmentsToCheck = ModStateManager.Shared.RouteAssignments.Values.Where(ra => ra.Loop).ToList();

            foreach (var assignment in assignmentsToCheck)
            {
                LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(assignment.LocoId);
                if (state.Waypoints.Count == 0)
                {
                    if (TrainController.Shared.TryGetCarForId(assignment.LocoId, out Car loco) && loco is BaseLocomotive)
                    {
                        RouteDefinition route = RouteRegistry.GetById(assignment.RouteId);
                        if (route == null)
                        {
                            Loader.LogError($"Failed to find route matching id {assignment.RouteId}");
                            ModStateManager.Shared.RemoveRouteAssignment(assignment.LocoId);
                            continue;
                        }
                        AddWaypointsFromRoute((BaseLocomotive)loco, route, true);
                    }
                    else
                    {
                        Loader.LogError($"Failed to find loco matching id {assignment.LocoId}");
                        ModStateManager.Shared.RemoveRouteAssignment(assignment.LocoId);
                        continue;
                    }
                }
            }
        }

        public void RemoveWaypoint(ManagedWaypoint waypoint)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.LocomotiveId);
            int index = state.Waypoints.FindIndex(w => w.Id == waypoint.Id);

            if (index < 0)
            {
                Loader.LogError($"Failed to find waypoint to remove by id {waypoint.Id}");
                return;
            }

            ManagedWaypoint waypointToRemove = state.Waypoints[index];

            if (waypointToRemove.Id == state.UnresolvedWaypoint?.Id)
            {
                state.UnresolvedWaypoint = null;
            }

            state.Waypoints.RemoveAt(index);

            Loader.LogDebug($"Saving loco queue state after removing waypoint {waypoint.Id} for loco id: {waypoint.LocomotiveId}");
            ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
        }

        public void RemoveCurrentWaypoint(string locoId)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(locoId);
            if (state.Waypoints.Count > 0)
            {
                RemoveWaypoint(state.Waypoints[0]);
            }
        }

        public void UpdateWaypoint(ManagedWaypoint waypoint)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.LocomotiveId);

            int index = state.Waypoints.FindIndex(w => w.Id == waypoint.Id);

            if (index < 0)
            {
                Loader.LogError($"Failed to find waypoint to update by id {waypoint.Id}");
                return;
            }

            state.Waypoints[index] = waypoint;

            // The unresolved waypoint is the one already sent to the train. Changing a waypoint that hasn't been sent
            // yet (its options set before the auto engineer was put in waypoint mode, say) marked it as sent: the next
            // tick found no waypoint order, took the train to have arrived, and resolved it where the train stood (a
            // "send past waypoint" then went the wrong way). Only the sent waypoint's own copy is refreshed.
            if (state.UnresolvedWaypoint != null && state.UnresolvedWaypoint.Id == waypoint.Id)
            {
                state.UnresolvedWaypoint = waypoint;
            }

            Loader.LogDebug($"Saving loco queue state after updating waypoint {waypoint.Id} for loco id: {waypoint.LocomotiveId}");
            ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
        }

        // Clears a waypoint's errors so the queue picks it up again: its remaining orders run again with the waypoint's
        // current settings (change them first if the error came from a setting).
        public void ResumeWaypoint(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resuming the waypoint for {waypoint.Locomotive.Ident} after {waypoint.Errors.Count} error(s)");
            // The train stopped short of the waypoint (wrong track or coupling ends): send it there again.
            bool resend = waypoint.Errors.Any(e => e.ErrorType == PatchWaypointMismatch.ErrorType);
            waypoint.Errors = [];
            waypoint.StatusLabel = "Running to waypoint";
            UpdateWaypoint(waypoint);
            if (resend)
            {
                LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.LocomotiveId);
                if (state.Waypoints.FirstOrDefault()?.Id == waypoint.Id)
                {
                    SendToFirstWaypoint(state);
                }
            }
        }

        public void ReorderWaypoint(ManagedWaypoint waypoint, int newIndex)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(waypoint.LocomotiveId);
            int oldIndex = state.Waypoints.FindIndex(w => w.Id == waypoint.Id);
            if (oldIndex < 0) return;

            state.Waypoints.RemoveAt(oldIndex);

            if (newIndex > oldIndex)
            {
                newIndex--; // the actual index could have shifted due to the removal
            }

            state.Waypoints.Insert(newIndex, waypoint);

            state.UnresolvedWaypoint = state.Waypoints.FirstOrDefault();

            Loader.LogDebug($"Saving loco queue state after reordering waypoint {waypoint.Id} for loco id: {waypoint.LocomotiveId}");
            ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
        }

        public void RerouteCurrentWaypoint(string locoId)
        {
            StateManager.ApplyLocal(new AutoEngineerWaypointRerouteRequest(locoId));
        }

        public void RefreshCurrentWaypoint(BaseLocomotive locomotive, AutoEngineerOrdersHelper ordersHelper)
        {
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(locomotive.id);
            if (state.Waypoints.Count > 0)
            {
                Loader.Log($"Resetting current waypoint as active");
                SendToFirstWaypoint(state, ordersHelper);
            }
        }

        public bool TryGetActiveWaypointFor(BaseLocomotive loco, out ManagedWaypoint waypoint)
        {
            waypoint = null;

            if (loco == null)
                return false;

            // Find the LocoWaypointState for this locomotive
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(loco.id);

            // The "active" waypoint is the unresolved one if present, otherwise the first in the list
            var active = state.UnresolvedWaypoint ?? state.Waypoints.FirstOrDefault();
            if (active == null)
                return false;

            waypoint = active;
            return true;
        }

        internal void SendToFirstWaypoint(LocoWaypointState state)
        {
            SendToFirstWaypoint(state, _autoEngineerService.GetOrdersHelper(state.Locomotive));
        }

        private void SendToFirstWaypoint(LocoWaypointState state, AutoEngineerOrdersHelper ordersHelper)
        {
            state = ModStateManager.Shared.GetLocoWaypointState(state.LocomotiveId);
            if (state.Waypoints.Count == 0) return;

            ManagedWaypoint waypoint = state.Waypoints.First();
            waypoint.StatusLabel = "Running to waypoint";

            state.UnresolvedWaypoint = waypoint;

            if (IsCouplingTargetAlreadyInTrain(waypoint, out Car coupledCar))
            {
                // Otherwise the waypoint counts as coupled at once, and any post-coupling cut is
                // calculated from a car in the middle of the train
                _autoEngineerService.CancelActiveOrders(state.Locomotive);
                var error = new CouplingException($"{waypoint.Locomotive.Ident} can't couple to {coupledCar.Ident} because it's already in {waypoint.Locomotive.Ident}'s train, probably picked up by an earlier waypoint. Delete this waypoint and add one without coupling, or couple to a different car.", waypoint);
                _waypointResolver.HandleProcessingError("Coupling", error, waypoint);
                ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
                return;
            }

            QueuedPose pose = waypoint.Pose;
            // 2026.1 places the train past the waypoint itself (the "train past it" pose), so the move past isn't a
            // second order after arriving. A pose the player picked already says where the train stands.
            if (!waypoint.StopAtWaypoint && waypoint.MoveTrainPastWaypoint && !waypoint.HasAnyCouplingOrders)
            {
                if (pose == null || !pose.IsPosed)
                {
                    pose = QueuedPose.PastTheMark(state.Locomotive, waypoint.Location);
                    waypoint.StatusLabel = "Sending train past waypoint";
                }
                waypoint.StopAtWaypoint = true;
                waypoint.MoveTrainPastWaypoint = false;
            }
            Loader.Log($"Sending next waypoint for {state.Locomotive?.Ident} to {waypoint.Location}");
            _autoEngineerService.SendToWaypoint(ordersHelper, waypoint.Location, waypoint.CoupleToCarId, pose);

            _waypointResolver.ApplyTimetableSymbolIfRequested(waypoint);

            ModStateManager.Shared.SaveLocoWaypointState(state.LocomotiveId, state);
        }

        internal void RestartCoroutine()
        {
            if (Multiplayer.IsHost)
            {
                if (_coroutine == null)
                {
                    Loader.LogDebug($"Starting waypoint coroutine");
                    _coroutine = StartCoroutine(Ticker());
                }
                else
                {
                    Loader.LogDebug($"Restarting waypoint coroutine");
                    StopCoroutine(_coroutine);
                    _coroutine = StartCoroutine(Ticker());
                }
            }
        }
    }

}
