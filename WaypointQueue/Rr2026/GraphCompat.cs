using Track;
using UnityEngine;
#if !RR2026
using Helpers;
#endif

namespace WaypointQueue
{
    // Graph calls that differ between Legacy and Railroader 2026.1, behind one set of names.
    internal static class GraphCompat
    {
#if RR2026
        // 2026.1: LocationToString is static, and node positions are already in game coordinates.
        public static string LocationToString(Location location) => Graph.LocationToString(location);

        public static Vector3 GamePosition(TrackNode node) => node.Position;
#else
        public static string LocationToString(Location location) => Graph.Shared.LocationToString(location);

        public static Vector3 GamePosition(TrackNode node) => node.transform.position.WorldToGame();
#endif
    }
}
