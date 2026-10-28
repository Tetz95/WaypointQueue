using Game.Messages;
using Game.State;
using Helpers;
using Model;
using Model.Definition;
using Model.Definition.Data;
using Model.Ops;
using Model.Ops.Definition;
using RollingStock;
using System;
using System.Collections.Generic;
using System.Linq;
using Track;
using UI.EngineControls;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.UUM;
using WaypointQueue.Wrappers;
using static Model.Car;

namespace WaypointQueue.Services
{
    internal class RefuelService(ICarService carService, AutoEngineerService autoEngineerService, IOpsControllerWrapper opsControllerWrapper)
    {
        private List<CarLoadTargetLoader> _carLoadTargetLoaders = [];
        private List<CarLoaderSequencer> _carLoaderSequencers = [];

        private readonly Dictionary<string, float> _cachedCarLoadQuantityByFuelCarId = [];

        // Modded loaders (e.g. AlinasMapMod LoaderBuilder splineys built by RailForge) can be created
        // after PropertiesDidRestore, so the collections are refreshed when they may be stale.
        private const float StaleCollectionsSeconds = 10f;
        private float _lastRebuildTime = float.NegativeInfinity;

        public void RebuildCollections()
        {
            _carLoadTargetLoaders.Clear();
            _carLoaderSequencers.Clear();

            _carLoadTargetLoaders = [.. UnityEngine.Object.FindObjectsOfType<CarLoadTargetLoader>()];
            _carLoaderSequencers = [.. UnityEngine.Object.FindObjectsOfType<CarLoaderSequencer>()];
            _lastRebuildTime = Time.realtimeSinceStartup;
        }

        private void RebuildCollectionsIfStale()
        {
            if (Time.realtimeSinceStartup - _lastRebuildTime > StaleCollectionsSeconds
                || _carLoadTargetLoaders.Any(l => l == null)
                || _carLoaderSequencers.Any(s => s == null))
            {
                RebuildCollections();
            }
        }

        public void OrderToRefuel(ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper, out List<BaseLocomotive> locosForRefuelList)
        {
            Loader.Log($"Beginning order to refuel {waypoint.Locomotive.Ident} for waypoint");

            BaseLocomotive locoForRefuel = null;
            // Only populate the refuel loco id queue if it is currently empty
            if (waypoint.RefuelLocoIdsQueue.Count > 0)
            {
                locoForRefuel = carService.GetLocoById(waypoint.RefuelLocoIdsQueue.First());
                locosForRefuelList = [waypoint.Locomotive];
            }
            else
            {
                locosForRefuelList = waypoint.EnableMultipleRefueling
                    ? GetCoupledLocosForRefuel(waypoint.Locomotive, waypoint.RefuelLoadName, waypoint.Location)
                    : [waypoint.Locomotive];

                waypoint.RefuelLocoIdsQueue = locosForRefuelList.Select(l => l.id).ToList();
                locoForRefuel = locosForRefuelList.First();
            }

            waypoint.CurrentlyRefueling = true;

            waypoint.MaxSpeedAfterRefueling = ordersHelper.Orders.MaxSpeedMph;
            // Set speed limit to help prevent train from overrunning waypoint
            int speedWhileRefueling = waypoint.RefuelingSpeedLimit;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: speedWhileRefueling, null, null);

            // Make sure AE knows how many cars in case we coupled just before this
            carService.UpdateCarsForAE(waypoint.Locomotive as BaseLocomotive);

            OrderLocomotiveToRefuel(locoForRefuel, waypoint, ordersHelper);
        }

