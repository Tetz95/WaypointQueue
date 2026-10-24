using Game.Messages;
using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Reflection;
using Track;
using UI.EngineControls;
using WaypointQueue.UUM;

namespace WaypointQueue.Services
{
    internal class AutoEngineerService(ICarService carService)
    {
        public void SendToWaypoint(AutoEngineerOrdersHelper ordersHelper, Location location, string coupleToCarId = null)
        {
            (Location, string)? maybeWaypoint = (InsideSegment(location), coupleToCarId);
            ordersHelper.SetOrdersValue(null, null, null, null, maybeWaypoint);
        }

        // The order keeps its waypoint as text with the distance rounded to 1 mm, so a waypoint on the very end of a
        // segment could round past it (47.088 on a 47.08782 m segment): the game then failed to route there on every
        // try and the train never set off. A centimetre inside the segment survives the rounding.
        private const float SegmentEndMargin = 0.01f;

        internal static Location InsideSegment(Location location)
        {
            Location clamped = location.Clamped();
            if (clamped.segment == null)
            {
                return clamped;
            }
            float length = clamped.segment.GetLength();
            if (length <= 2f * SegmentEndMargin)
            {
                return clamped;
            }
            float distance = Math.Min(Math.Max(clamped.distance, SegmentEndMargin), length - SegmentEndMargin);
            return new Location(clamped.segment, distance, clamped.end);
        }

        public void SendToWaypoint(BaseLocomotive loco, Location location, string coupleToCarId = null)
        {
            var ordersHelper = GetOrdersHelper(loco);
            SendToWaypoint(ordersHelper, location, coupleToCarId);
        }

        public AutoEngineerOrdersHelper GetOrdersHelper(BaseLocomotive locomotive)
        {
            AutoEngineerPersistence persistence = Traverse.Create(locomotive.AutoEngineerPlanner).Field("_persistence").GetValue<AutoEngineerPersistence>();
            AutoEngineerOrdersHelper ordersHelper = new(locomotive, persistence);
            return ordersHelper;
        }

        public string GetPlannerStatus(BaseLocomotive locomotive)
        {
            Type plannerType = typeof(AutoEngineerPlanner);
            FieldInfo fieldInfo = plannerType.GetField("_persistence", BindingFlags.NonPublic | BindingFlags.Instance);
            AutoEngineerPersistence persistence = (AutoEngineerPersistence)fieldInfo.GetValue((locomotive as BaseLocomotive).AutoEngineerPlanner);
            return persistence.PlannerStatus;
        }

        public void CancelActiveOrders(BaseLocomotive loco)
        {
            Loader.Log($"Canceling active orders for {loco.Ident}");
            AutoEngineerOrdersHelper ordersHelper = GetOrdersHelper(loco);

            if (ordersHelper.Mode == AutoEngineerMode.Waypoint)
            {
                ordersHelper.ClearWaypoint();
            }
        }

        public (Location location, string coupleToCarId) GetCurrentOrderWaypoint(BaseLocomotive loco)
        {
            var ordersHelper = GetOrdersHelper(loco);
            OrderWaypoint? waypoint = ordersHelper.Orders.Waypoint;
            if (waypoint.HasValue)
            {
                OrderWaypoint valueOrDefault = waypoint.GetValueOrDefault();
                Location resolvedLocation = Graph.Shared.ResolveLocationString(valueOrDefault.LocationString);
                return (resolvedLocation, valueOrDefault.CoupleToCarId);
            }

            return (Location.Invalid, "");
        }

        public bool HasActiveWaypoint(AutoEngineerOrdersHelper ordersHelper)
        {
            return ordersHelper.Orders.Waypoint.HasValue;
        }

        public bool IsInWaypointMode(AutoEngineerOrdersHelper ordersHelper)
        {
            return ordersHelper.Orders.Mode == Game.Messages.AutoEngineerMode.Waypoint;
        }

        public bool AtEndOfTrack(BaseLocomotive loco)
        {
            string plannerStatus = GetPlannerStatus(loco);
            return plannerStatus == "End of Track";
        }

        public bool IsNearWaypoint(ManagedWaypoint waypoint)
        {
            (Location closestTrainEnd, Location _) = carService.GetTrainEndLocations(waypoint.Locomotive, waypoint.Location, out float closestDistance, out _, out _, out _, out _);
            // The straight-line distance alone would count a train on a parallel track as near
            return closestDistance < 10 && Graph.Shared.CheckSameRoute(closestTrainEnd, waypoint.Location, 10f);
        }

        public int GetOrdersMaxSpeed(BaseLocomotive locomotive)
        {
            var ordersHelper = GetOrdersHelper(locomotive);
            return ordersHelper.Orders.MaxSpeedMph;
        }
    }
}
