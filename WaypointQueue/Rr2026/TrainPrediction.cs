using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Model;
using Model.AI;
using Track;
using Track.Search;
using UnityEngine;
using WaypointQueue.Model;
using WaypointQueue.Services;
using WaypointQueue.State;

namespace WaypointQueue
{
    // The train as it will be when it reaches a queued waypoint: the cars the earlier waypoints couple and cut, and the
    // ends it stands with at each of them. Railroader 2026.1's picker draws the train's footprint at the mark and names
    // its poses by the train's ends ("back of train on the mark"); for a waypoint further down the queue that has to be
    // the train after the earlier couplings and cuts, not the train standing in the yard now.
    //
    // Each earlier waypoint is a leg: the end that arrives first is the pose's end where the player chose one, or the
    // leading end of the auto engineer's own route search from where the train stands after the leg before (the same
    // search the planner runs, given the predicted train's ends and length). A coupling adds the coupled car's whole
    // cut at that end; cuts follow WaypointQueue's own rules (UncouplingService). What can't be known ahead (the nearest
    // car to couple to, a cut that takes the uncoupled cars on) leaves the prediction marked approximate.
    internal sealed class TrainPrediction
    {
        // Consist end A to B, as the picker's _consist (A is the locomotive's logical A side).
        public List<Car> Cars { get; private set; } = [];

        public float Length => AutoEngineerPlanner.CalculateTotalLength(Cars);

        // Why the prediction may be off, one note per guess; empty when every step was known.
        public List<string> Guesses { get; } = [];

        public bool Changed { get; private set; }

        // The outer end of the train at consist end A, facing out of the train (toward A), and at end B (toward B).
        private Location _endA;
        private Location _endB;

        private readonly IGraphView _graph = Graph.Shared.Current;

        public static TrainPrediction ForQueueIndex(BaseLocomotive locomotive, int index)
        {
            var prediction = new TrainPrediction();
            prediction.Start(locomotive);
            List<ManagedWaypoint> queue = ModStateManager.Shared.GetLocoWaypointState(locomotive.id)?.Waypoints ?? [];
            for (int i = 0; i < Math.Min(index, queue.Count); i++)
            {
                try
                {
                    prediction.Apply(queue[i]);
                }
                catch (Exception e)
                {
                    ModLocator.Loader.LogDebug($"Train prediction for {locomotive.Ident} stopped at waypoint {i}: {e.Message}");
                    prediction.Guesses.Add("couldn't follow the queue past waypoint " + (i + 1));
                    break;
                }
            }
            return prediction;
        }

        private void Start(BaseLocomotive locomotive)
        {
            Cars = [.. locomotive.EnumerateCoupled(Car.LogicalEnd.A)];
            Car first = Cars[0], last = Cars[Cars.Count - 1];
            _endA = OutwardEnd(first, Cars.Count > 1 ? Cars[1] : null);
            _endB = OutwardEnd(last, Cars.Count > 1 ? Cars[Cars.Count - 2] : null);
        }

        // A car's end that isn't coupled to its neighbour in the train, as a location facing out of the train.
        private static Location OutwardEnd(Car car, Car neighbour)
        {
            Car.LogicalEnd outer = neighbour != null && car.CoupledTo(Car.LogicalEnd.A) == neighbour ? Car.LogicalEnd.B : Car.LogicalEnd.A;
            // A car's LocationA and LocationB both face toward its end A.
            return outer == Car.LogicalEnd.A ? car.LocationA : car.LocationB.Flipped();
        }

        private void Apply(ManagedWaypoint wp)
        {
            ConsistEnd arriving = ArriveAt(wp);

            Car coupleTo = CouplingTarget(wp);
            if (coupleTo != null && !Cars.Contains(coupleTo))
            {
                Couple(coupleTo, arriving, wp);
            }

            if (wp.HasAnyUncouplingOrders && !wp.HasAnyCouplingOrders)
            {
                Cut(wp, arriving);
            }
        }

