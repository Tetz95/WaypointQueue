using Track;
using UnityEngine;

namespace WaypointQueue
{
    // Graph calls that differ between Legacy and Railroader 2026.1, behind one set of names.
    internal static class GraphCompat
    {
        // 2026.1: LocationToString is static, and node positions are already in game coordinates.
        public static string LocationToString(Location location) => Graph.LocationToString(location);

        public static Vector3 GamePosition(TrackNode node) => node.Position;
    }
}
