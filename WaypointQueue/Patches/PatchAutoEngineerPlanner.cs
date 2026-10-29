using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using Track;
using UnityEngine;
using WaypointQueue.State;

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
            ModLocator.Loader.LogDebug($"{locomotive.Ident}: non-stop waypoint {managed.Id}: next waypoint's route {(runsOver ? "runs over it; handing over early" : "doesn't run over it")}");
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
                    ModLocator.Loader.LogError($"High-speed waypoint distance calc failed: {ex}");
                    return;
                }

                if (isWithinRadius)
                {
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"IsWaypointSatisfiedPostfix failed: {ex}");
            }
        }
    }

}