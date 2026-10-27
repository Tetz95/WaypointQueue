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
            JunctionTurn[] turns = OrderWaypoint.DecodeTurns(Turns);
            if (BodyLength <= 0f || !IsPosed)
            {
                return new OrderWaypoint(locationString, coupleToCarId, end, side, null, 0f, carEnd);
            }
            float length = AutoEngineerPlanner.CalculateTotalLength(locomotive.EnumerateCoupled().ToList());
            if (System.Math.Abs(length - BodyLength) <= SameLengthTolerance)
            {
                return new OrderWaypoint(locationString, coupleToCarId, end, side, turns, BodyLength, carEnd);
            }
            return PosedWaypoint.Order(locomotive, Graph.Shared.Current, mark, end, side, null, coupleToCarId, carEnd);
        }
#endif
    }
}
