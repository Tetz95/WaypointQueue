using KeyValue.Runtime;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
#if RR2026
using Model;
using Model.AI;
using Model.AI.Waypoints;
using Track;
using Track.Search;
using UnityEngine;
#endif

namespace WaypointQueue.Model
{
    // How Railroader 2026.1's picker asked for the train to stand at a waypoint: which end of the train arrives on the
    // mark, which side of the mark the train lies, the junctions its body was placed over (when the player picked a
    // particular placement), how long the train was then, and which end of the car to couple to. Kept as plain values
    // so saves and route files read the same in both game versions; Legacy has no poses and never sets one.
    public class QueuedPose
    {
        // WaypointEnd: 0 nearest end, 1 consist end A, 2 consist end B.
        [JsonProperty] public int ArrivingEnd { get; set; }

        // BodyToward: 0 either side, 1 toward the segment's end A, 2 toward its end B.
        [JsonProperty] public int BodyToward { get; set; }

        // Car.End of the car to couple to, if the picker snapped to one.
        [JsonProperty] public int? CoupleToCarEnd { get; set; }

        // Junction turns as node, from-segment, to-segment triples (the game's own encoding).
        [JsonProperty] public string[] Turns { get; set; }

        // Train length the placement was planned for, in metres; 0 when nothing was placed.
        [JsonProperty] public float BodyLength { get; set; }

        [JsonIgnore] public bool IsPosed => ArrivingEnd != 0 || BodyToward != 0;

        [JsonIgnore] public bool IsEmpty => !IsPosed && CoupleToCarEnd == null && BodyLength <= 0f;

        public QueuedPose Copy() => new()
        {
            ArrivingEnd = ArrivingEnd,
            BodyToward = BodyToward,
            CoupleToCarEnd = CoupleToCarEnd,
            Turns = Turns?.ToArray(),
            BodyLength = BodyLength,
        };

        public bool SameAs(QueuedPose other)
        {
            if (other == null) return IsEmpty;
            return ArrivingEnd == other.ArrivingEnd && BodyToward == other.BodyToward && CoupleToCarEnd == other.CoupleToCarEnd
                && BodyLength.Equals(other.BodyLength) && (Turns ?? []).SequenceEqual(other.Turns ?? []);
        }

        public Value ToPropertyValue()
        {
            var dictionary = new Dictionary<string, Value>
            {
                ["end"] = Value.Int(ArrivingEnd),
                ["side"] = Value.Int(BodyToward),
                ["blen"] = Value.Float(BodyLength),
            };
            if (CoupleToCarEnd.HasValue) dictionary["cplEnd"] = Value.Int(CoupleToCarEnd.Value);
            if (Turns != null && Turns.Length > 0) dictionary["turns"] = Value.Array(Turns.Select(t => Value.String(t)).ToList());
            return Value.Dictionary(dictionary);
        }

        public static QueuedPose FromPropertyValue(Value value)
        {
            if (value.IsNull || value.Type != ValueType.Dictionary) return null;
            var pose = new QueuedPose
            {
                ArrivingEnd = value["end"].IntValue,
                BodyToward = value["side"].IntValue,
                BodyLength = value["blen"].FloatValue,
                CoupleToCarEnd = value["cplEnd"].IsNull ? null : value["cplEnd"].IntValue,
                Turns = value["turns"].IsNull ? null : value["turns"].ArrayValue.Select(v => v.StringValue).ToArray(),
            };
            return pose.IsEmpty ? null : pose;
        }

#if RR2026
        public static QueuedPose From(OrderWaypoint waypoint)
        {
            var pose = new QueuedPose
            {
                ArrivingEnd = (int)waypoint.ArrivingEnd,
                BodyToward = (int)waypoint.BodyToward,
                CoupleToCarEnd = waypoint.CoupleToCarEnd.HasValue ? (int)waypoint.CoupleToCarEnd.Value : null,
                Turns = OrderWaypoint.EncodeTurns(waypoint.RequiredTurns),
                BodyLength = waypoint.BodyLength,
            };
            return pose.IsEmpty ? null : pose;
        }