        public void OrderLocomotiveToRefuel(BaseLocomotive locoForRefuel, ManagedWaypoint waypoint, AutoEngineerOrdersHelper ordersHelper)
        {
            Location locationToMove = GetRefuelLocation(locoForRefuel, waypoint);
            _cachedCarLoadQuantityByFuelCarId.Remove(locoForRefuel.id);

            Loader.Log($"Sending refueling waypoint for {waypoint.Locomotive.Ident} to {locationToMove} to refuel {locoForRefuel.Ident}");
            waypoint.StatusLabel = $"Moving to refuel {waypoint.RefuelLoadName}";
            waypoint.OverwriteLocation(locationToMove);
            waypoint.StopAtWaypoint = true;
            WaypointQueueController.Shared.UpdateWaypoint(waypoint);
            autoEngineerService.SendToPosedMark(ordersHelper, locationToMove);
        }

        private List<BaseLocomotive> GetCoupledLocosForRefuel(BaseLocomotive locomotive, string refuelLoadName, Location location)
        {
            carService.GetTrainEndLocations(locomotive, location, out _, out _, out _, out LogicalEnd closestEnd, out LogicalEnd furthestEnd);

            if (refuelLoadName == "water" || refuelLoadName == "coal")
            {
                return [.. locomotive.EnumerateCoupled(closestEnd).Where(c => c.Archetype == CarArchetype.LocomotiveSteam).Cast<BaseLocomotive>()];
            }
            if (refuelLoadName == "diesel-fuel")
            {
                return [.. locomotive.EnumerateCoupled(closestEnd).Where(c => c.Archetype == CarArchetype.LocomotiveDiesel).Cast<BaseLocomotive>()];
            }
            return [];
        }

        // Where the train's leading end has to stop for the fuel car's slot to sit at the loader, facing outward from the
        // train (the train behind it). The slot has to travel from where it is to the loader; the train end on that side
        // travels the same distance. 2026.1's engineer counts a plain waypoint reached as soon as it lies under the train,
        // and a refuel mark nearly always does, so the mark goes out as a posed waypoint (SendToPosedMark).
        private Location GetRefuelLocation(BaseLocomotive locomotive, ManagedWaypoint waypoint)
        {
            Car fuelCar = GetFuelCar(locomotive);

            Vector3 loadSlotPosition = GetFuelCarLoadSlotPosition(fuelCar, waypoint, out float slotMaxCapacity);
            waypoint.RefuelMaxCapacity = slotMaxCapacity;

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new RefuelException($"Failed to get track graph location from refuel game point {waypoint.RefuelPoint}.", waypoint);
            }
            if (!Graph.Shared.TryGetLocationFromGamePoint(loadSlotPosition, 10f, out Location slotLocation))
            {
                throw new RefuelException($"Failed to get track graph location for {fuelCar.Ident}'s {waypoint.RefuelLoadName} slot at {loadSlotPosition}.", waypoint);
            }

            (Location closestTrainEndLocation, Location furthestTrainEndLocation) = carService.GetTrainEndLocations(locomotive, targetLoaderLocation, out _, out _, out _, out _, out _);

            float distanceToMove = Graph.Shared.GetDistanceBetweenClose(slotLocation, targetLoaderLocation);
            TrackSegment.End towardLoader = Graph.Shared.LocationOrientedToward(slotLocation, targetLoaderLocation).end;

            // The leading end: the train end that lies the way the slot has to go (if neither does, the slot is beyond the
            // train's end on that side: the end nearest the slot).
            Location leadingEnd;
            if (Graph.Shared.LocationOrientedToward(slotLocation, closestTrainEndLocation).end == towardLoader)
            {
                leadingEnd = closestTrainEndLocation;
            }
            else if (Graph.Shared.LocationOrientedToward(slotLocation, furthestTrainEndLocation).end == towardLoader)
            {
                leadingEnd = furthestTrainEndLocation;
            }
            else
            {
                leadingEnd = Graph.Shared.GetDistanceBetweenClose(slotLocation, closestTrainEndLocation) <= Graph.Shared.GetDistanceBetweenClose(slotLocation, furthestTrainEndLocation)
                    ? closestTrainEndLocation
                    : furthestTrainEndLocation;
            }

