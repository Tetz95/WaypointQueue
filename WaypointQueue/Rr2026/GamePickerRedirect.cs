using System;
using HarmonyLib;
using Model;
using Model.AI;
using Track;
using WaypointQueue.Model;
using WaypointQueue.Services;

namespace WaypointQueue.UI
{
    // Railroader 2026.1's waypoint picker lets the player choose how the train stands at the mark (Space cycles the
    // pose) and shows the train's footprint there. Moving, inserting and route waypoints use it too: the pick is taken
    // from the orders helper (PatchAutoEngineerOrdersHelper) and handed to the waiting action instead of becoming the
    // locomotive's waypoint.
    [HarmonyPatch]
    internal static class GamePickerRedirect
    {
        private static Action<Location, string, QueuedPose> _pending;

        public static bool IsPending => _pending != null;

        private static readonly AccessTools.FieldRef<AutoEngineerDestinationPicker, bool> PoseChosenRef =
            AccessTools.FieldRefAccess<AutoEngineerDestinationPicker, bool>("_poseChosen");

        // Whether the player picked the pose (pressed Space) in the pick being committed, rather than taking the
        // picker's default (the way the train would arrive from where it is now).
        public static bool PoseChosen => AutoEngineerDestinationPicker.Shared != null && PoseChosenRef(AutoEngineerDestinationPicker.Shared);

        public static bool Start(BaseLocomotive locomotive, Action<Location, string, QueuedPose> onPicked)
        {
            var picker = AutoEngineerDestinationPicker.Shared;
            if (locomotive == null || picker == null)
            {
                return false;
            }
            picker.Cancel();
            _pending = onPicked;
            var ordersHelper = ModLocator.ServiceProvider.GetService<AutoEngineerService>().GetOrdersHelper(locomotive);
            picker.StartPickingLocation(locomotive, ordersHelper);
            return true;
        }

        // Hands a picked waypoint to the waiting action; false when nothing is waiting (an ordinary pick).
        public static bool TryTake(OrderWaypoint waypoint)
        {
            var action = _pending;
            if (action == null)
            {
                return false;
            }
            _pending = null;
            action(Graph.Shared.ResolveLocationString(waypoint.LocationString), waypoint.CoupleToCarId ?? "", QueuedPose.From(waypoint, PoseChosen));
            return true;
        }

        // Whether the pick being made goes into the queue (appended, inserted, or handed to a WPQ move or insert) rather
        // than becoming the locomotive's order at once.
        private static bool IsQueueingPick()
        {
            if (_pending != null)
            {
                return true;
            }
            // As PatchAutoEngineerOrdersHelper decides it: a held append or insert key queues; with no key held,
            // appending is the default unless the legacy hotkeys are on.
            var settings = ModLocator.Settings;
            if (settings.Input.appendWaypointMode.IsPressed() || settings.Input.insertNextWaypointMode.IsPressed())
            {
                return true;
            }
            return !settings.UseLegacyHotkeyOptions && !settings.Input.replaceWaypointMode.IsPressed();
        }

        // 2026.1's picker only commits a mark the train can be placed at and routed to from where it stands now. A
        // queued waypoint is driven later, often after the train has changed (a set-out shortens it, an earlier
        // waypoint lines a switch), so a queued pick is let through; its pose is planned again when it is sent
        // (QueuedPose.ToOrder), and the engineer reports then if it still can't get there.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "TryDerivePlacementForCommit")]
        private static void TryDerivePlacementForCommitPostfix(ref bool __result, ref string refusal)
        {
            if (!__result && IsQueueingPick())
            {
                ModLocator.Loader.LogDebug($"Picker would refuse a queued waypoint ({refusal}); letting it through");
                __result = true;
                refusal = null;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "OnCancelled")]
        private static void OnCancelledPostfix()
        {
            _pending = null;
        }
    }
}
