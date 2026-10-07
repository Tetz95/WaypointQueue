using HarmonyLib;
using KeyValue.Runtime;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    // The AE remembers where each switch it throws was (_routeSwitchesToRestore) and puts it back once the train has
    // passed, but only in memory. A save made mid-route and loaded again forgets them: the route carries on over a
    // switch already thrown, so it isn't recorded again, and the switch is left thrown behind the train. Keep a copy
    // on the locomotive's key-value store, which is saved with the game, and give it back to the AE after a load.
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal class PatchPersistSwitchRestores
    {
        internal const string Key = "wpq.aeSwitchRestore";

        // Planners whose saved list has been read back, and what was last written for each.
        private static readonly ConditionalWeakTable<AutoEngineerPlanner, Dictionary<string, bool>> _written = new();

        // One patch method per target: Harmony merges several [HarmonyPatch] names on one method into one target.
        // The prefixes run before PatchRestoreSwitchesAfterWaypoint's, so that one sees the restored entries too.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch("TrySetSwitch")]
        private static void TrySetSwitchPrefix(AutoEngineerPlanner __instance, BaseLocomotive ____locomotive, Dictionary<string, bool> ____routeSwitchesToRestore)
            => Hydrate(__instance, ____locomotive, ____routeSwitchesToRestore);

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch("RestorePassedSwitchesToOriginalPosition")]
        private static void RestorePassedPrefix(AutoEngineerPlanner __instance, BaseLocomotive ____locomotive, Dictionary<string, bool> ____routeSwitchesToRestore)
            => Hydrate(__instance, ____locomotive, ____routeSwitchesToRestore);

        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllPrefix(AutoEngineerPlanner __instance, BaseLocomotive ____locomotive, Dictionary<string, bool> ____routeSwitchesToRestore)
            => Hydrate(__instance, ____locomotive, ____routeSwitchesToRestore);

        [HarmonyPostfix]
        [HarmonyPatch("TrySetSwitch")]
        private static void TrySetSwitchPostfix(AutoEngineerPlanner __instance, BaseLocomotive ____locomotive, Dictionary<string, bool> ____routeSwitchesToRestore)
            => Persist(__instance, ____locomotive, ____routeSwitchesToRestore);

        [HarmonyPostfix]
        [HarmonyPatch("RestorePassedSwitchesToOriginalPosition")]
        private static void RestorePassedPostfix(AutoEngineerPlanner __instance, BaseLocomotive ____locomotive, Dictionary<string, bool> ____routeSwitchesToRestore)
            => Persist(__instance, ____locomotive, ____routeSwitchesToRestore);

        [HarmonyPostfix]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllPostfix(AutoEngineerPlanner __instance, BaseLocomotive ____locomotive, Dictionary<string, bool> ____routeSwitchesToRestore)
            => Persist(__instance, ____locomotive, ____routeSwitchesToRestore);

        [HarmonyPostfix]
        [HarmonyPatch("ClearRoute")]
        private static void ClearRoutePostfix(AutoEngineerPlanner __instance, BaseLocomotive ____locomotive, Dictionary<string, bool> ____routeSwitchesToRestore)
            => Persist(__instance, ____locomotive, ____routeSwitchesToRestore);

        // Gives the saved list back to the AE. After a load the planner can run before the car's saved values are
        // restored, so an empty saved list doesn't count: it waits until the list turns up or the AE starts one of
        // its own, which from then on is the one to keep.
        private static void Hydrate(AutoEngineerPlanner planner, BaseLocomotive locomotive, Dictionary<string, bool> routeSwitchesToRestore)
        {
            try
            {
                if (_written.TryGetValue(planner, out _) || locomotive?.KeyValueObject == null)
                    return;

                Dictionary<string, bool> saved = Read(locomotive);
                if (saved.Count == 0)
                {
                    if (routeSwitchesToRestore.Count > 0)
                        _written.Add(planner, []);
                    return;
                }

                foreach (KeyValuePair<string, bool> entry in saved)
                {
                    if (routeSwitchesToRestore.ContainsKey(entry.Key))
                        continue;
                    routeSwitchesToRestore.Add(entry.Key, entry.Value);
                    Loader.LogDebug($"Loco {locomotive.Ident}: switch {entry.Key} was thrown by its route before the game was loaded; will reset it to {(entry.Value ? "reverse" : "normal")} once passed");
                }
                _written.Add(planner, saved);
            }
            catch (Exception ex)
            {
                Loader.LogError($"PatchPersistSwitchRestores hydrate failed: {ex}");
            }
        }

        private static void Persist(AutoEngineerPlanner planner, BaseLocomotive locomotive, Dictionary<string, bool> routeSwitchesToRestore)
        {
            try
            {
                if (locomotive?.KeyValueObject == null || !Game.State.StateManager.IsHost)
                    return;

                // Never overwrite a saved list that hasn't been read back yet.
                Hydrate(planner, locomotive, routeSwitchesToRestore);
                if (!_written.TryGetValue(planner, out Dictionary<string, bool> last))
                    return;

                if (last.Count == routeSwitchesToRestore.Count && !last.Except(routeSwitchesToRestore).Any())
                    return;

                locomotive.KeyValueObject[Key] = routeSwitchesToRestore.Count == 0
                    ? Value.Null()
                    : Value.Dictionary(routeSwitchesToRestore.ToDictionary(e => e.Key, e => Value.Bool(e.Value)));
                last.Clear();
                foreach (KeyValuePair<string, bool> entry in routeSwitchesToRestore)
                    last.Add(entry.Key, entry.Value);
            }
            catch (Exception ex)
            {
                Loader.LogError($"PatchPersistSwitchRestores persist failed: {ex}");
            }
        }

        private static Dictionary<string, bool> Read(BaseLocomotive locomotive)
        {
            Value value = locomotive.KeyValueObject[Key];
            if (value.IsNull || value.Type != KeyValue.Runtime.ValueType.Dictionary)
                return [];
            return value.DictionaryValue.ToDictionary(e => e.Key, e => e.Value.BoolValue);
        }
    }
}
