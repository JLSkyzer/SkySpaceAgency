using System;
using System.Collections.Generic;
using K2D2.OrbitPlanning;

namespace K2D2.KSPService
{
    /// <summary>
    /// In-memory registry of the target orbit of each maneuver node the Orbit tab created, by
    /// NodeID. The Node tab flies a registered node with the closed-loop TargetOrbitBurn instead
    /// of the game's maneuver model; any other node (made by the player) is not here and keeps the
    /// usual path. Entries go when their node is executed or removed (ManeuverCreator.RemoveNode /
    /// RemoveAllNodes, Prune against the live plan), and all of them on a vessel change.
    /// </summary>
    public static class NodeTargets
    {
        static readonly Dictionary<Guid, TargetOrbit> targets = new Dictionary<Guid, TargetOrbit>();

        public static int Count => targets.Count;

        public static void Register(Guid nodeId, TargetOrbit target) => targets[nodeId] = target;

        public static bool TryGet(Guid nodeId, out TargetOrbit target) => targets.TryGetValue(nodeId, out target);

        public static void Remove(Guid nodeId) => targets.Remove(nodeId);

        public static void Clear() => targets.Clear();

        /// <summary>Drops every entry whose node is not among liveNodeIds (removed by hand, for example).</summary>
        public static void Prune(IEnumerable<Guid> liveNodeIds)
        {
            var live = new HashSet<Guid>(liveNodeIds);
            var stale = new List<Guid>();
            foreach (var id in targets.Keys)
                if (!live.Contains(id))
                    stale.Add(id);
            foreach (var id in stale)
                targets.Remove(id);
        }
    }
}