        // Moves the predicted train to the waypoint and returns the end that arrives on the mark.
        private ConsistEnd ArriveAt(ManagedWaypoint wp)
        {
            Location mark = wp.Location;
            QueuedPose pose = wp.Pose;
            if (pose != null && pose.IsPosed && pose.ArrivingEnd != 0 && pose.BodyToward != 0)
            {
                ConsistEnd posedEnd = pose.ArrivingEnd == (int)WaypointEnd.A ? ConsistEnd.A : ConsistEnd.B;
                Location intoBody = WaypointPose.IntoBody(mark, WaypointPose.ToSegmentEnd((BodyToward)pose.BodyToward));
                StandAt(posedEnd, intoBody);
                return posedEnd;
            }

            if (TryRoute(mark, out List<RouteSearch.Step> route))
            {
                ConsistEnd leading = route[route.Count - 1].LeadingEnd;
                StandAt(leading, IntoBodyFromRoute(mark, route));
                return leading;
            }

            // No route found: the end nearer the mark, standing where it is.
            Guesses.Add("no route found to " + DescribeMark(wp));
            Vector3 markPosition = _graph.GetPosition(mark);
            ConsistEnd nearer = (_graph.GetPosition(_endA) - markPosition).sqrMagnitude <= (_graph.GetPosition(_endB) - markPosition).sqrMagnitude ? ConsistEnd.A : ConsistEnd.B;
            return nearer;
        }

        // The auto engineer's route from either end of the predicted train, as the planner searches (it starts at the
        // leading end and may turn back); the shorter one is the one it would take.
        private bool TryRoute(Location mark, out List<RouteSearch.Step> best)
        {
            best = null;
            float bestDistance = float.MaxValue;
            foreach ((Location start, ConsistEnd startEnd) in new[] { (_endA, ConsistEnd.A), (_endB, ConsistEnd.B) })
            {
                var steps = new List<RouteSearch.Step>();
                if (_graph.FindRoute(start, mark, HeuristicCosts.AutoEngineer, steps, out RouteSearch.Metrics metrics,
                        checkForCars: false, trainLength: Length, startEnd: startEnd) && steps.Count > 0 && metrics.Distance < bestDistance)
                {
                    best = steps;
                    bestDistance = metrics.Distance;
                }
            }
            return best != null;
        }

        // The mark, facing into the train's body as it stands after the route: toward the side it came in from.
        private Location IntoBodyFromRoute(Location mark, List<RouteSearch.Step> route)
        {
            Vector3 markPosition = _graph.GetPosition(mark);
            Vector3 from = route[0].Position;
            for (int i = route.Count - 1; i >= 0; i--)
            {
                if (Vector3.Distance(route[i].Position, markPosition) >= 5f)
                {
                    from = route[i].Position;
                    break;
                }
            }
            Location towardA = WaypointPose.IntoBody(mark, TrackSegment.End.A);
            Location towardB = WaypointPose.IntoBody(mark, TrackSegment.End.B);
            Vector3 probeA = _graph.GetPosition(_graph.LocationByMoving(towardA, 5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true));
            Vector3 probeB = _graph.GetPosition(_graph.LocationByMoving(towardB, 5f, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true));
            return (probeA - from).sqrMagnitude <= (probeB - from).sqrMagnitude ? towardA : towardB;
        }

        // The arriving end on the mark, the rest of the train behind it along intoBody.
        private void StandAt(ConsistEnd arriving, Location intoBody)
        {
            Location lead = intoBody.Flipped();
            Location tail = _graph.LocationByMoving(intoBody, Length, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true);
            if (arriving == ConsistEnd.A)
            {
                _endA = lead;
                _endB = tail;
            }
            else
            {
                _endA = tail;
                _endB = lead;
            }
            Changed = true;
        }

