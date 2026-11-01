using System.Collections.Generic;
using HarmonyLib;
using Model;
using Model.AI;
using Model.AI.Waypoints;
using Track;
using Track.Search;
using UI.Waypoints;
using UnityEngine;
using WaypointQueue.State;

namespace WaypointQueue
{
    // While the game's picker picks a queued waypoint, it draws and poses the train as it will be at that point in the
    // queue (TrainPrediction) instead of the train as it stands now. Front and back, the footprint and the placement
    // recorded with the pick are then those of the predicted train, so a pose picked before a coupling means what the
    // player saw ("back of train on the mark" puts the coupled cars there), and the placement goes out as picked when
    // the train's length matches at the time.
    [HarmonyPatch]
    internal static class PredictedPickerTrain
    {
        private static BaseLocomotive _locomotive;
        private static int _index = -1;
        private static TrainPrediction _prediction;

        private static readonly AccessTools.FieldRef<AutoEngineerDestinationPicker, BaseLocomotive> LocomotiveRef =
            AccessTools.FieldRefAccess<AutoEngineerDestinationPicker, BaseLocomotive>("_locomotive");
        private static readonly AccessTools.FieldRef<AutoEngineerDestinationPicker, List<Car>> ConsistRef =
            AccessTools.FieldRefAccess<AutoEngineerDestinationPicker, List<Car>>("_consist");
        private static readonly AccessTools.FieldRef<AutoEngineerDestinationPicker, float> TrainLengthRef =
            AccessTools.FieldRefAccess<AutoEngineerDestinationPicker, float>("_trainLength");
        private static readonly AccessTools.FieldRef<AutoEngineerDestinationPicker, HashSet<Car>> DontSnapRef =
            AccessTools.FieldRefAccess<AutoEngineerDestinationPicker, HashSet<Car>>("_dontSnapToCars");

        private static bool _explicit;

        // The next pick for this locomotive goes in at this queue index (a move or an insert in WaypointQueue's window);
        // -1 for none (a route waypoint).
        public static void SetNext(BaseLocomotive locomotive, int index)
        {
            _locomotive = locomotive;
            _index = index;
            _explicit = true;
        }

        // A pick from the game's own waypoint button goes on the end of the queue when appending is the default.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), nameof(AutoEngineerDestinationPicker.StartPickingLocation))]
        private static void StartPickingLocationPrefix(BaseLocomotive locomotive)
        {
            if (!_explicit || _locomotive != locomotive)
            {
                _locomotive = locomotive;
                _index = AppendsByDefault() ? QueueCount(locomotive) : -1;
            }
            _explicit = false;
            _prediction = null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "RefreshConsist")]
        private static void RefreshConsistPostfix(AutoEngineerDestinationPicker __instance)
        {
            BaseLocomotive locomotive = LocomotiveRef(__instance);
            if (locomotive == null || locomotive != _locomotive || _index <= 0)
            {
                return;
            }
            try
            {
                _prediction = TrainPrediction.ForQueueIndex(locomotive, _index);
            }
            catch (System.Exception e)
            {
                ModLocator.Loader.LogDebug($"No train prediction for {locomotive.Ident}: {e}");
                _prediction = null;
                return;
            }
            if (!_prediction.Changed)
            {
                return;
            }
            List<Car> consist = ConsistRef(__instance);
            consist.Clear();
            consist.AddRange(_prediction.Cars);
            TrainLengthRef(__instance) = _prediction.Length;
            // Don't snap the pick to a car that will be in the train by then.
            HashSet<Car> dontSnap = DontSnapRef(__instance);
            foreach (Car car in _prediction.Cars)
            {
                dontSnap.Add(car);
            }
            ModLocator.Loader.Log($"Picker shows {locomotive.Ident}'s train as predicted at waypoint {_index + 1}: {_prediction.Describe()}");
        }

        // The picker works out the pose from the locomotive where it stands now: the side of the mark the train comes
        // from (NearSideOf) and the natural arrival (the auto engineer's route search, PlanNaturalArrival). For a waypoint
        // later in the queue the train comes from wherever the earlier waypoints leave it, so where an earlier leg turns
        // it round, the previewed pose came out reversed. While picking such a waypoint both use the predicted train.
        // NearSideOf's own private helpers, so the side is worked out exactly as the game does, from the predicted train.
        private static readonly System.Func<IGraphView, Location, TrackSegment.End, float, Location> StepInto =
            AccessTools.MethodDelegate<System.Func<IGraphView, Location, TrackSegment.End, float, Location>>(AccessTools.Method(typeof(WaypointPoseCycle), "StepInto"));
        private static readonly System.Func<IGraphView, Location, TrackSegment.End, Vector3> DirectionToward =
            AccessTools.MethodDelegate<System.Func<IGraphView, Location, TrackSegment.End, Vector3>>(AccessTools.Method(typeof(WaypointPoseCycle), "DirectionToward"));

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WaypointPoseCycle), nameof(WaypointPoseCycle.NearSideOf))]
        private static bool NearSideOfPrefix(IGraphView graphView, Location waypoint, BaseLocomotive locomotive, out Vector3 nearDirection, ref TrackSegment.End __result)
        {
            nearDirection = default;
            if (_prediction == null || locomotive != _locomotive)
            {
                return true;
            }
            Vector3 train = _prediction.Position;
            Vector3 towardA = graphView.GetPosition(StepInto(graphView, waypoint, TrackSegment.End.A, 20f));
            Vector3 towardB = graphView.GetPosition(StepInto(graphView, waypoint, TrackSegment.End.B, 20f));
            __result = (towardA - train).sqrMagnitude <= (towardB - train).sqrMagnitude ? TrackSegment.End.A : TrackSegment.End.B;
            nearDirection = DirectionToward(graphView, waypoint, __result);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PosedWaypoint), nameof(PosedWaypoint.PlanNaturalArrival))]
        private static bool PlanNaturalArrivalPrefix(BaseLocomotive locomotive, Location mark, out List<RouteSearch.Step> route, ref bool __result)
        {
            route = null;
            if (_prediction == null || locomotive != _locomotive)
            {
                return true;
            }
            __result = _prediction.TryRouteTo(mark, out route);
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "PromptText")]
        private static void PromptTextPostfix(ref string __result)
        {
            if (_prediction != null && _prediction.Changed)
            {
                __result += "\nTrain at this waypoint (predicted): " + _prediction.Describe();
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "CleanUp")]
        private static void CleanUpPostfix()
        {
            _locomotive = null;
            _index = -1;
            _prediction = null;
        }

        private static int QueueCount(BaseLocomotive locomotive) =>
            ModStateManager.Shared.GetLocoWaypointState(locomotive.id)?.Waypoints?.Count ?? 0;

        private static bool AppendsByDefault()
        {
            var settings = ModLocator.Settings;
            return !settings.UseLegacyHotkeyOptions || settings.Input.appendWaypointMode.IsPressed();
        }
    }
}
