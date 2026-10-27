using Game;
using Game.Messages;
using Game.Notices;
using Game.State;
using HarmonyLib;
using Model;
using Model.AI;
using Model.Ops.Timetable;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Track;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.UI;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    internal class WaypointResolver(UncouplingService uncouplingService, RefuelService refuelService, CouplingService couplingService, ICarService carService, AutoEngineerService autoEngineerService)
    {
        public static readonly string NoDestinationString = "No destination";
        public static readonly string RemoveTrainSymbolString = "remove-train-symbol";

        private readonly Dictionary<string, float> _timeSpentWaitingBeforeCutLookup = [];

        private readonly Dictionary<string, float> _timeSpentWaitingForRefuel = [];

        private readonly Dictionary<string, HashSet<string>> _locoIdsWithTroubleRefuelingByWaypointLocoId = [];

        private readonly Dictionary<string, int> _refuelRetryAttemptsByLocoId = [];

        private readonly int _refuelTimeoutSeconds = 15;

        private readonly int _maxRefuelRetryAttempts = 3;

        private readonly string _refuelNoticeKey = "wpq-refuel";

        public bool HandleUnresolvedWaypoint(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, float tickIntervalSeconds)
        {
            if (wp.Errors.Any())
            {
                return false;
            }

            try
            {
                return TryHandleUnresolvedWaypoint(wp, ordersHelper, tickIntervalSeconds);
            }
            catch (UncouplingException e)
            {
                HandleProcessingError("Uncoupling", e, wp);
                return false;
            }
            catch (CouplingException e)
            {
                HandleProcessingError("Coupling", e, wp);
                return false;
            }
            catch (RefuelException e)
            {
                HandleProcessingError("Refueling", e, wp);
                return false;
            }
            catch (Exception e)
            {
                HandleProcessingError("Waypoint", e, wp);
                return false;
            }
        }

        internal void HandleProcessingError(string orderType, Exception exception, ManagedWaypoint wp)
        {
            Loader.LogError($"Exception while processing waypoint: {exception}");
            Loader.Log($"Waypoint configuration:\n{wp}");
            wp.Errors.Add(new WaypointError(errorType: orderType, message: exception.Message));
            wp.StatusLabel = "Paused due to error";
            WaypointQueueController.Shared.UpdateWaypoint(wp);
            ErrorModalController.Shared.ShowProcessingErrorModal(exception.Message, orderType, wp);
        }

        /**
         * Returns false when the waypoint is not yet resolved (i.e. needs to continue)
         */
        private bool TryHandleUnresolvedWaypoint(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, float tickIntervalSeconds)
        {
            if (wp.WaitingWasSkipped)
            {
                return true;
            }

            // Loader.LogDebug($"Trying to handle unresolved waypoint for {wp.Locomotive.Ident}:\n {wp.ToString()}");
            if (!wp.StopAtWaypoint)
            {
                if (wp.MoveTrainPastWaypoint)
                {
                    if (OrderClearBeyondWaypoint(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        Loader.LogError($"Failed to move {wp.Locomotive.Ident} past the waypoint");
                    }
                }

                // TODO: refactor TryHandleUnresolvedWaypoint to
                // avoid handling orders like this when not stopping at a waypoint

                // Try to begin nearby coupling
                if (wp.WillSeekNearestCoupling && !wp.CurrentlyCouplingNearby)
                {
                    if (couplingService.FindNearbyCoupling(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                        WaypointQueueController.Shared.UpdateWaypoint(wp);
                        Loader.LogError($"{wp.Locomotive.Ident} cannot find a nearby car to couple.");
                    }
                }

                // Try coupling to target
                if (wp.WillSeekSpecificCarCoupling && !wp.CurrentlyCouplingSpecificCar)
                {
                    if (couplingService.FindSpecificCouplingTarget(wp, ordersHelper))
                    {
                        return false;
                    }
                    else
                    {
                        wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                    }
                }

                if (wp.HasAnyUncouplingOrders)
                {
                    ResolveUncouplingOrders(wp);
                }
                if (wp.WillChangeMaxSpeed)
                {
                    ResolveChangeMaxSpeed(wp);
                }
                return true;
            }

            // Check if done waiting
            if (wp.CurrentlyWaiting)
            {
                // We don't want to start waiting until after we resolve the current waypoint orders,
                // but we also don't want that resolving logic to run again after we are finished waiting
                return TryEndWaiting(wp);
            }

            if (wp.MoveTrainPastWaypoint)
            {
                if (OrderClearBeyondWaypoint(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    Loader.LogError($"Failed to move {wp.Locomotive.Ident} past the waypoint");
                }
            }

            // Try to begin nearby coupling
            if (wp.WillSeekNearestCoupling && !wp.CurrentlyCouplingNearby)
            {
                if (couplingService.FindNearbyCoupling(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                    WaypointQueueController.Shared.UpdateWaypoint(wp);
                    Loader.LogError($"{wp.Locomotive.Ident} cannot find a nearby car to couple.");
                }
            }

            // Try coupling to target
            if (wp.WillSeekSpecificCarCoupling && !wp.CurrentlyCouplingSpecificCar)
            {
                if (couplingService.FindSpecificCouplingTarget(wp, ordersHelper))
                {
                    return false;
                }
                else
                {
                    wp.CouplingSearchMode = ManagedWaypoint.CoupleSearchMode.None;
                }
            }

            // Connecting air and releasing handbrakes may be done before being completely stopped
            if (!wp.HasResolvedBrakeSystemOnCouple && wp.IsCoupling && !wp.CurrentlyRefueling && wp.TryResolveCoupleToCar(out Car _))
            {
                ResolveBrakeSystemOnCouple(wp);
                wp.HasResolvedBrakeSystemOnCouple = true;
                WaypointQueueController.Shared.UpdateWaypoint(wp);
            }

            if (!TryResolveFuelingOrders(wp, ordersHelper, tickIntervalSeconds))
            {
                return false;
            }

            /*
             * Unless explicitly not stopping, loco needs a complete stop before resolving orders that would uncouple.
             * Otherwise, some cars may be uncoupled and then recoupled if the train still has momentum.
             */
            if (!_timeSpentWaitingBeforeCutLookup.ContainsKey(wp.Id))
            {
                _timeSpentWaitingBeforeCutLookup[wp.Id] = 0f;
            }

            if (wp.StopAtWaypoint && !IsTrainStopped(wp) && wp.HasAnyCutOrders && _timeSpentWaitingBeforeCutLookup[wp.Id] < Loader.Settings.WaitBeforeCuttingTimeout)
            {
                if (!wp.CurrentlyWaitingBeforeCutting)
                {
                    Loader.Log($"{wp.Locomotive.Ident} is waiting until train is at rest to resolve cut orders");
                    wp.CurrentlyWaitingBeforeCutting = true;
                    wp.StatusLabel = $"Waiting until at rest to cut cars";
                    WaypointQueueController.Shared.UpdateWaypoint(wp);
                }

                if (Mathf.Floor(wp.Locomotive.VelocityMphAbs) == 0)
                {
                    _timeSpentWaitingBeforeCutLookup[wp.Id] += tickIntervalSeconds;
                }

                if (_timeSpentWaitingBeforeCutLookup[wp.Id] < Loader.Settings.WaitBeforeCuttingTimeout)
                {
                    return false;
                }
                else
                {
                    Loader.Log($"{wp.Locomotive.Ident} proceeding with cut after waiting {Loader.Settings.WaitBeforeCuttingTimeout} seconds from zero absolute velocity floor");
                }
            }

            if (wp.IsCoupling && wp.HasAnyPostCouplingCutOrders && wp.UncouplingMode != ManagedWaypoint.UncoupleMode.None)
            {
                ResolvePostCouplingCut(wp);
            }
            else if (wp.HasAnyUncouplingOrders)
            {
                ResolveUncouplingOrders(wp);
            }

            _timeSpentWaitingBeforeCutLookup.Remove(wp.Id);

            if (wp.WillChangeMaxSpeed)
            {
                ResolveChangeMaxSpeed(wp);
            }

            if (TryBeginWaiting(wp))
            {
                return false;
            }

            return true;
        }

        private void ResolveChangeMaxSpeed(ManagedWaypoint wp)
        {
            var ordersHelper = autoEngineerService.GetOrdersHelper(wp.Locomotive);
            int maxSpeedToSet = Mathf.Clamp(wp.MaxSpeedForChange, 0, 45);
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeedToSet, null, null);
        }

        public bool CleanupBeforeRemovingWaypoint(ManagedWaypoint wp)
        {
            if (_locoIdsWithTroubleRefuelingByWaypointLocoId.ContainsKey(wp.Locomotive.id))
            {
                foreach (var locoId in _locoIdsWithTroubleRefuelingByWaypointLocoId[wp.Locomotive.id])
                {
                    TrainController.Shared.TryGetCarForId(locoId, out Car carLoco);
                    if (carLoco is BaseLocomotive loco)
                    {
                        loco.PostNotice(_refuelNoticeKey, String.Empty);
                    }
                }
                _locoIdsWithTroubleRefuelingByWaypointLocoId.Remove(wp.Locomotive.id);
                // return early since we don't want to put away the loader equipment yet
                return true;
            }

            if (wp.RefuelLoaderAnimated)
            {
                try
                {
                    refuelService.SetCarLoaderSequencerWantsLoading(wp, false);
                }
                catch (Exception e)
                {
                    Loader.LogError($"{wp.Locomotive.Ident} failed to put away the {wp.RefuelLoadName} loader equipment: {e}");
                    Console.Log($"{wp.Locomotive.Ident} failed to put away the {wp.RefuelLoadName} loader equipment.");
                }
            }
            return true;
        }

        private bool TryResolveFuelingOrders(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper, float tickIntervalSeconds)
        {
            if (wp.RepositioningToRetryRefuel)
            {
                wp.RepositioningToRetryRefuel = false;
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                refuelService.OrderToRefuel(wp, ordersHelper, out var locosForRefuelList);
                return false;
            }

            // Reposition to refuel
            if (wp.WillRefuel && !wp.CurrentlyRefueling && !wp.HasAnyCouplingOrders && !wp.MoveTrainPastWaypoint)
            {
                refuelService.OrderToRefuel(wp, ordersHelper, out var locosForRefuelList);
                locosForRefuelList.ForEach(l =>
                {
                    _timeSpentWaitingForRefuel.Remove(l.id);
                    _refuelRetryAttemptsByLocoId.Remove(l.id);
                });
                return false;
            }

            // After reaching the reposition waypoint, begin refueling with animations
            if (wp.CurrentlyRefueling && !wp.RefuelLoaderAnimated && Mathf.Floor(wp.Locomotive.VelocityMphAbs) == 0)
            {
                refuelService.SetCarLoaderSequencerWantsLoading(wp, true);
                wp.RefuelLoaderAnimated = true;
                wp.StatusLabel = $"Refueling {wp.RefuelLoadName}";
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                return false;
            }

            // Can't check refueling progress if locomotive is still moving
            if (Mathf.Floor(wp.Locomotive.VelocityMphAbs) > 0)
            {
                return false;
            }

            // Check if done refueling
            if (wp.CurrentlyRefueling)
            {
                BaseLocomotive currentRefuelLoco = GetNextLocomotiveForRefuel(wp);
                bool locoIsFull = refuelService.IsLocoFull(currentRefuelLoco, wp.RefuelLoadName, wp.RefuelMaxCapacity, out bool isIncreasing);
                bool loaderIsEmpty = refuelService.IsLoaderEmpty(wp.RefuelIndustryId, wp.RefuelLoadName);

                if (isIncreasing)
                {
                    _timeSpentWaitingForRefuel[currentRefuelLoco.id] = 0;
                }

                if (locoIsFull || loaderIsEmpty)
                {
                    _timeSpentWaitingForRefuel.Remove(currentRefuelLoco.id);
                    _refuelRetryAttemptsByLocoId.Remove(currentRefuelLoco.id);

                    if (wp.RefuelLocoIdsQueue.Count > 0)
                    {
                        wp.RefuelLocoIdsQueue.RemoveAt(0);
                        wp.RefuelLoaderAnimated = false;
                        refuelService.SetCarLoaderSequencerWantsLoading(wp, false);
                        WaypointQueueController.Shared.UpdateWaypoint(wp);
                    }

                    // check if we need to continue refueling additional locomotives
                    BaseLocomotive nextLocoToRefuel = GetNextLocomotiveForRefuel(wp);
                    if (nextLocoToRefuel != null)
                    {
                        refuelService.OrderLocomotiveToRefuel(nextLocoToRefuel, wp, ordersHelper);
                        return false;
                    }
                    else
                    {
                        refuelService.CleanupAfterRefuel(wp, ordersHelper);
                    }
                }
                else if (!locoIsFull && !loaderIsEmpty && !isIncreasing)
                {
                    HandleIncrementingRefuelWaitingTime(currentRefuelLoco, tickIntervalSeconds);

                    if (_timeSpentWaitingForRefuel[currentRefuelLoco.id] < _refuelTimeoutSeconds)
                    {
                        // wait longer before retrying refuel position
                        return false;
                    }
                    else
                    {
                        if (ShouldRetryRefuel(currentRefuelLoco.id))
                        {
                            HandleRetryRefuel(wp, currentRefuelLoco, ordersHelper);
                        }
                        else
                        {
                            HandleUnableToRefuel(wp, currentRefuelLoco);
                        }
                        return false;
                    }
                }
                else
                {
                    //Loader.LogDebug($"Still refueling");
                    return false;
                }
            }

            return true;
        }

        private void HandleIncrementingRefuelWaitingTime(BaseLocomotive currentRefuelLoco, float tickIntervalSeconds)
        {
            if (_timeSpentWaitingForRefuel.TryGetValue(currentRefuelLoco.id, out float elapsed))
            {
                _timeSpentWaitingForRefuel[currentRefuelLoco.id] = elapsed + tickIntervalSeconds;
            }
            else
            {
                _timeSpentWaitingForRefuel[currentRefuelLoco.id] = tickIntervalSeconds;
            }
        }

        private bool ShouldRetryRefuel(string locoId)
        {
            if (_refuelRetryAttemptsByLocoId.TryGetValue(locoId, out int attempts) && attempts > _maxRefuelRetryAttempts)
            {
                return false;
            }
            return true;
        }

        private void HandleRetryRefuel(ManagedWaypoint wp, BaseLocomotive currentRefuelLoco, AutoEngineerOrdersHelper ordersHelper)
        {
            if (_refuelRetryAttemptsByLocoId.TryGetValue(currentRefuelLoco.id, out int attempts))
            {
                _refuelRetryAttemptsByLocoId[currentRefuelLoco.id] = attempts + 1;
            }
            else
            {
                _refuelRetryAttemptsByLocoId[currentRefuelLoco.id] = 1;
            }
            _timeSpentWaitingForRefuel.Remove(currentRefuelLoco.id);

            int maxSpeed = wp.MaxSpeedAfterRefueling;
            if (maxSpeed == 0) maxSpeed = 35;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeed, null, null);

            wp.RefuelLoaderAnimated = false;
            wp.RepositioningToRetryRefuel = true;
            refuelService.SetCarLoaderSequencerWantsLoading(wp, false);

            refuelService.RepositionToRetryRefueling(wp, currentRefuelLoco, ordersHelper);
            WaypointQueueController.Shared.UpdateWaypoint(wp);
        }

        private void HandleUnableToRefuel(ManagedWaypoint wp, BaseLocomotive currentRefuelLoco)
        {
            if (!_locoIdsWithTroubleRefuelingByWaypointLocoId.ContainsKey(wp.Locomotive.id))
            {
                _locoIdsWithTroubleRefuelingByWaypointLocoId.Add(wp.Locomotive.id, []);
            }
            if (!(_locoIdsWithTroubleRefuelingByWaypointLocoId.TryGetValue(wp.Locomotive.id, out var set) && set.Contains(currentRefuelLoco.id)))
            {
                _locoIdsWithTroubleRefuelingByWaypointLocoId[wp.Locomotive.id].Add(currentRefuelLoco.id);

                string message = $"""
                                The Auto Engineer could not accurately position {currentRefuelLoco.Ident} for refueling.
                                
                                Please switch {wp.Locomotive.Ident} to Manual, position {currentRefuelLoco.Ident} for refueling, and then return {wp.Locomotive.Ident} to AE Waypoint mode to continue the queue.
                                """;
                Console.Log(message);
                currentRefuelLoco.PostNotice("wpq-refuel", "Needs help refueling!");
                ModalAlertController.PresentOkay("Refueling Issue", message);
            }
        }

        private BaseLocomotive GetNextLocomotiveForRefuel(ManagedWaypoint waypoint)
        {
            if (waypoint.RefuelLocoIdsQueue.Count == 0)
            {
                return null;
            }
            return carService.GetLocoById(waypoint.RefuelLocoIdsQueue.First());
        }

        internal bool IsTrainStopped(ManagedWaypoint wp)
        {
            List<Car> coupled = [.. wp.Locomotive.EnumerateCoupled()];
            Car firstCar = coupled.First();
            Car lastCar = coupled.Last();
            //Loader.LogDebug($"First car {firstCar.Ident} is {(firstCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")} and last car {lastCar.Ident} is {(lastCar.IsStopped(2) ? "stopped for 2" : "NOT stopped")}");

            return firstCar.IsStopped(2) && lastCar.IsStopped(2);
        }

        private bool TryEndWaiting(ManagedWaypoint wp)
        {
            if (TimeWeather.Now.TotalSeconds >= wp.WaitUntilGameTotalSeconds)
            {
                Loader.Log($"Loco {wp.Locomotive.Ident} done waiting");
                wp.ClearWaiting();
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                return true;
            }
            //Loader.LogDebug($"Loco {wp.Locomotive.Ident} still waiting");
            return false;
        }

        private bool TryBeginWaiting(ManagedWaypoint wp)
        {
            return wp.WillWait && (TryBeginWaitingDuration(wp) || TryBeginWaitingUntilTime(wp));
        }

        private bool TryBeginWaitingDuration(ManagedWaypoint wp)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.Duration && wp.WaitForDurationMinutes > 0)
            {
                GameDateTime waitUntilTime = TimeWeather.Now.AddingMinutes(wp.WaitForDurationMinutes);
                wp.StatusLabel = "Waiting before continuing";
                wp.WaitUntilGameTotalSeconds = waitUntilTime.TotalSeconds;
                wp.CurrentlyWaiting = true;
                Loader.Log($"Loco {wp.Locomotive.Ident} waiting {wp.WaitForDurationMinutes}m until {waitUntilTime}");
                WaypointQueueController.Shared.UpdateWaypoint(wp);
                return true;
            }
            return false;
        }

        private bool TryBeginWaitingUntilTime(ManagedWaypoint wp)
        {
            if (wp.DurationOrSpecificTime == ManagedWaypoint.WaitType.SpecificTime)
            {
                if (TimetableReader.TryParseTime(wp.WaitUntilTimeString, out TimetableTime time))
                {
                    wp.SetWaitUntilByMinutes(time.Minutes, out GameDateTime waitUntilTime);
                    wp.StatusLabel = "Waiting before continuing";
                    wp.CurrentlyWaiting = true;
                    Loader.Log($"Loco {wp.Locomotive.Ident} waiting until {waitUntilTime}");
                    WaypointQueueController.Shared.UpdateWaypoint(wp);
                    return true;
                }
                else
                {
                    Loader.LogError($"Error parsing time: \"{wp.WaitUntilTimeString}\"");
                    Toast.Present("Waypoint wait time must be in HH:MM 24-hour format.");
                    return false;
                }
            }
            return false;
        }

        // Send past waypoint without stopping: once the train is this close, it is sent on to the point that clears the
        // waypoint. Ordered only on arrival, the game's own stop at the waypoint and a stop within one train length
        // braked the train almost to a stand short of the waypoint before it crept on to the clear point.
        internal const float ClearBeyondLeadDistance = 400f;
#if RR2026

        private static readonly bool NativeSendPast = true;
#endif

        internal bool TryOrderClearBeyondEarly(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
#if RR2026
            // 2026.1 sends the train past with a pose when the waypoint is sent (WaypointQueueController).
            if (NativeSendPast)
            {
                return false;
            }
#endif
            if (wp.StopAtWaypoint || !wp.MoveTrainPastWaypoint || wp.HasAnyCouplingOrders || wp.WaitingWasSkipped)
            {
                return false;
            }
            carService.GetTrainEndLocations(wp.Locomotive, wp.Location, out float closestDistance, out _, out _, out _, out _);
            if (closestDistance > ClearBeyondLeadDistance)
            {
                return false;
            }
            // Only on the last leg: with a turn-back still ahead (a train reversing out before it heads for the
            // waypoint) the train's far end can be on the wrong side, and "past" came out the wrong way.
            if (!TryGetLastLegApproach(wp, out Vector3 approachFrom))
            {
                return false;
            }
            // The point past the waypoint is reached by the auto engineer's own best route, which need not run through the
            // waypoint: past a yard track's far end it can lie on the main, and the train then stayed on the main and never
            // took the yard track. Ordered early only when that route still runs over the waypoint's track; otherwise the
            // train runs to the waypoint itself and is sent past on arrival.
            Location clearPoint;
            try
            {
                clearPoint = ClearPoint(wp, approachFrom);
            }
            catch (Exception)
            {
                return false;                                      // doesn't fit past: reported on arrival as before
            }
            if (!RouteRunsOverWaypoint(wp, clearPoint))
            {
                Loader.LogDebug($"{wp.Locomotive.Ident}: the best route past the waypoint doesn't run over it; sending past on arrival");
                return false;
            }
            Loader.Log($"{wp.Locomotive.Ident} is on its last approach to the waypoint ({closestDistance:F0} m); sending it past now");
            try
            {
                return OrderClearBeyondWaypoint(wp, ordersHelper, approachFrom);
            }
            catch (WaypointProcessingException e)
            {
                HandleProcessingError("Move past waypoint", e, wp);
                return false;
            }
        }

        private static readonly MethodInfo PlannerRouteStart = AccessTools.Method(typeof(AutoEngineerPlanner), "RouteStartLocation", [typeof(float).MakeByRefType()]);

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, float> PlannerMaximumLength =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, float>("_maximumLength");

        // The game's own route search (as the auto engineer will run it) from where the train is now to the point past the
        // waypoint: does it run over the waypoint's track segment? When in doubt, no (send past on arrival).
        private static bool RouteRunsOverWaypoint(ManagedWaypoint wp, Location clearPoint)
        {
            try
            {
                AutoEngineerPlanner planner = (wp.Locomotive as BaseLocomotive)?.AutoEngineerPlanner;
                TrackSegment segment = wp.Location.segment;
                if (planner == null || segment == null || PlannerRouteStart == null)
                {
                    return false;
                }
                if (clearPoint.segment == segment)
                {
                    return true;
                }
                object[] args = { 0f };
                Location start = (Location)PlannerRouteStart.Invoke(planner, args);
                var steps = new List<Track.Search.RouteSearch.Step>();
                if (!Track.Search.RouteSearch.FindRoute(Graph.Shared, start, clearPoint, Track.Search.HeuristicCosts.AutoEngineer, steps, out _,
                        checkForCars: false, PlannerMaximumLength(planner), (float)args[0]))
                {
                    return false;
                }
                for (int i = 0; i < steps.Count; i++)
                {
                    if (steps[i].Location.segment == segment)
                    {
                        return true;
                    }
                    if (i > 0 && steps[i].Node != null && steps[i - 1].Node != null
                        && (steps[i].Node == segment.a && steps[i - 1].Node == segment.b || steps[i].Node == segment.b && steps[i - 1].Node == segment.a))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, List<Track.Search.RouteSearch.Step>> PlannerRoute =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, List<Track.Search.RouteSearch.Step>>("_route");

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, int> PlannerStartStep =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, int>("_startStepIndex");

        // False while the auto engineer's route to the waypoint still turns back. Otherwise the point the train comes
        // from on its last approach (the route's last step before the waypoint, or the train's end nearest the
        // waypoint), which sets which way is past the waypoint.
        private bool TryGetLastLegApproach(ManagedWaypoint wp, out Vector3 approachFrom)
        {
            approachFrom = default;
            AutoEngineerPlanner planner = (wp.Locomotive as BaseLocomotive)?.AutoEngineerPlanner;
            List<Track.Search.RouteSearch.Step> route = planner == null ? null : PlannerRoute(planner);
            if (route == null || route.Count == 0)
            {
                return false;
            }
            // The route to this waypoint, not the one before it: the game finds a new route a moment after the waypoint
            // is sent, and until then still holds the old one.
            Vector3 waypointAt = Graph.Shared.GetPosition(wp.Location);
            if (Vector3.Distance(route[route.Count - 1].Position, waypointAt) > 2f)
            {
                return false;
            }
            int start = Math.Max(0, PlannerStartStep(planner));
            for (int i = start + 1; i < route.Count; i++)
            {
                if (route[i].Direction != route[i - 1].Direction)
                {
                    return false;
                }
            }
            for (int i = route.Count - 2; i >= start; i--)
            {
                Vector3 position = route[i].Position;
                if (Vector3.Distance(position, waypointAt) > 2f)
                {
                    approachFrom = position;
                    return true;
                }
            }
            (Location closest, _) = carService.GetTrainEndLocations(wp.Locomotive, wp.Location, out _, out _, out _, out _, out _);
            approachFrom = Graph.Shared.GetPosition(closest);
            return Vector3.Distance(approachFrom, waypointAt) > 2f;
        }

        private bool OrderClearBeyondWaypoint(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper, Vector3? approachFrom = null)
        {
            waypoint.StopAtWaypoint = true;
            waypoint.MoveTrainPastWaypoint = false;

            Loader.Log($"Beginning order to clear {waypoint.Locomotive.Ident} train past the waypoint");
            Location locationToMove;

            try
            {
                locationToMove = ClearPoint(waypoint, approachFrom);
            }
            catch (Exception e)
            {
                WaypointQueueController.Shared.UpdateWaypoint(waypoint);
                throw new WaypointProcessingException($"{waypoint.Locomotive.Ident} cannot fit train past the waypoint", waypoint, e);
            }

            waypoint.StatusLabel = "Sending train past waypoint";
            waypoint.OverwriteLocation(locationToMove);
            WaypointQueueController.Shared.UpdateWaypoint(waypoint);

            Loader.Log($"Sending train of {waypoint.Locomotive.Ident} to {locationToMove} past the waypoint");
            autoEngineerService.SendToWaypoint(ordersHelper, locationToMove);
            return true;
        }

        // One train length past the waypoint. Past is away from where the train comes from: its far end on arrival, or
        // (ordered on the approach) the route's point before the waypoint, so where the train stands on the way in
        // doesn't matter. Throws when the train doesn't fit past.
        private Location ClearPoint(ManagedWaypoint waypoint, Vector3? approachFrom)
        {
            (_, Location furthestCarLocation) = carService.GetTrainEndLocations(waypoint.Locomotive, waypoint.Location, out _, out _, out _, out _, out _);
            float totalTrainLength = GetTrainLength(waypoint.Locomotive as BaseLocomotive);
            Location orientedLocation = approachFrom.HasValue
                ? OrientedAwayFrom(waypoint.Location, approachFrom.Value)
                : Graph.Shared.LocationOrientedToward(waypoint.Location, furthestCarLocation);
            Location location = Graph.Shared.LocationByMoving(orientedLocation, totalTrainLength, checkSwitchAgainstMovement: false, stopAtEndOfTrack: false);
            location.AssertValid();
            return location;
        }

        private static Location OrientedAwayFrom(Location location, Vector3 point)
        {
            Graph graph = Graph.Shared;
            Vector3 ahead = graph.GetPosition(graph.LocationByMoving(location, 5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true));
            Vector3 behind = graph.GetPosition(graph.LocationByMoving(location.Flipped(), 5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true));
            return Vector3.Distance(ahead, point) >= Vector3.Distance(behind, point) ? location : location.Flipped();
        }

        private float GetTrainLength(BaseLocomotive locomotive)
        {
#if RR2026
            // 2026.1: a public static over the given cars.
            return AutoEngineerPlanner.CalculateTotalLength(locomotive.EnumerateCoupled().ToList());
#else
            MethodInfo calculateTotalLengthMI = AccessTools.Method(typeof(AutoEngineerPlanner), "CalculateTotalLength");
            float totalTrainLength = (float)calculateTotalLengthMI.Invoke(locomotive.AutoEngineerPlanner, []);
            return totalTrainLength;
#endif
        }

        private void ResolveBrakeSystemOnCouple(ManagedWaypoint waypoint)
        {
            if (!waypoint.IsCoupling) return;
            Loader.LogDebug($"Resolving coupling orders for loco {waypoint.Locomotive.Ident}");
            foreach (Car car in waypoint.Locomotive.EnumerateCoupled())
            {
                if (waypoint.ConnectAirOnCouple)
                {
                    carService.ConnectAir(car);
                }

                if (waypoint.ReleaseHandbrakesOnCouple)
                {
                    car.SetHandbrake(false);
                }
            }
        }

        private void ResolvePostCouplingCut(ManagedWaypoint waypoint)
        {
            if (!waypoint.TryResolveCoupleToCar(out Car carCoupledTo))
            {
                throw new UncouplingException($"Cannot perform post coupling cut due to unresolvable CoupledToCarId {waypoint.CoupleToCarId}", waypoint);
            }

            // Take active cut not allowed for pickups or dropoffs
            waypoint.TakeUncoupledCarsAsActiveCut = false;

            bool isPickup = waypoint.PostCouplingCutMode == ManagedWaypoint.PostCoupleCutType.Pickup;

            List<Car> carsToCut = [];
            if (waypoint.WillUncoupleByCount)
            {
                carsToCut = uncouplingService.FindPickupOrDropoffByCount(waypoint, carCoupledTo);
                if (carsToCut.Count == 0)
                {
                    string message = $"Waypoint for {waypoint.Locomotive.Ident} was configured to perform a post-coupling cut by count with {waypoint.NumberOfCarsToCut} cars to cut, but since no cars can be cut it will remain coupled to all cars.";
                    Console.Log(message);
                    Loader.Log(message);
                    Loader.Log($"Waypoint was: {waypoint}");
                    return;
                }
            }
            if (waypoint.WillUncoupleByDestination && !string.IsNullOrEmpty(waypoint.UncoupleDestinationId))
            {
                carsToCut = isPickup
                    ? uncouplingService.FindPickupByDestination(waypoint, carCoupledTo) : uncouplingService.FindDropoffByDestination(waypoint, carCoupledTo);

                if (!isPickup && carsToCut.Count == 0)
                {
                    string message = $"{waypoint.Locomotive.Ident} has no cars to drop off for {DescribeUncoupleDestination(waypoint)}, so it will remain coupled to all cars.";
                    Console.Log(message);
                    Loader.Log(message);
                    return;
                }
            }

            if (waypoint.WillUncoupleBySpecificCar)
            {
                carsToCut = isPickup
                    ? uncouplingService.FindPickupBySpecificCar(waypoint, carCoupledTo) : uncouplingService.FindDropoffBySpecificCar(waypoint, carCoupledTo);
            }

            if (waypoint.WillUncoupleAllExceptLocomotives)
            {
                carsToCut = isPickup
                    ? uncouplingService.FindPickupAllExceptLocomotives(waypoint, carCoupledTo) : uncouplingService.FindDropoffAllExceptLocomotives(waypoint, carCoupledTo);

                if (carsToCut.Count == 0 && isPickup)
                {
                    Loader.Log($"Pickup all except locomotives found zero cars to cut.");
                    return;
                }
            }

            if (isPickup && carsToCut.Count == 0)
            {
                string message = $"Waypoint for {waypoint.Locomotive.Ident} was configured to perform a post-coupling cut, but since no cars can be cut it will remain coupled to all cars.";
                Console.Log(message);
                Loader.Log(message);
                Loader.Log($"Waypoint was: {waypoint}");
                return;
            }

            uncouplingService.PerformCut(carsToCut, waypoint);
        }

        private static string DescribeUncoupleDestination(ManagedWaypoint waypoint)
        {
            return string.IsNullOrEmpty(waypoint.DestinationSearchText) ? waypoint.UncoupleDestinationId : waypoint.DestinationSearchText;
        }

        private void ResolveUncouplingOrders(ManagedWaypoint waypoint)
        {
            Loader.Log($"Resolving uncoupling orders for {waypoint.Locomotive.Ident}");
            List<Car> carsToCut = [];
            if (waypoint.WillUncoupleByCount && waypoint.NumberOfCarsToCut > 0)
            {
                carsToCut = uncouplingService.FindCutByCount(waypoint);
            }

            if (waypoint.WillUncoupleByDestination && !string.IsNullOrEmpty(waypoint.UncoupleDestinationId))
            {
                carsToCut = uncouplingService.FindCutByDestination(waypoint);

                if (carsToCut.Count == 0)
                {
                    string message = $"{waypoint.Locomotive.Ident} has no cars to cut for {DescribeUncoupleDestination(waypoint)}, so no cars were uncoupled.";
                    Console.Log(message);
                    Loader.Log(message);
                    return;
                }
            }

            if (waypoint.WillUncoupleBySpecificCar)
            {
                carsToCut = uncouplingService.FindCutBySpecificCar(waypoint);
            }

            if (waypoint.WillUncoupleAllExceptLocomotives)
            {
                uncouplingService.UncoupleAllExceptLocomotives(waypoint);
                return;
            }

            uncouplingService.PerformCut(carsToCut, waypoint);
        }

        internal void ApplyTimetableSymbolIfRequested(ManagedWaypoint waypoint)
        {
            if (String.IsNullOrEmpty(waypoint.TimetableSymbol)) return;

            string crewId = waypoint.Locomotive?.trainCrewId;

            if (string.IsNullOrEmpty(crewId))
            {
                var ident = (waypoint.Locomotive != null)
                    ? waypoint.Locomotive.Ident.ToString()
                    : "This locomotive";

                ModalAlertController.Present(
                    "No crew assigned to train",
                    $"{ident} has no crew. Assign a crew to use timetable symbols.",
                    new (bool, string)[] { (true, "OK") },
                    _ => { }
                );
                return;
            }

            if (waypoint.TimetableSymbol == RemoveTrainSymbolString)
            {
                waypoint.TimetableSymbol = null;
            }

            StateManager.ApplyLocal(new RequestSetTrainCrewTimetableSymbol(crewId, waypoint.TimetableSymbol));
            Loader.Log($"[Timetable] {(waypoint.TimetableSymbol ?? "None")} for {waypoint.Locomotive.Ident}");
        }
    }
}