        private Car CouplingTarget(ManagedWaypoint wp)
        {
            if (wp.IsCoupling && wp.TryResolveCoupleToCar(out Car car))
            {
                return car;
            }
            if (wp.WillSeekSpecificCarCoupling)
            {
                return wp.CouplingSearchResultCar;
            }
            if (wp.WillSeekNearestCoupling)
            {
                Guesses.Add("couples to the nearest car at " + DescribeMark(wp));
            }
            return null;
        }

        // The coupled car's whole cut joins at the arriving end; then any cut after the coupling.
        private void Couple(Car coupleTo, ConsistEnd arriving, ManagedWaypoint wp)
        {
            // The whole string of cars the coupled car stands in joins (they're coupled together), nearest the train
            // first. Ordered by which end of the string is nearer the train's end where the train comes up to it.
            Location atEnd = arriving == ConsistEnd.A ? _endA : _endB;
            Vector3 trainEnd = _graph.GetPosition(atEnd);
            List<Car> cut = [.. coupleTo.EnumerateCoupled(Car.LogicalEnd.A)];
            if (cut.Count > 1 && (Middle(cut[0]) - trainEnd).sqrMagnitude > (Middle(cut[cut.Count - 1]) - trainEnd).sqrMagnitude)
            {
                cut.Reverse();
            }

            if (wp.WillPostCoupleCutPickup && wp.WillUncoupleByCount)
            {
                // Keep the cars of the cut nearest the train; the rest stay where they are.
                cut = cut.Take(Mathf.Clamp(wp.NumberOfCarsToCut, 0, cut.Count)).ToList();
            }
            else if (wp.WillPostCoupleCutDropoff && wp.WillUncoupleByCount)
            {
                // The cut stays, with the train's own cars next to it.
                int leave = Mathf.Clamp(wp.NumberOfCarsToCut, 0, Cars.Count - 1);
                RemoveFromEnd(arriving, leave);
                cut = [];
            }
            else if (wp.HasAnyPostCouplingCutOrders)
            {
                Guesses.Add("a pick-up or set-out after coupling at " + DescribeMark(wp) + " that isn't by count");
            }

            if (cut.Count == 0)
            {
                return;
            }
            Car far = cut[cut.Count - 1];
            Location farOutward = OutwardEnd(far, cut.Count > 1 ? cut[cut.Count - 2] : Cars[arriving == ConsistEnd.A ? 0 : Cars.Count - 1]);
            if (arriving == ConsistEnd.A)
            {
                cut.Reverse();
                Cars = [.. cut, .. Cars];
                _endA = farOutward;
            }
            else
            {
                Cars = [.. Cars, .. cut];
                _endB = farOutward;
            }
            Changed = true;
        }

        // A cut at a waypoint without a coupling, as UncouplingService picks it.
        private void Cut(ManagedWaypoint wp, ConsistEnd arriving)
        {
            if (wp.TakeUncoupledCarsAsActiveCut)
            {
                Guesses.Add("the train takes the uncoupled cars on at " + DescribeMark(wp));
                return;
            }
            // "Nearest to the waypoint" is the end that arrived on it.
            ConsistEnd countFrom = wp.CountUncoupledFromNearestToWaypoint ? arriving : Opposite(arriving);
            List<Car> fromEnd = countFrom == ConsistEnd.A ? [.. Cars] : Enumerable.Reverse(Cars).ToList();

            List<Car> toCut;
            if (wp.WillUncoupleByCount)
            {
                toCut = fromEnd.Take(wp.NumberOfCarsToCut).ToList();
            }
            else if (wp.WillUncoupleBySpecificCar && wp.UncouplingSearchResultCar is Car car && Cars.Contains(car))
            {
                // From the car toward the end counted from (UncouplingService.FindCutBySpecificCar).
                int at = fromEnd.IndexOf(car);
                toCut = fromEnd.Take(wp.ExcludeMatchingCarsFromCut ? at : at + 1).ToList();
            }
            else if (wp.WillUncoupleAllExceptLocomotives)
            {
                toCut = Cars.Where(c => !(c is BaseLocomotive) && !c.CarType.IsTender).ToList();
                Cars = Cars.Except(toCut).ToList();
                Changed = true;
                Guesses.Add("which end the remaining locomotives stand at after " + DescribeMark(wp));
                return;
            }
            else if (wp.WillUncoupleByDestination && TryDestinationCut(wp, fromEnd, out toCut))
            {
                Guesses.Add("cars set out by destination at " + DescribeMark(wp) + " (by today's waybills)");
            }
            else
            {
                Guesses.Add("the cut at " + DescribeMark(wp));
                return;
            }
            // The locomotive stays with the train.
            int keep = toCut.FindIndex(c => c is BaseLocomotive);
            if (keep >= 0)
            {
                toCut = toCut.Take(keep).ToList();
            }
            RemoveFromEnd(countFrom, toCut.Count);
        }