            Location outward = Graph.Shared.LocationOrientedToward(leadingEnd, slotLocation).Flipped();
            Loader.LogDebug($"Refuel: {fuelCar.Ident}'s {waypoint.RefuelLoadName} slot is {distanceToMove:0.00} m from the loader; moving the train end at {leadingEnd} that far");
            return Graph.Shared.LocationByMoving(outward, distanceToMove, false, true);
        }

        private Vector3 GetFuelCarLoadSlotPosition(Car fuelCar, ManagedWaypoint waypoint, out float maxCapacity)
        {
            int loadSlotIndex = FuelSlotIndex(fuelCar, waypoint.RefuelLoadName);
            if (loadSlotIndex < 0)
            {
                throw new RefuelException($"{fuelCar.Ident} has no {waypoint.RefuelLoadName} load slot.", waypoint);
            }
            maxCapacity = FuelSlotCapacity(fuelCar, loadSlotIndex);

            List<CarLoadTarget> carLoadTargets = fuelCar.GetComponentsInChildren<CarLoadTarget>().ToList();
            CarLoadTarget loadTarget = carLoadTargets.Find(clt => clt.slotIndex == loadSlotIndex);

            Vector3 loadSlotPosition = CalculatePositionFromLoadTarget(fuelCar, loadTarget);

            return loadSlotPosition;
        }

        // Where the slot stopped relative to the loader, for the log: the loader fills only a slot within its radius.
        public void LogSlotAlignment(ManagedWaypoint waypoint)
        {
            try
            {
                BaseLocomotive loco = waypoint.RefuelLocoIdsQueue.Count > 0 ? carService.GetLocoById(waypoint.RefuelLocoIdsQueue.First()) : waypoint.Locomotive;
                Car fuelCar = GetFuelCar(loco);
                Vector3 slot = GetFuelCarLoadSlotPosition(fuelCar, waypoint, out _);
                Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location loader);
                Graph.Shared.TryGetLocationFromGamePoint(slot, 10f, out Location slotLocation);
                float along = loader.IsValid && slotLocation.IsValid ? Graph.Shared.GetDistanceBetweenClose(slotLocation, loader) : -1f;
                (Location closest, Location furthest) = carService.GetTrainEndLocations(loco, loader, out _, out _, out _, out _, out _);
                Loader.Log($"Refuel: {fuelCar.Ident} stopped with its {waypoint.RefuelLoadName} slot {Vector3.Distance(slot.ZeroY(), waypoint.RefuelPoint.ZeroY()):0.00} m from the loader ({along:0.00} m along the track); train ends {closest} (nearest the loader) and {furthest}");
            }
            catch (Exception e)
            {
                Loader.Log($"Refuel: couldn't measure the slot's alignment: {e.Message}");
            }
        }

        // The fuel car's slot for a refuel load name ("water"); 2026.1 slots name the load by id ("rr/water"). -1 if none.
        private static int FuelSlotIndex(Car fuelCar, string refuelLoadName)
        {
            return fuelCar.Definition.LoadSlots.FindIndex(slot => Compat2026.LoadName(slot.RequiredLoadIdentifier) == refuelLoadName);
        }

        // What the slot holds when full, as the game's loader works it out (volume times density can cap coal).
        private static float FuelSlotCapacity(Car fuelCar, int slotIndex)
        {
            LoadSlot slot = fuelCar.Definition.LoadSlots[slotIndex];
            return slot.CalculateMaximumCapacity(slot.RequiredLoadIdentifier, fuelCar);
        }

        private Vector3 CalculatePositionFromLoadTarget(Car fuelCar, CarLoadTarget loadTarget)
        {
            // This logic is based on CarLoadTargetLoader.LoadSlotFromCar
            Matrix4x4 transformMatrix = fuelCar.GetTransformMatrix(Graph.Shared);
            Vector3 point2 = fuelCar.transform.InverseTransformPoint(loadTarget.transform.position);
            Vector3 vector = transformMatrix.MultiplyPoint3x4(point2);
            return vector;
        }

        private bool IsTargetBetween(Vector3 target, Vector3 positionA, Vector3 positionB, bool inclusive = false)
        {
            // If target is in the middle, the distance between either end to the target will always be less than the length from one end to the other
            float distanceAToTarget = Vector3.Distance(positionA.ZeroY(), target.ZeroY());

            if (inclusive && distanceAToTarget == 0)
            {
                return true;
            }

            float distanceBToTarget = Vector3.Distance(positionB.ZeroY(), target.ZeroY());

            if (inclusive && distanceBToTarget == 0)
            {
                return true;
            }

            float distanceAtoB = Vector3.Distance(positionA.ZeroY(), positionB.ZeroY());

            if (distanceAToTarget < distanceAtoB && distanceBToTarget < distanceAtoB)
            {
                return true;
            }
            return false;
        }

        private List<string> GetValidLoadsForWaypoint(ManagedWaypoint waypoint)
        {
            BaseLocomotive locomotive = waypoint.Locomotive;

            if (locomotive == null)
            {
                return ["water", "coal", "diesel-fuel"];
            }
            if (locomotive.Archetype == CarArchetype.LocomotiveSteam)
            {
                return ["water", "coal"];
            }
            if (locomotive.Archetype == CarArchetype.LocomotiveDiesel)
            {
                return ["diesel-fuel"];
            }
            return null;
        }

        public void CheckNearbyFuelLoaders(ManagedWaypoint waypoint)
        {
            List<string> validLoads = GetValidLoadsForWaypoint(waypoint);

            CarLoadTargetLoader closestLoader = null;
            float shortestDistance = 0;

            RebuildCollectionsIfStale();

            foreach (CarLoadTargetLoader targetLoader in _carLoadTargetLoaders)
            {
                if (targetLoader == null || !validLoads.Contains(targetLoader.load?.name?.ToLower()))
                {
                    continue;
                }

                bool hasLocation = false;
                Location loaderLocation;
                try
                {
                    hasLocation = Graph.Shared.TryGetLocationFromWorldPoint(targetLoader.transform.position, 10f, out loaderLocation);
                }
                catch (NullReferenceException)
                {
                    continue;
                }

                if (hasLocation)
                {
                    float distanceFromWaypointToLoader = Vector3.Distance(waypoint.Location.GetPosition(), loaderLocation.GetPosition());

                    float radiusToSearch = 5f;

                    if (distanceFromWaypointToLoader < radiusToSearch)
                    {
                        Loader.LogDebug($"Found {targetLoader.load.name} loader within {distanceFromWaypointToLoader}");
                        if (closestLoader == null)
                        {
                            shortestDistance = distanceFromWaypointToLoader;
                            closestLoader = targetLoader;
                        }
                        if (distanceFromWaypointToLoader < shortestDistance)
                        {
                            shortestDistance = distanceFromWaypointToLoader;
                            closestLoader = targetLoader;
                        }
                    }
                }
            }

            if (closestLoader != null)
            {
                Loader.Log($"Using loader named \"{closestLoader.load.name}\" with key value registered id {closestLoader.keyValueObject.RegisteredId} loader at {closestLoader.transform?.position}");
                Vector3 loaderPosition = WorldTransformer.WorldToGame(closestLoader.transform.position);
                // CarLoadTargetLoader uses game position for loading logic, not graph Location
                waypoint.SerializableRefuelPoint = new SerializableVector3(loaderPosition.x, loaderPosition.y, loaderPosition.z);
                // Water towers will have a null source industry
                waypoint.RefuelIndustryId = closestLoader.sourceIndustry?.identifier;
                waypoint.RefuelLoaderRegisteredId = closestLoader.keyValueObject.RegisteredId;
                waypoint.RefuelLoadName = closestLoader.load.name;
                waypoint.WillRefuel = true;
            }
        }

        private Car GetFuelCar(BaseLocomotive locomotive)
        {
            Car fuelCar = locomotive;
            if (locomotive.Archetype == CarArchetype.LocomotiveSteam)
            {
                fuelCar = PatchSteamLocomotive.FuelCar(locomotive);
            }
            return fuelCar;
        }

        public bool IsLocoFull(BaseLocomotive locomotive, string refuelLoadName, float refuelMaxCapacity, out bool isIncreasing)
        {
            Car fuelCar = GetFuelCar(locomotive);
            int slotIndex = FuelSlotIndex(fuelCar, refuelLoadName);
            CarLoadInfo? carLoadInfo = slotIndex < 0 ? null : fuelCar.GetLoadInfo(slotIndex);
            isIncreasing = false;

            if (!carLoadInfo.HasValue)
            {
                Loader.LogError($"Locomotive {locomotive.Ident} had a null CarLoadInfo");
                return true;
            }

            if (_cachedCarLoadQuantityByFuelCarId.TryGetValue(locomotive.id, out var oldQuantity) && oldQuantity < carLoadInfo.Value.Quantity)
            {
                isIncreasing = true;
            }
            _cachedCarLoadQuantityByFuelCarId[locomotive.id] = carLoadInfo.Value.Quantity;

            // As the game's loader decides it: coal (pounds, with a density) can be capped by the tender's volume below the
            // slot's authored maximum, and the loader stops there.
            float capacity = FuelSlotCapacity(fuelCar, slotIndex);

            bool isFullVanilla = capacity <= 0f || carLoadInfo.Value.Quantity / capacity > 0.999f;

            if (isFullVanilla)
            {
                Loader.Log($"Fuel car {fuelCar.Ident} is full");
            }
            return isFullVanilla;
        }

        public bool IsLoaderEmpty(string industryId, string refuelLoadName)
        {
            if (!opsControllerWrapper.TryGetIndustryById(industryId, out Industry industry))
            {
                // Water towers are unlimited and have a null industry
                return false;
            }

            if (industry.Storage == null)
            {
                Loader.Log($"Storage is null for {industry.name}");
                return true;
            }

            Load matchingLoad = industry.Storage.Loads().ToList().Find(l => l.name == refuelLoadName);

            if (matchingLoad == null)
            {
                Loader.Log($"Industry {industry.name} is empty, did not find matching load for {refuelLoadName}");
                return true;
            }

            if (industry.Storage.QuantityInStorage(matchingLoad) <= 0f)
            {
                Loader.Log($"Industry {industry.name} is empty, quantity in storage was zero");
                return true;
            }
            return false;
        }

        public void CleanupAfterRefuel(ManagedWaypoint wp, AutoEngineerOrdersHelper ordersHelper)
        {
            Loader.Log($"Done refueling {wp.Locomotive.Ident}");
            wp.WillRefuel = false;
            wp.CurrentlyRefueling = false;
            SetCarLoaderSequencerWantsLoading(wp, false);
            WaypointQueueController.Shared.UpdateWaypoint(wp);

            int maxSpeed = wp.MaxSpeedAfterRefueling;
            if (maxSpeed == 0) maxSpeed = 35;
            ordersHelper.SetOrdersValue(null, null, maxSpeedMph: maxSpeed, null, null);
        }

        public void SetCarLoaderSequencerWantsLoading(ManagedWaypoint waypoint, bool value)
        {
            CarLoadTargetLoader loaderTarget = FindCarLoadTargetLoader(waypoint);
            if (loaderTarget == null)
            {
                RebuildCollections();
                loaderTarget = FindCarLoadTargetLoader(waypoint);
            }
            if (loaderTarget == null)
            {
                Loader.LogError($"_carLoadTargetLoaders: {String.Join("-", _carLoadTargetLoaders.Select(c => $"[{c.keyValueObject.RegisteredId}, {waypoint.RefuelLoadName} loader]"))}");
                var message = $"Cannot find valid CarLoadTargetLoader for \"{waypoint.RefuelLoadName}\" at point {waypoint.RefuelPoint} with registered id {waypoint.RefuelLoaderRegisteredId}.";
                Loader.LogError(message);
                if (value)
                {
                    // Only throw if we are trying to activate the loader. If we're just cleaning up then it can fail silently.
                    throw new RefuelException(message, waypoint);
                }
                return;
            }
            CarLoaderSequencer sequencer = _carLoaderSequencers.Find(x => x != null && x.keyValueObject.RegisteredId == loaderTarget.keyValueObject.RegisteredId);
            if (sequencer != null)
            {
                StateManager.ApplyLocal(new PropertyChange(sequencer.keyValueObject.RegisteredId, sequencer.readWantsLoadingKey, new BoolPropertyValue(value)));
            }
            else
            {
                Loader.LogError($"_carLoaderSequencers: {String.Join("-", _carLoaderSequencers.Select(c => $"[{c.keyValueObject.RegisteredId}, {waypoint.RefuelLoadName} sequencer]"))}");
                var message = $"Cannot find valid CarLoaderSequencer for loader target {loaderTarget.name} with load \"{waypoint.RefuelLoadName}\" at point {waypoint.RefuelPoint} with registered id {loaderTarget.keyValueObject.RegisteredId}.";
                Loader.LogError(message);
                if (value)
                {
                    // Only throw if we are trying to activate the loader. If we're just cleaning up then it can fail silently.
                    throw new RefuelException(message, waypoint);
                }
            }
        }

        private CarLoadTargetLoader FindCarLoadTargetLoader(ManagedWaypoint waypoint)
        {
            CarLoadTargetLoader loader = null;
            // First try to find by registered id
            if (!String.IsNullOrEmpty(waypoint.RefuelLoaderRegisteredId))
            {
                loader = _carLoadTargetLoaders.Find(l => l != null && l.keyValueObject.RegisteredId == waypoint.RefuelLoaderRegisteredId);
            }

            // If that fails, then try to find by position
            if (loader == null)
            {
                Vector3 worldPosition = WorldTransformer.GameToWorld(waypoint.RefuelPoint);
                //Loader.LogDebug($"Starting search for target loader matching world point {worldPosition}");
                loader = _carLoadTargetLoaders.Find(l => l != null && l.transform.position == worldPosition);
                //Loader.LogDebug($"Found matching {loader.load.name} loader at game point {waypoint.RefuelPoint}");
            }

            return loader;
        }

        public void RepositionToRetryRefueling(ManagedWaypoint waypoint, BaseLocomotive currentRefuelLoco, AutoEngineerOrdersHelper ordersHelper)
        {
            _cachedCarLoadQuantityByFuelCarId.Remove(currentRefuelLoco.id);

            BaseLocomotive locomotive = waypoint.Locomotive;

            if (!Graph.Shared.TryGetLocationFromGamePoint(waypoint.RefuelPoint, 10f, out Location targetLoaderLocation))
            {
                throw new RefuelException($"Failed to get track graph location from refuel game point {waypoint.RefuelPoint}.", waypoint);
            }

            (Location closestTrainEndLocation, Location furthestTrainEndLocation) = carService.GetTrainEndLocations(locomotive, targetLoaderLocation, out _, out _, out _, out _, out _);

            // The far end, 10 m further out: the train backs away from the loader to come at it again.
            Location outwardFurthestEnd = Graph.Shared.LocationOrientedToward(furthestTrainEndLocation, targetLoaderLocation).Flipped();

            float distanceToMoveAwayFromLoader = 10;
            Location locationToMove = Graph.Shared.LocationByMoving(outwardFurthestEnd, distanceToMoveAwayFromLoader, false, true);

            Loader.Log($"Sending retry refueling waypoint for {waypoint.Locomotive.Ident} to {locationToMove} to refuel {currentRefuelLoco.Ident}");
            waypoint.StatusLabel = $"Moving to refuel {waypoint.RefuelLoadName}";
            waypoint.OverwriteLocation(locationToMove);
            waypoint.StopAtWaypoint = true;
            autoEngineerService.SendToPosedMark(ordersHelper, locationToMove);
        }
    }
}
