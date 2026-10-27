#if RR2026
using System;
using HarmonyLib;
using Model;
using Model.AI;
using Track;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.UUM;

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

        public static bool Start(BaseLocomotive locomotive, Action<Location, string, QueuedPose> onPicked)
        {
            var picker = AutoEngineerDestinationPicker.Shared;
            if (locomotive == null || picker == null)
            {
                return false;
            }
            picker.Cancel();
            _pending = onPicked;
            var ordersHelper = Loader.ServiceProvider.GetService<AutoEngineerService>().GetOrdersHelper(locomotive);
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
            action(Graph.Shared.ResolveLocationString(waypoint.LocationString), waypoint.CoupleToCarId ?? "", QueuedPose.From(waypoint));
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AutoEngineerDestinationPicker), "OnCancelled")]
        private static void OnCancelledPostfix()
        {
            _pending = null;
        }
    }
}
#endif
