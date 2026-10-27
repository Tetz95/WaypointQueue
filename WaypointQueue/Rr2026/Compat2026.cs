// Members Railroader 2026.1 renamed or dropped, added back as C# 14 extension members so the code that uses them reads
// as it does for Legacy.
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Model.Ops;
using Model.Ops.Definition;
using RollingStock;
using Track;
using Track.Search;
using Track.Signals;
using UI;
using UI.Builder;
using UnityEngine.UI;

namespace WaypointQueue
{
    internal static class Compat2026
    {
        extension(TrainController trainController)
        {
            public Graph graph => trainController.Graph;
        }

        extension(GameInput)
        {
            public static GameInput shared => GameInput.Shared;
        }

        // Interlockings now hold switch node ids.
        extension(CTCInterlocking.SwitchSet set)
        {
            public List<TrackNode> switchNodes =>
                set.switchNodeIds?.Select(id => Graph.Shared.GetNode(id)).Where(n => n != null).ToList();
        }

        extension(ref UIPanelBuilder builder)
        {
            public float Spacing
            {
                get => Traverse.Create(builder).Field<UnityEngine.RectTransform>("_container").Value.GetComponent<HorizontalOrVerticalLayoutGroup>().spacing;
                set => builder.ConfigureLayout(spacing: value);
            }
        }

        // Loads are definitions with namespaced ids now ("rr/water"); the old asset name is the id without the base
        // game's "rr/", so waypoints saved by Legacy still match.
        extension(Load load)
        {
            public string name => load.id != null && load.id.StartsWith("rr/") ? load.id.Substring(3) : load.id;
        }

        extension(CarLoadTargetLoader loader)
        {
            public Industry sourceIndustry => Traverse.Create(loader).Property<Industry>("sourceIndustry").Value;
        }

        // The route search no longer reports a traverse time.
        extension(Graph graph)
        {
            public bool TryFindDistance(Location start, Location end, out float totalDistance, out float traverseTimeSeconds)
            {
                traverseTimeSeconds = 0f;
                return GraphRouteSearchExtension.TryFindDistance(graph, start, end, out totalDistance);
            }
        }
    }
}
