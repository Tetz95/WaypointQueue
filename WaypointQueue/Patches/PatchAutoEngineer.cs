using HarmonyLib;
using Model;
using Model.AI;
using Model.AI.SpeedControl;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using Track.Search;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Services;
using WaypointQueue.State;

namespace WaypointQueue.Patches
{
    [HarmonyPatch(typeof(AutoEngineer))]
    internal class PatchAutoEngineer
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineer.SetRouteProfile))]
        static bool SetRouteProfilePrefix(AutoEngineer __instance, ref RouteProfile profile)
        {
            AutoEngineerService autoEngineerService = ModLocator.ServiceProvider.GetService<AutoEngineerService>();
            ICarService carService = ModLocator.ServiceProvider.GetService<ICarService>();

            try
            {
                if (__instance == null || __instance?.Locomotive == null)
                {
                    return true;
                }

                BaseLocomotive loco = __instance.Locomotive;

                AutoEngineerOrdersHelper ordersHelper = autoEngineerService.GetOrdersHelper(loco);
                if (!ordersHelper.Orders.Waypoint.HasValue)
                {
                    return true;
                }

                LocoWaypointState waypointState = ModStateManager.Shared.GetLocoWaypointState(loco.id);

                if (waypointState.Waypoints.Count == 0 || waypointState.UnresolvedWaypoint == null)
                {
                    ModLocator.Loader.LogDebug($"SetRouteProfile {loco.Ident} has no managed waypoints");
                    return true;
                }

                // The order carries the mark moved up to a centimetre inside its segment (AutoEngineerService.InsideSegment),
                // so compare track positions, not the text.
                if (!AutoEngineerService.SameMark(waypointState.UnresolvedWaypoint?.LocationString, ordersHelper.Orders.Waypoint?.LocationString))
                {
                    ModLocator.Loader.LogDebug($"Loco {loco.Ident} unresolved waypoint to {waypointState.UnresolvedWaypoint?.LocationString} did not match {ordersHelper.Orders.Waypoint?.LocationString}");
                    return true;
                }

                // Only change route profile if we are not stopping
                if (waypointState.UnresolvedWaypoint.StopAtWaypoint)
                {
                    return true;
                }

                List<SpeedTarget> profileTargets = [.. profile.Targets];

                List<SpeedTarget> updatedSpeedTargets = [.. profile.Targets];

                float targetSpeedMps = profile.OrdersCeilingMps;
                if (waypointState.UnresolvedWaypoint.WillLimitPassingSpeed)
                {
                    targetSpeedMps = MphToMps(Mathf.Abs(waypointState.UnresolvedWaypoint.WaypointTargetSpeed));
                }

                if (updatedSpeedTargets != null && updatedSpeedTargets.Count > 0)
                {
                    int indexOfWaypoint = updatedSpeedTargets.FindIndex(t => t.Reason == "Running to waypoint" || t.Reason == "At waypoint");
                    if (indexOfWaypoint < 0)
                    {
                        ModLocator.Loader.LogDebug($"Did not find waypoint target");
                        return true;
                    }

                    (Location closestTrainEnd, Location _) = carService.GetTrainEndLocations(waypointState.UnresolvedWaypoint.Locomotive, waypointState.UnresolvedWaypoint.Location, out float closestDistance, out var _, out var _, out _, out _);

                    // No route (a turntable not lined to the waypoint's stall, say): the search reports 0 m, which
                    // matched the planner's "At waypoint" 0 m and turned its stop into running at track speed, so the
                    // train drove off in whatever direction it faced and kept going. Only a found route counts.
                    bool routeFound = GraphRouteSearchExtension.TryFindDistance(Graph.Shared, closestTrainEnd, waypointState.UnresolvedWaypoint.Location, out float totalDistance);

                    SpeedTarget t = updatedSpeedTargets[indexOfWaypoint];
                    if (routeFound && Mathf.Abs(totalDistance - t.DistanceM) < 10f)
                    {
                        ModLocator.Loader.LogDebug($"Updating AE target {targetSpeedMps}mps @ {t.DistanceM}m for reason {t.Reason}");
                        updatedSpeedTargets[indexOfWaypoint] = new SpeedTarget(t.DistanceM, targetSpeedMps, t.Reason);

                        profile = BuildRouteProfileForNewSpeedTargets(profile, updatedSpeedTargets);
                    }
                }
                return true;

            }
            catch (Exception e)
            {
                return true;
            }
        }

        private static string SpeedTargetToString(SpeedTarget target)
        {
            return $"{target.DistanceM}|{target.SpeedMps}|{target.Reason}";
        }

        private static float MpsToMph(float mps)
        {
            return mps / 0.447039276f;
        }

        private static float MphToMps(float mph)
        {
            return mph * 0.447039276f;
        }

        private static RouteProfile BuildRouteProfileForNewSpeedTargets(RouteProfile profile, List<SpeedTarget> speedTargets)
        {
            return new RouteProfile(profile.Grades, profile.UnderTrain, profile.Limits, speedTargets.ToList(), profile.AvailableDistanceM, profile.TrainLengthM, profile.OrdersCeilingMps, profile.HardCeilingMps, profile.Curvatures, profile.UnderTrainCurvatures);
        }
    }
}
