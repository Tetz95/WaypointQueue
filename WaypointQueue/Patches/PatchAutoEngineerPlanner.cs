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

                if (waypointState.UnresolvedWaypoint?.LocationString != ordersHelper.Orders.Waypoint?.LocationString)
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

                        GraphRouteSearchExtension.TryFindDistance(Graph.Shared, closestTrainEnd, waypointState.UnresolvedWaypoint.Location, out float totalDistance, out float traverseTimeSeconds);

                        Targets.Target t = updatedTargets[indexOfWaypoint];
                        if (Mathf.Abs(totalDistance - t.Distance) < 10f)
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
        [HarmonyPatch("IsWaypointSatisfied")]
        private static void IsWaypointSatisfiedPostfix(
            AutoEngineerPlanner __instance,
            OrderWaypoint waypoint,
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

                float bestDistance;
                try
                {
                    Location locF = ____locomotive.LocationF;
                    Location locR = ____locomotive.LocationR;

                    float dF = Mathf.Abs(____graph.GetDistanceBetweenClose(targetLoc, locF));
                    float dR = Mathf.Abs(____graph.GetDistanceBetweenClose(targetLoc, locR));
                    bestDistance = Mathf.Min(dF, dR);
                }
                catch (Exception ex)
                {
                    Loader.LogError($"High-speed waypoint distance calc failed: {ex}");
                    return;
                }

                if (bestDistance <= HighSpeedWaypointRadiusMeters)
                {
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                Loader.LogError($"IsWaypointSatisfiedPostfix failed: {ex}");
            }
        }

        // When the AE reaches a waypoint, RestoreAllRemainingSwitches resets the switches it threw
        // and then forgets them all, skipping any switch the train is still on. With Send past
        // waypoint the train is then moved clear, so keep those switches per loco and hand them to
        // the move-past route, whose RestorePassedSwitchesToOriginalPosition resets them once the
        // rear is clear.
        private sealed class KeptSwitch
        {
            public bool OriginalThrown;
            public bool LeftThrown;
        }

        private sealed class KeptSwitches
        {
            public string WaypointId;
            public Dictionary<string, KeptSwitch> Switches;
        }

        private static readonly Dictionary<string, KeptSwitches> _switchesToRestoreAfterMovePast = [];

        internal static void ClearSwitchesToRestoreAfterMovePast()
        {
            _switchesToRestoreAfterMovePast.Clear();
        }

        [HarmonyPrefix]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllRemainingSwitchesPrefix(
            Dictionary<string, bool> ____routeSwitchesToRestore,
            out Dictionary<string, bool> __state
        )
        {
            __state = ____routeSwitchesToRestore != null && ____routeSwitchesToRestore.Count > 0
                ? new Dictionary<string, bool>(____routeSwitchesToRestore)
                : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllRemainingSwitchesPostfix(
            BaseLocomotive ____locomotive,
            Graph ____graph,
            Dictionary<string, bool> __state
        )
        {
            try
            {
                if (__state == null || ____locomotive == null || ____graph == null)
                    return;

                // Only the waypoint being resolved counts, not the first queued one.
                ManagedWaypoint managed = ModStateManager.Shared.GetLocoWaypointState(____locomotive.id).UnresolvedWaypoint;
                if (managed == null || !managed.MoveTrainPastWaypoint)
                    return;

                Dictionary<string, KeptSwitch> unrestored = [];
                foreach (KeyValuePair<string, bool> entry in __state)
                {
                    TrackNode node = ____graph.GetNode(entry.Key);
                    if (node != null && node.isThrown != entry.Value)
                        unrestored[entry.Key] = new KeptSwitch { OriginalThrown = entry.Value, LeftThrown = node.isThrown };
                }

                if (unrestored.Count == 0)
                    return;

                _switchesToRestoreAfterMovePast[____locomotive.id] = new KeptSwitches { WaypointId = managed.Id, Switches = unrestored };
                Loader.LogDebug($"Loco {____locomotive.Ident} left {unrestored.Count} switch(es) unrestored at the waypoint; restoring them after moving past it: {string.Join(", ", unrestored.Keys)}");
            }
            catch (Exception ex)
            {
                Loader.LogError($"RestoreAllRemainingSwitchesPostfix failed: {ex}");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("RebuildRoute")]
        private static void RebuildRoutePostfix(
            bool __result,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            Dictionary<string, bool> ____routeSwitchesToRestore
        )
        {
            try
            {
                // A failed rebuild has no route to restore on; keep the switches for the retry.
                if (!__result || ____locomotive == null || ____graph == null || ____routeSwitchesToRestore == null)
                    return;

                if (!_switchesToRestoreAfterMovePast.TryGetValue(____locomotive.id, out KeptSwitches kept))
                    return;

                _switchesToRestoreAfterMovePast.Remove(____locomotive.id);

                // Only the move-past route of the same waypoint takes them. Any other route means
                // the move past didn't happen, so drop them rather than throw switches later.
                ManagedWaypoint managed = ModStateManager.Shared.GetLocoWaypointState(____locomotive.id).UnresolvedWaypoint;
                if (managed == null || managed.Id != kept.WaypointId)
                {
                    Loader.LogDebug($"Loco {____locomotive.Ident} route is not the move past waypoint {kept.WaypointId}; not restoring its kept switches");
                    return;
                }

                foreach (KeyValuePair<string, KeptSwitch> entry in kept.Switches)
                {
                    TrackNode node = ____graph.GetNode(entry.Key);

                    // Leave switches someone else has changed since, or that the dispatcher controls.
                    if (node == null || node.isThrown != entry.Value.LeftThrown || (node.IsCTCSwitch && !node.IsCTCSwitchUnlocked))
                        continue;

                    if (!____routeSwitchesToRestore.ContainsKey(entry.Key))
                        ____routeSwitchesToRestore[entry.Key] = entry.Value.OriginalThrown;
                }
            }
            catch (Exception ex)
            {
                Loader.LogError($"RebuildRoutePostfix failed: {ex}");
            }
        }
    }
}