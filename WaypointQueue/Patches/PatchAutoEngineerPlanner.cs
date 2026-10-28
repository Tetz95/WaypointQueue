using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using System.Reflection;
using Track;
using Track.Search;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.State;
using WaypointQueue.UUM;
using static Model.AI.AutoEngineer;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal class PatchAutoEngineerPlanner
    {

        private const float HighSpeedMphThreshold = 35f;
        private const float HighSpeedWaypointRadiusMeters = 25f;

        [HarmonyPostfix]
        [HarmonyPatch("CheckWaypointArrival")]
        private static void CheckWaypointArrivalPostfix(
            ref WaypointArrival __result,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            ref Location? ____routeTargetLocation
        )
        {
            // Only "not there yet" can become arrived; the placement and coupling mismatches stay the game's call.
            if (__result != WaypointArrival.NotYet)
                return;
            bool arrived = false;
            IsWaypointSatisfiedPostfix(ref arrived, ____locomotive, ____graph, ref ____routeTargetLocation);
            if (arrived)
                __result = WaypointArrival.Arrived;
        }

        // Per waypoint: whether the route to the waypoint after it runs over it (worked out once).
        private static readonly Dictionary<string, bool> _nextRouteRunsOver = new Dictionary<string, bool>();

        private static bool NextRouteRunsOver(BaseLocomotive locomotive, ManagedWaypoint managed)
        {
            if (_nextRouteRunsOver.TryGetValue(managed.Id, out bool known))
                return known;
            LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(locomotive.id);
            int index = state.Waypoints.FindIndex(w => w.Id == managed.Id);
            if (index < 0 || index + 1 >= state.Waypoints.Count)
                return false;
            bool runsOver = WaypointResolver.RouteRunsOverWaypoint(managed, state.Waypoints[index + 1].Location);
            if (_nextRouteRunsOver.Count > 200)
                _nextRouteRunsOver.Clear();
            _nextRouteRunsOver[managed.Id] = runsOver;
            Loader.LogDebug($"{locomotive.Ident}: non-stop waypoint {managed.Id}: next waypoint's route {(runsOver ? "runs over it; handing over early" : "doesn't run over it")}");
            return runsOver;
        }

        private static void IsWaypointSatisfiedPostfix(
            ref bool __result,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            ref Location? ____routeTargetLocation
        )
        {
            try
            {

                if (__result)
                    return;

                if (____locomotive == null)
                    return;

                if (!WaypointQueueController.Shared.TryGetActiveWaypointFor(____locomotive, out ManagedWaypoint managed))
                    return;

                if (managed.StopAtWaypoint || managed.WaypointTargetSpeed <= 0)
                    return;

                // Below 35 mph only a waypoint the next one's route runs over: 2026.1's engineer brakes for the end of its
                // route, which is this waypoint, so a non-stop waypoint was passed at a crawl (4 mph for a 10 mph target)
                // unless the next waypoint goes out before the braking starts. Its route running over this waypoint's
                // track means the train still gets there, whichever way it goes on.
                float speedMph = ____locomotive.VelocityMphAbs;
                if (speedMph < HighSpeedMphThreshold && !NextRouteRunsOver(____locomotive, managed))
                    return;

                if (!____routeTargetLocation.HasValue || ____graph == null)
                    return;

                Location targetLoc = ____routeTargetLocation.Value;

                bool isWithinRadius;
                try
                {
                    Location locF = ____locomotive.LocationF;
                    Location locR = ____locomotive.LocationR;

                    // Measure along the track as the switches are lined, not in a straight line: a locomotive
                    // passing on a parallel track can be well within the radius without being anywhere near the waypoint
                    isWithinRadius = ____graph.CheckSameRoute(locF, targetLoc, HighSpeedWaypointRadiusMeters)
                        || ____graph.CheckSameRoute(locR, targetLoc, HighSpeedWaypointRadiusMeters);
                }
                catch (Exception ex)
                {
                    Loader.LogError($"High-speed waypoint distance calc failed: {ex}");
                    return;
                }

                if (isWithinRadius)
                {
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                Loader.LogError($"IsWaypointSatisfiedPostfix failed: {ex}");
            }
        }
    }

    [HarmonyPatch]
    internal class PatchAutoEngineerSetTargets
    {
        // 2026.1: the engineer drives from a speed profile the planner builds from the same target list right after
        // SetTargets (Legacy drove from the targets themselves, which a postfix on UpdateTargets could rewrite). Rewriting
        // the waypoint's target in that list, in place, here reaches both.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AutoEngineer), nameof(AutoEngineer.SetTargets))]
        static void SetTargetsPrefix(AutoEngineer __instance, Targets targets)
        {
            AutoEngineer ____engineer = __instance;
            AutoEngineerService autoEngineerService = Loader.ServiceProvider.GetService<AutoEngineerService>();
            ICarService carService = Loader.ServiceProvider.GetService<ICarService>();
            try
            {
                if (____engineer == null || ____engineer?.Locomotive == null)
                {
                    return;
                }

                BaseLocomotive loco = ____engineer.Locomotive;

                AutoEngineerOrdersHelper ordersHelper = autoEngineerService.GetOrdersHelper(loco);
                if (!ordersHelper.Orders.Waypoint.HasValue)
                {
                    return;
                }

                LocoWaypointState waypointState = ModStateManager.Shared.GetLocoWaypointState(loco.id);

                if (waypointState.Waypoints.Count == 0 || waypointState.UnresolvedWaypoint == null)
                {
                    Loader.LogDebug($"Update targets {loco.Ident} has no managed waypoints");
                    return;
                }

                if (!AutoEngineerService.SameMark(waypointState.UnresolvedWaypoint?.LocationString, ordersHelper.Orders.Waypoint?.LocationString))
                {
                    Loader.LogDebug($"Loco {loco.Ident} unresolved waypoint to {waypointState.UnresolvedWaypoint?.LocationString} did not match {ordersHelper.Orders.Waypoint?.LocationString}");
                }

                // Only update targets if we are not stopping
                if (waypointState.UnresolvedWaypoint.StopAtWaypoint)
                {
                    return;
                }

                if (targets == null)
                {
                    Loader.LogDebug($"Targets object was null");
                    return;
                }

                List<Targets.Target> updatedTargets = targets.AllTargets;
                float direction = Mathf.Sign(targets.MaxSpeedMph);

                float targetSpeed = ordersHelper.Orders.MaxSpeedMph;
                if (waypointState.UnresolvedWaypoint.WillLimitPassingSpeed)
                {
                    targetSpeed = waypointState.UnresolvedWaypoint.WaypointTargetSpeed;
                }

                float maxSafeSpeed = Mathf.Abs(targets.MaxSpeedMph);
                float speedToSet = Mathf.Clamp(targetSpeed, 0, maxSafeSpeed);
                float signedSpeedToSet = speedToSet * Mathf.Sign(direction);

                if (updatedTargets != null && updatedTargets.Count > 0)
                {
                    int indexOfWaypoint = updatedTargets.FindIndex(t => t.Reason == "Running to waypoint" || t.Reason == "At waypoint");
                    if (indexOfWaypoint != -1)
                    {
                        (Location closestTrainEnd, Location _) = carService.GetTrainEndLocations(waypointState.UnresolvedWaypoint.Locomotive, waypointState.UnresolvedWaypoint.Location, out float closestDistance, out var _, out var _, out _, out _);

                        // No route (a turntable not lined to the waypoint's stall, say): the search reports 0 m, which
                        // matched the planner's "At waypoint" 0 m and turned its stop into running at track speed, so
                        // the train drove off in whatever direction it faced and kept going. Only a found route counts.
                        bool routeFound = Graph.Shared.TryFindDistance(closestTrainEnd, waypointState.UnresolvedWaypoint.Location, out float totalDistance, out float traverseTimeSeconds);

                        Targets.Target t = updatedTargets[indexOfWaypoint];
                        if (routeFound && Mathf.Abs(totalDistance - t.Distance) < 10f)
                        {
                            //Loader.LogDebug($"Updating AE target {signedSpeedToSet}mph @ {t.Distance} for reason {t.Reason}");
                            updatedTargets[indexOfWaypoint] = new Targets.Target(signedSpeedToSet, t.Distance, t.Reason);
                        }
                    }
                    else
                    {
                        Loader.LogDebug($"Did not find waypoint target");
                    }
                }

            }
            catch (Exception e)
            {
                Loader.LogError($"SetTargetsPrefix exception: {e}");
            }
        }
    }
}