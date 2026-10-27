using HarmonyLib;
using Model;
using Model.AI;
using System.Collections.Generic;
using UI.Common;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.State;
using WaypointQueue.UI;
using WaypointQueue.UUM;
using Location = Track.Location;
using Graph = Track.Graph;

namespace WaypointQueue
{
    [HarmonyPatch(typeof(AutoEngineerOrdersHelper))]
    internal class PatchAutoEngineerOrdersHelper
    {
        // 2026.1: the picker hands over a whole OrderWaypoint, through SetWaypoint in waypoint mode or SetWaypointOrder
        // (which also switches the engineer to waypoint mode). The queue keeps its location and the car to couple to;
        // the pose the picker chose (arriving end, facing, turns) isn't kept yet.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.SetWaypoint))]
        static bool SetWaypointPrefix(OrderWaypoint waypoint, ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            if (GamePickerRedirect.TryTake(waypoint))
            {
                return false;
            }
            Location location = Graph.Shared.ResolveLocationString(waypoint.LocationString);
            _pickedPose = QueuedPose.From(waypoint, GamePickerRedirect.PoseChosen);
            try
            {
                return SetWaypointPrefix(location, waypoint.CoupleToCarId, ref ____locomotive, ref ____persistence);
            }
            finally
            {
                _pickedPose = null;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.SetWaypointOrder))]
        static bool SetWaypointOrderPrefix(AutoEngineerOrdersHelper __instance, OrderWaypoint waypoint, int? maxSpeedMph, ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            if (GamePickerRedirect.TryTake(waypoint))
            {
                return false;
            }
            __instance.SetOrdersValue(Game.Messages.AutoEngineerMode.Waypoint, null, maxSpeedMph, null, null);
            return SetWaypointPrefix(waypoint, ref ____locomotive, ref ____persistence);
        }

        // The pose of the waypoint being added, while SetWaypointPrefix runs (the overwrite question captures it).
        private static QueuedPose _pickedPose;

        static bool SetWaypointPrefix(Location location, string coupleToCarId, ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            QueuedPose pose = _pickedPose;
            //Loader.LogDebug($"SetWaypoint prefix");

            OrderWaypoint? existingWaypoint = ____persistence.Orders.Waypoint;
            bool isAppendingWaypoint = Input.GetKey(Loader.Settings.queuedWaypointModeKey.keyCode);
            bool isReplacingWaypoint = Input.GetKey(Loader.Settings.replaceWaypointModeKey.keyCode);
            bool isInsertingNext = Input.GetKey(Loader.Settings.insertNextWaypointModeKey.keyCode);

            bool isAnyModifierPressed = isAppendingWaypoint || isReplacingWaypoint || isInsertingNext;
            //Loader.LogDebug($"Appending: {isAppendingWaypoint}, Replacing {isReplacingWaypoint}, Inserting {isInsertingNext}");

            if (!isAnyModifierPressed && Loader.Settings.MakeAppendDefaultWithoutHotkeybinding)
            {
                isAppendingWaypoint = true;
                isAnyModifierPressed = true;
            }

            BaseLocomotive loco = (BaseLocomotive)____locomotive;

            // Setting a waypoint without one of the modifiers will reset the locomotive's waypoint list
            if (!isAnyModifierPressed)
            {
                LocoWaypointState state = ModStateManager.Shared.GetLocoWaypointState(loco.id);
                List<ManagedWaypoint> waypoints = state.Waypoints;
                if (waypoints.Count > 1)
                {
                    ModalAlertController.Present($"{____locomotive.Ident} already has {waypoints.Count} waypoints.", "Are you sure you would like to overwrite the queue?\n\nThis cannot be undone.",
                    [
                        (true, "Overwrite"),
                        (false, "Cancel")
                    ], delegate (bool b)
                    {
                        if (b)
                        {
                            ModStateManager.Shared.RemoveLocoWaypointState(loco.id);
                            HandleAddingWaypoint(existingWaypoint, loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext, pose);
                        }
                    });
                }
                else
                {
                    ModStateManager.Shared.RemoveLocoWaypointState(____locomotive.id);
                    HandleAddingWaypoint(existingWaypoint, loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext, pose);
                }
            }
            else
            {
                HandleAddingWaypoint(existingWaypoint, loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext, pose);
            }

            // Skip original since we need to manage waypoints to keep track of any orders that need to be resolved
            return false;
        }

        private static void HandleAddingWaypoint(OrderWaypoint? existingWaypoint, BaseLocomotive loco, Location location, string coupleToCarId, bool isReplacingWaypoint, bool isInsertingNext, QueuedPose pose)
        {
            if (existingWaypoint != null)
            {
                //Loader.LogDebug($"Current waypoint for {loco.Ident} is {existingWaypoint.Value.LocationString}");
            }

            // Always add the waypoint to the queue
            if (location != null)
            {
                WaypointQueueController.Shared.AddWaypoint(loco, location, coupleToCarId, isReplacingWaypoint, isInsertingNext, pose);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AutoEngineerOrdersHelper.ClearWaypoint))]
        static bool ClearWaypointPrefix(ref Car ____locomotive, ref AutoEngineerPersistence ____persistence)
        {
            WaypointResolver resolver = Loader.ServiceProvider.GetService<WaypointResolver>();
            if (WaypointQueueController.Shared.TryGetActiveWaypointFor((BaseLocomotive)____locomotive, out ManagedWaypoint waypoint))
            {
                resolver.CleanupBeforeRemovingWaypoint(waypoint);
            }
            return true;
        }
    }
}