        // The same block of cars UncouplingService cuts for a destination, from today's waybills.
        private static bool TryDestinationCut(ManagedWaypoint wp, List<Car> fromEnd, out List<Car> toCut)
        {
            toCut = [];
            var service = ModLocator.ServiceProvider.GetService<UncouplingService>();
            var predicate = AccessTools.Method(typeof(UncouplingService), "GetDestinationPredicate")?.Invoke(service, [wp]) as Func<Car, bool>;
            var block = AccessTools.Method(typeof(UncouplingService), "FindMatchingCarBlock");
            if (predicate == null || block == null || !fromEnd.Any(predicate))
            {
                return false;
            }
            toCut = (List<Car>)block.Invoke(service, [fromEnd, predicate, wp.ExcludeMatchingCarsFromCut]);
            return true;
        }

        private void RemoveFromEnd(ConsistEnd end, int count)
        {
            count = Mathf.Clamp(count, 0, Cars.Count - 1);
            if (count == 0)
            {
                return;
            }
            List<Car> removed = end == ConsistEnd.A ? Cars.Take(count).ToList() : Cars.Skip(Cars.Count - count).ToList();
            float removedLength = AutoEngineerPlanner.CalculateTotalLength(removed);
            Cars = end == ConsistEnd.A ? Cars.Skip(count).ToList() : Cars.Take(Cars.Count - count).ToList();
            // The new end, moved in along the train by what was cut, still facing out.
            if (end == ConsistEnd.A)
            {
                _endA = _graph.LocationByMoving(_endA.Flipped(), removedLength, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true).Flipped();
            }
            else
            {
                _endB = _graph.LocationByMoving(_endB.Flipped(), removedLength, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true).Flipped();
            }
            Changed = true;
        }

        private Vector3 Middle(Car car) => (_graph.GetPosition(car.LocationA) + _graph.GetPosition(car.LocationB)) / 2f;

        private static ConsistEnd Opposite(ConsistEnd end) => end == ConsistEnd.A ? ConsistEnd.B : ConsistEnd.A;

        private static string DescribeMark(ManagedWaypoint wp) => string.IsNullOrEmpty(wp.Name) ? "an earlier waypoint" : wp.Name;

        // For Tetz's Test Harness (invoke WaypointQueue.TrainPrediction Harness <loco> <index>): the predicted cars at a
        // queue index, end A first, to compare with the train that arrives there.
        public static string Harness(string locomotiveQuery, string index)
        {
            if (!(TrainController.Shared.CarForString(locomotiveQuery) is BaseLocomotive locomotive))
            {
                return "no locomotive " + locomotiveQuery;
            }
            TrainPrediction prediction = ForQueueIndex(locomotive, int.Parse(index));
            return prediction.Describe() + ": " + string.Join(" - ", prediction.Cars.Select(c => c.DisplayName));
        }

        public string Describe()
        {
            string text = $"{Cars.Count} car{(Cars.Count == 1 ? "" : "s")}, {Length:0} m";
            return Guesses.Count == 0 ? text : text + " (a guess: " + string.Join("; ", Guesses.Distinct()) + ")";
        }
    }
}
