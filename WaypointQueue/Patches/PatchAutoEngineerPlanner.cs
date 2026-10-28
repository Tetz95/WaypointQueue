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
        [HarmonyPostfix]
        [HarmonyPatch("UpdateTargets")]
        static void UpdateTargetsPostfix(AutoEngineerPlanner __instance, float direction, ref AutoEngineer ____engineer)
        {
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

                Targets targets = (Targets)AccessTools.Field(typeof(AutoEngineer), "_targets")?.GetValue(____engineer);

                if (targets == null)
                {
                    Loader.LogDebug($"Targets object was null");
                    return;
                }

                List<Targets.Target> updatedTargets = [.. targets.AllTargets];

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

                FieldInfo allTargetsFI = AccessTools.Field(typeof(Targets), "AllTargets");
                if (allTargetsFI != null)
                {
                    allTargetsFI.SetValue(targets, updatedTargets);
                }
                else
                {
                    Loader.LogDebug($"AllTargetsFI was null");
                }
            }
            catch (Exception e)
            {
                Loader.LogError($"UpdateTargetsPostfix exception: {e}");
            }
        }

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

                float speedMph = ____locomotive.VelocityMphAbs;
                if (speedMph < HighSpeedMphThreshold)
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
}