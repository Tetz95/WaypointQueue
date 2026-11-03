using HarmonyLib;
using Model;
using Track;
using UI.Waypoints;
using UnityEngine.InputSystem;

namespace WaypointQueue
{
    // The game's picker snaps a pick within 2 m of a free coupler to that coupler (a coupling order), and the train's
    // footprint then sits under the car, so the pose can't be seen. Holding the no-coupling shortcut (Right Alt by
    // default) keeps the pick on the track where it was clicked: no coupling, and the pose shows and can be chosen.
    [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "ProcessLocation")]
    internal static class PickerNoCarSnap
    {
        private static readonly AccessTools.FieldRef<AutoEngineerDestinationPicker, (Car car, Car.End end)?> CurrentCarInfoRef =
            AccessTools.FieldRefAccess<AutoEngineerDestinationPicker, (Car car, Car.End end)?>("_currentCarInfo");

        private static void Postfix(AutoEngineerDestinationPicker __instance, Location rawLocation, ref Location? __result)
        {
            if (__result == null || CurrentCarInfoRef(__instance) == null || !ModLocator.Settings.Input.noCouplingShortcut.IsPressed())
            {
                return;
            }
            CurrentCarInfoRef(__instance) = null;
            __result = rawLocation;
        }

        // While a pick is snapped to a car, the prompt says how to keep it on the track.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "PromptText")]
        private static void PromptTextPostfix(AutoEngineerDestinationPicker __instance, ref string __result)
        {
            if (CurrentCarInfoRef(__instance) == null)
            {
                return;
            }
            string key = ModLocator.Settings.Input.noCouplingShortcut.GetBindingDisplayString();
            if (!string.IsNullOrEmpty(key))
            {
                __result += $"\n[{key}] Place on the track instead of coupling to this car";
            }
        }
    }
}
