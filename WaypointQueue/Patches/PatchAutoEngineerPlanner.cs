using Game.State;
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
            AutoEngineerPlanner __instance,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            Dictionary<string, bool> __state
        )
        {
            if (!StateManager.IsHost) return;

            try
            {
                KeepUnrestored(__instance, ____locomotive, ____graph, __state, "with the train on it");
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"RestoreAllRemainingSwitches postfix failed: {ex}");
            }
        }

        // Runs before ClearRoute empties the list (after RestoreAllRemainingSwitches it's already empty).
        [HarmonyPrefix]
        [HarmonyPatch("ClearRoute")]
        private static void ClearRoutePrefix(
            AutoEngineerPlanner __instance,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            Dictionary<string, bool> ____routeSwitchesToRestore,
            Orders ____orders
        )
        {
            if (!StateManager.IsHost) return;

            try
            {
                if (____orders.Mode == Game.Messages.AutoEngineerMode.Waypoint && ____routeSwitchesToRestore != null && ____routeSwitchesToRestore.Count > 0)
                    KeepUnrestored(__instance, ____locomotive, ____graph, new Dictionary<string, bool>(____routeSwitchesToRestore), "when its route was cleared");
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"ClearRoute prefix failed: {ex}");
            }
        }

        // The AE records where a switch was when it first throws it, as the position to put it back to. A switch an
        // earlier route left thrown, still waiting in the restorer, isn't where it belongs, so recording it there made
        // the new route put it back the wrong way. The restorer saw the switch "back" and dropped it, and from then on
        // the thrown position was the one restored (a switch left reversed for good). A route that throws a switch the
        // restorer is holding takes it over with the restorer's position.
        [HarmonyPrefix]
        [HarmonyPatch("TrySetSwitch")]
        private static void TrySetSwitchPrefix(TrackNode node, Dictionary<string, bool> ____routeSwitchesToRestore, out bool __state)
        {
            __state = node != null && ____routeSwitchesToRestore != null && !____routeSwitchesToRestore.ContainsKey(node.id);
        }

        [HarmonyPostfix]
        [HarmonyPatch("TrySetSwitch")]
        private static void TrySetSwitchPostfix(TrackNode node, Dictionary<string, bool> ____routeSwitchesToRestore, BaseLocomotive ____locomotive, bool __state)
        {
            if (!StateManager.IsHost) return;

            var restorer = ModLocator.Loader.PendingSwitchRestorer;
            try
            {
                if (__state && ____routeSwitchesToRestore.ContainsKey(node.id) && restorer.TryTake(node.id, out bool originalThrown))
                {
                    ____routeSwitchesToRestore[node.id] = originalThrown;
                    ModLocator.Loader.LogDebug($"Loco {____locomotive?.Ident}: route takes over switch {node.id} from the restorer; it goes back to {(originalThrown ? "reverse" : "normal")}");
                }
            }
            catch (Exception ex)
            {
                ModLocator.Loader.LogError($"TrySetSwitch postfix failed: {ex}");
            }
        }

        // The switches in the list still away from where the AE found them go to the restorer.
        private static void KeepUnrestored(AutoEngineerPlanner planner, BaseLocomotive locomotive, Graph graph, Dictionary<string, bool> switches, string why)
        {
            if (switches == null || locomotive == null || graph == null || !StateManager.IsHost)
                return;

            var restorer = ModLocator.Loader.PendingSwitchRestorer;

            foreach (KeyValuePair<string, bool> entry in switches)
            {
                TrackNode node = graph.GetNode(entry.Key);
                if (node == null || node.isThrown == entry.Value)
                    continue;

                restorer.Add(new PendingSwitchRestorer.PendingSwitch
                {
                    NodeId = entry.Key,
                    OriginalThrown = entry.Value,
                    LeftThrown = node.isThrown,
                    LocomotiveId = locomotive.id,
                    LocomotiveIdent = locomotive.Ident.ToString(),
                    Position = node.Position,
                    AddedAt = Time.unscaledTime
                });
                ModLocator.Loader.LogDebug($"Loco {locomotive.Ident} couldn't reset switch {entry.Key} {why}; will reset it once the train is clear");
            }
        }
    }

}