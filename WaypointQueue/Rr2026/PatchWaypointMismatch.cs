using System;
using HarmonyLib;
using Model;
using Model.AI;
using WaypointQueue.Model;

namespace WaypointQueue
{
    // Railroader 2026.1 checks a posed waypoint on arrival: a train that comes to rest on a different track than the
    // placement asked for, or couples by the wrong ends, is stopped and its waypoint cleared, just as an arrival
    // clears it. The queue would take that for arriving and run the waypoint's cuts and couplings in the wrong place,
    // so the waypoint is paused with an error instead. Resuming sends the train to the waypoint again.
    [HarmonyPatch(typeof(AutoEngineerPlanner), "StopForMismatch")]
    internal static class PatchWaypointMismatch
    {
        internal const string ErrorType = "Placement";

        private static void Postfix(string notice, BaseLocomotive ____locomotive)
        {
            try
            {
                if (____locomotive == null || !WaypointQueueController.Shared.TryGetActiveWaypointFor(____locomotive, out ManagedWaypoint waypoint))
                {
                    return;
                }
                var error = new WaypointProcessingException(
                    $"{____locomotive.Ident} didn't stand at the waypoint the way it was set. {notice} Fix the switches or the waypoint, then resume to send it there again.",
                    waypoint);
                ModLocator.ServiceProvider.GetService<WaypointResolver>().HandleProcessingError(ErrorType, error, waypoint);
            }
            catch (Exception e)
            {
                ModLocator.Loader.LogError($"Waypoint mismatch handling failed: {e}");
            }
        }
    }
}