        // How the train will stand, in the picker's words ("Front of train on the mark, train past it"). Front is the
        // locomotive's front; short of and past are seen from the side the locomotive is on now, so for a waypoint
        // further down the queue they're only a guide. Null for a waypoint without a pose.
        public string Describe(BaseLocomotive locomotive, Location mark)
        {
            if (!IsPosed)
            {
                return null;
            }
            string end = ArrivingEnd == 0 ? "Either end of train" : "End " + ((WaypointEnd)ArrivingEnd) + " of train";
            string side = BodyToward == 0 ? "" : ", train lies toward segment end " + (BodyToward == 1 ? "A" : "B");
            if (locomotive != null)
            {
                if (ArrivingEnd != 0)
                {
                    bool front = PosedWaypoint.ToWaypointEnd(PosedWaypoint.FrontEnd(locomotive)) == (WaypointEnd)ArrivingEnd;
                    end = front ? "Front of train" : "Back of train";
                }
                if (BodyToward != 0)
                {
                    TrackSegment.End nearSide = global::UI.Waypoints.WaypointPoseCycle.NearSideOf(Graph.Shared.Current, mark, locomotive, out _);
                    bool shortOf = WaypointPose.ToSegmentEnd((Track.Search.BodyToward)BodyToward) == nearSide;
                    side = shortOf ? ", train short of it" : ", train past it";
                }
            }
            int junctions = Turns == null ? 0 : Turns.Length / 3;
            string over = junctions > 0 ? $" (over {junctions} junction{(junctions == 1 ? "" : "s")})" : "";
            return end + " on the mark" + side + over;
        }

        // Send past waypoint as a pose: the whole train past the mark, whichever end arrives on it. Past is away from
        // the side the train comes in from: the auto engineer's own route there, from its last point before the mark
        // (a train that runs round a loop or reverses first comes in from the far side). Without a route, the side
        // nearer the locomotive, as the game's picker guesses it.
        public static QueuedPose PastTheMark(BaseLocomotive locomotive, Location mark)
        {
            IGraphView graphView = Graph.Shared.Current;
            Vector3 from = graphView.GetPosition(locomotive.LocationF);
            if (PosedWaypoint.PlanNaturalArrival(locomotive, graphView, mark, null, new List<JunctionTurn>(), out List<RouteSearch.Step> route) && route != null)
            {
                Vector3 markPosition = graphView.GetPosition(mark);
                for (int i = route.Count - 1; i >= 0; i--)
                {
                    if (Vector3.Distance(route[i].Position, markPosition) >= ApproachPointDistance)
                    {
                        from = route[i].Position;
                        break;
                    }
                }
            }
            Vector3 sideA = graphView.GetPosition(graphView.LocationByMoving(WaypointPose.IntoBody(mark, TrackSegment.End.A), SideProbeDistance, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true));
            Vector3 sideB = graphView.GetPosition(graphView.LocationByMoving(WaypointPose.IntoBody(mark, TrackSegment.End.B), SideProbeDistance, checkSwitchAgainstMovement: false, stopAtEndOfTrack: true));
            TrackSegment.End nearSide = (sideA - from).sqrMagnitude <= (sideB - from).sqrMagnitude ? TrackSegment.End.A : TrackSegment.End.B;
            return new QueuedPose { BodyToward = (int)WaypointPose.ToBodyToward(nearSide.Flipped()) };
        }

        // The route's last point at least this far from the mark tells which side the train comes in from.
        private const float ApproachPointDistance = 5f;

        // How far to each side of the mark to look, as the game's picker does.
        private const float SideProbeDistance = 20f;

        // A train counts as the same length within half a metre (coupler slack, rounding).
        private const float SameLengthTolerance = 0.5f;

        // The order to send the locomotive to the waypoint now. A placement picked for a train of the length it still
        // has goes out as picked; once cars have been picked up or set out since, the game plans the same pose again
        // for the train as it is (PosedWaypoint.Order walks the new length from the mark).
        public OrderWaypoint ToOrder(BaseLocomotive locomotive, Location mark, string locationString, string coupleToCarId)
        {
            var end = (WaypointEnd)ArrivingEnd;
            var side = (Track.Search.BodyToward)BodyToward;
            Car.End? carEnd = CoupleToCarEnd.HasValue ? (Car.End)CoupleToCarEnd.Value : null;
            if (!IsPosed)
            {
                return new OrderWaypoint(locationString, coupleToCarId, end, side, null, 0f, carEnd);
            }
            if (BodyLength > 0f)
            {
                float length = AutoEngineerPlanner.CalculateTotalLength(locomotive.EnumerateCoupled().ToList());
                if (System.Math.Abs(length - BodyLength) <= SameLengthTolerance)
                {
                    return new OrderWaypoint(locationString, coupleToCarId, end, side, OrderWaypoint.DecodeTurns(Turns), BodyLength, carEnd);
                }
            }
            return PosedWaypoint.Order(locomotive, Graph.Shared.Current, mark, end, side, null, coupleToCarId, carEnd);
        }
#endif
    }
}
