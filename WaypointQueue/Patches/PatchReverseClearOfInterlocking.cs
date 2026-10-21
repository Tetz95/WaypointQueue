using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Model;
using Model.AI;
using Track;
using Track.Search;
using Track.Signals;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    // When a route reverses beyond a switch (out of a spur onto the main, then back the other way), the AE's route
    // search turns the train back as soon as its length has cleared the switch points (Track.Search.ClearSwitch). At a
    // CTC interlocking that leaves the rear of the train in the plant: the OS stays occupied, so the dispatcher can't
    // throw the switch for the reverse move and the train waits for ever. Under CTC, move the turn-back point on until
    // the whole train is past the plant's blocks, so the reverse move is a normal signalled move into the plant.
    // The game rebuilds the route each time the train enters a CTC switch, and charges the rebuilt route heavily for
    // reversing on the spot (momentumRerouteAtCtcSwitch); by then the switch to turn back beyond is no longer under the
    // train, so a plant with a second switch on the way out (Ela West, from the distillery) got a turn-back far beyond
    // the next signal. That rebuild is skipped while the route still turns back ahead beyond a switch moved on here, as
    // long as the turn-back is still needed: a dry run of the game's search from where the train is now must still turn
    // back. When it doesn't (a stale turn-back), the game rebuilds as usual; skipping it then kept a route that ran past
    // the switch and backed up into a red signal (Tetz's AE Routing Fixes 1.4.1, 2026-10-20).
    [HarmonyPatch]
    internal static class PatchReverseClearOfInterlocking
    {
        // Beyond the last metre of the plant, so the rear of the train is clearly out of the OS.
        private const float Margin = 10f;
        private const float Step = 2f;
        private const float MaxSearch = 1000f;

        // Distance from a switch along its stem to the end of its interlocking's blocks, or 0 when there's nothing to add.
        private static readonly Dictionary<string, float> _plantExit = new();
        private static float _cacheTime = -1f;

        // Switches whose turn-back point has been moved on.
        private static readonly HashSet<string> _extended = new();

        private static MethodBase TargetMethod()
        {
            Type clearSwitch = AccessTools.TypeByName("Track.Search.ClearSwitch");
            return AccessTools.Constructor(clearSwitch, [typeof(TrackNode), typeof(float)]);
        }

        private static void Prefix(TrackNode node, ref float clearDistance)
        {
            try
            {
                if (!Loader.Settings.ReverseClearOfInterlockings || node == null || !node.IsCTCSwitch || node.IsCTCSwitchUnlocked)
                    return;

                float exit = PlantExit(node);
                if (exit <= 0f)
                    return;

                // Keep the game's turn-back point where the longer move would run off the end of the track.
                Graph graph = Graph.Shared;
                if (!graph.DecodeSwitchAt(node, out TrackSegment enter, out _, out _))
                    return;
                Location start = new Location(enter, 0f, enter.EndForNode(node));
                float extended = clearDistance + exit + Margin;
                graph.LocationByMoving(start, extended, checkSwitchAgainstMovement: false, stopAtEndOfTrack: false);
                clearDistance = extended;
                _extended.Add(node.id);
            }
            catch (Exception)
            {
                // End of track (or no graph yet): leave the game's turn-back point alone.
            }
        }

        [HarmonyPatch]
        private static class KeepRouteAtCtcSwitch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(AutoEngineerPlanner), "UpdateWaypointRouteIfNeeded");

            private static readonly MethodInfo RouteStartLocation = AccessTools.Method(typeof(AutoEngineerPlanner), "RouteStartLocation");
            private static readonly AccessTools.FieldRef<AutoEngineerPlanner, global::Model.AI.Orders> OrdersRef = AccessTools.FieldRefAccess<AutoEngineerPlanner, global::Model.AI.Orders>("_orders");
            private static readonly AccessTools.FieldRef<AutoEngineerPlanner, float> MaximumLength = AccessTools.FieldRefAccess<AutoEngineerPlanner, float>("_maximumLength");

            private static bool Prefix(AutoEngineerPlanner __instance, bool force, float baseMomentum, List<RouteSearch.Step> ____route, int ____startStepIndex, BaseLocomotive ____locomotive)
            {
                // Only the rebuild on entering a CTC switch carries momentum.
                if (!force || baseMomentum <= 0f || !Loader.Settings.ReverseClearOfInterlockings || ____route == null)
                    return true;
                for (int i = Math.Max(____startStepIndex, 0) + 1; i < ____route.Count; i++)
                {
                    RouteSearch.Step step = ____route[i];
                    if (step.Direction != ____route[i - 1].Direction && step.Node != null && _extended.Contains(step.Node.id))
                    {
                        if (!StillTurnsBack(__instance, baseMomentum))
                        {
                            Loader.LogDebug($"{____locomotive?.Ident}: the route no longer needs to turn back beyond {step.Node.id}; rebuilding it");
                            return true;
                        }
                        Loader.LogDebug($"{____locomotive?.Ident}: keeping the route through the CTC switch; it turns back beyond {step.Node.id}, clear of the interlocking");
                        return false;
                    }
                }
                return true;
            }

            // The game's own search (as RebuildRoute runs it first), from where the train is now: does the best route still
            // turn back anywhere? When in doubt, yes (keep the route).
            private static bool StillTurnsBack(AutoEngineerPlanner planner, float baseMomentum)
            {
                try
                {
                    OrderWaypoint? waypoint = OrdersRef(planner).Waypoint;
                    if (!waypoint.HasValue)
                        return true;
                    Graph graph = Graph.Shared;
                    Location end = graph.ResolveLocationString(waypoint.Value.LocationString);
                    object[] args = { 0f };
                    Location start = (Location)RouteStartLocation.Invoke(planner, args);
                    float momentum = (float)args[0] + baseMomentum;
                    var steps = new List<RouteSearch.Step>();
                    if (!graph.FindRoute(start, end, HeuristicCosts.AutoEngineer, steps, out _, checkForCars: false, MaximumLength(planner), momentum))
                        return true;
                    for (int i = 1; i < steps.Count; i++)
                    {
                        if (steps[i].Direction != steps[i - 1].Direction)
                            return true;
                    }
                    return false;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private static float PlantExit(TrackNode node)
        {
            if (Time.realtimeSinceStartup - _cacheTime > 60f || _cacheTime < 0f)
            {
                _plantExit.Clear();
                _cacheTime = Time.realtimeSinceStartup;
            }
            if (_plantExit.TryGetValue(node.id, out float cached))
                return cached;

            float exit = 0f;
            try
            {
                exit = MeasurePlantExit(node);
            }
            catch (Exception e)
            {
                Loader.LogDebug($"Couldn't measure the plant at switch {node.id}: {e.Message}");
            }
            _plantExit[node.id] = exit;
            if (exit > 0f)
                Loader.LogDebug($"Routes reversing beyond switch {node.id} turn back {exit:F0} m further on, clear of its interlocking");
            return exit;
        }

        private static float MeasurePlantExit(TrackNode node)
        {
            CTCPanelController panel = CTCPanelController.Shared;
            if (panel == null)
                return 0f;
            CTCInterlocking interlocking = panel.AllInterlockings.Values.FirstOrDefault(il =>
                il != null && il.switchSets != null && il.switchSets.Any(set => set.switchNodes != null && set.switchNodes.Contains(node)));
            if (interlocking == null)
                return 0f;
            List<TrackSpan> spans = interlocking.Blocks
                .SelectMany(b => b.GetComponentsInChildren<TrackSpan>(true))
                .Where(s => s != null && s.IsValid)
                .ToList();
            if (spans.Count == 0)
                return 0f;

            Graph graph = Graph.Shared;
            if (!graph.DecodeSwitchAt(node, out TrackSegment enter, out _, out _))
                return 0f;
            Location start = new Location(enter, 0f, enter.EndForNode(node));
            float exit = 0f;
            for (float d = Step; d <= MaxSearch; d += Step)
            {
                Location here;
                try
                {
                    here = graph.LocationByMoving(start, d, checkSwitchAgainstMovement: false, stopAtEndOfTrack: false);
                }
                catch (EndOfTrack)
                {
                    break;
                }
                if (!spans.Any(s => s.Contains(here)))
                    break;
                exit = d;
            }
            return exit;
        }
    }
}
