using Game.Events;
using Game.State;
using GalaSoft.MvvmLight.Messaging;
using HarmonyLib;
using Model;
using Model.AI;
using System;
using System.Collections.Generic;
using Track;
using Track.Search;
using UnityEngine;
using WaypointQueue.UUM;

namespace WaypointQueue
{
    // When the AE reaches a waypoint, RestoreAllRemainingSwitches resets the switches it threw and
    // then forgets them all, skipping any switch a car is still on. A train that stops on a switch
    // it threw therefore leaves it thrown after it moves off. Keep those switches and reset each one
    // once the train is clear of it.
    [HarmonyPatch(typeof(AutoEngineerPlanner))]
    internal class PatchRestoreSwitchesAfterWaypoint
    {
        [HarmonyPrefix]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllRemainingSwitchesPrefix(
            Dictionary<string, bool> ____routeSwitchesToRestore,
            out Dictionary<string, bool> __state
        )
        {
            __state = ____routeSwitchesToRestore != null && ____routeSwitchesToRestore.Count > 0
                ? new Dictionary<string, bool>(____routeSwitchesToRestore)
                : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch("RestoreAllRemainingSwitches")]
        private static void RestoreAllRemainingSwitchesPostfix(
            AutoEngineerPlanner __instance,
            BaseLocomotive ____locomotive,
            Graph ____graph,
            Dictionary<string, bool> __state
        )
        {
            try
            {
                if (__state == null || ____locomotive == null || ____graph == null)
                    return;

                foreach (KeyValuePair<string, bool> entry in __state)
                {
                    TrackNode node = ____graph.GetNode(entry.Key);
                    if (node == null || node.isThrown == entry.Value)
                        continue;

                    PendingSwitchRestorer.Add(new PendingSwitchRestorer.PendingSwitch
                    {
                        NodeId = entry.Key,
                        OriginalThrown = entry.Value,
                        LeftThrown = node.isThrown,
                        Planner = new WeakReference<AutoEngineerPlanner>(__instance),
                        LocomotiveIdent = ____locomotive.Ident.ToString()
                    });
                    Loader.Log($"Loco {____locomotive.Ident} couldn't reset switch {entry.Key} with the train on it; will reset it once the train is clear");
                }
            }
            catch (Exception ex)
            {
                Loader.LogError($"PatchRestoreSwitchesAfterWaypoint postfix failed: {ex}");
            }
        }
    }

    // Retries the switches every couple of seconds, on the host only.
    internal class PendingSwitchRestorer : MonoBehaviour
    {
        internal sealed class PendingSwitch
        {
            public string NodeId;

            // The position to put the switch back to, and the position the AE left it in.
            public bool OriginalThrown;
            public bool LeftThrown;

            public WeakReference<AutoEngineerPlanner> Planner;
            public string LocomotiveIdent;
        }

        private const float IntervalSeconds = 2f;

        private static readonly Dictionary<string, PendingSwitch> _pending = [];
        private static PendingSwitchRestorer _instance;

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, List<RouteSearch.Step>> _route =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, List<RouteSearch.Step>>("_route");

        private static readonly AccessTools.FieldRef<AutoEngineerPlanner, int> _startStepIndex =
            AccessTools.FieldRefAccess<AutoEngineerPlanner, int>("_startStepIndex");

        private float _nextCheck;

        internal static void Add(PendingSwitch pending)
        {
            _pending[pending.NodeId] = pending;
            if (_instance == null)
            {
                GameObject go = new GameObject("WaypointQueue.PendingSwitchRestorer");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<PendingSwitchRestorer>();
            }
        }

        private void Awake()
        {
            // Pending switches belong to the loaded save.
            Messenger.Default.Register<MapWillUnloadEvent>(this, _ => _pending.Clear());
        }

        private void OnDestroy()
        {
            Messenger.Default.Unregister(this);
        }

        private void Update()
        {
            if (_pending.Count == 0 || Time.unscaledTime < _nextCheck)
                return;

            _nextCheck = Time.unscaledTime + IntervalSeconds;

            if (!StateManager.IsHost || TrainController.Shared == null)
                return;

            TrainController trainController = TrainController.Shared;
            Graph graph = trainController.graph;
            List<string> done = [];
            foreach (PendingSwitch pending in _pending.Values)
            {
                try
                {
                    TrackNode node = graph.GetNode(pending.NodeId);
                    if (node == null || node.isThrown == pending.OriginalThrown)
                    {
                        // Gone, or already back where it was.
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (node.isThrown != pending.LeftThrown)
                    {
                        Loader.Log($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} was changed by someone else; leaving it");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (node.IsCTCSwitch && !node.IsCTCSwitchUnlocked)
                    {
                        Loader.Log($"Loco {pending.LocomotiveIdent}: switch {pending.NodeId} is under CTC control; leaving it to the dispatcher");
                        done.Add(pending.NodeId);
                        continue;
                    }

                    if (IsOnCurrentRoute(pending, node) || !trainController.CanSetSwitch(node, pending.OriginalThrown, out _))
                        continue;

                    if (trainController.TrySetSwitch(pending.NodeId, pending.OriginalThrown, "AE " + pending.LocomotiveIdent, out string error))
                        Loader.Log($"Loco {pending.LocomotiveIdent}: reset switch {pending.NodeId} now that the train is clear");
                    else
                        Loader.LogError($"Loco {pending.LocomotiveIdent}: couldn't reset switch {pending.NodeId}: {error}");

                    done.Add(pending.NodeId);
                }
                catch (Exception ex)
                {
                    Loader.LogError($"Restoring switch {pending.NodeId} failed: {ex}");
                    done.Add(pending.NodeId);
                }
            }

            foreach (string id in done)
                _pending.Remove(id);
        }

        // The AE's current route still needs the switch where it is, for example a move that comes
        // back over it.
        private static bool IsOnCurrentRoute(PendingSwitch pending, TrackNode node)
        {
            if (!pending.Planner.TryGetTarget(out AutoEngineerPlanner planner) || planner == null)
                return false;

            List<RouteSearch.Step> route = _route(planner);
            if (route == null)
                return false;

            for (int i = Math.Max(0, _startStepIndex(planner)); i < route.Count; i++)
            {
                if (route[i].Node == node)
                    return true;
            }
            return false;
        }
    }
}
