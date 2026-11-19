using System;
using System.Collections.Generic;
using SkySpaceAgency.OrbitPlanning;
using KSP.Sim;

namespace SkySpaceAgency.KSPService
{
    /// <summary>
    /// A registered node: the orbit planned right after it, and the node's BurnVector and Time
    /// when it was created, to tell whether the player has edited it since.
    /// </summary>
    public struct NodeTarget
    {
        // Beyond these, the node no longer is the one planned: the player moved or resized it.
        public const double BurnVectorTolerance = 0.5;   // m/s, per component
        public const double TimeTolerance = 1.0;         // s

        public TargetOrbit Target;
        public Vector3d BurnVector;
        public double Time;

        public bool IsEdited(Vector3d burnVector, double time) =>
            Math.Abs(burnVector.x - BurnVector.x) > BurnVectorTolerance
            || Math.Abs(burnVector.y - BurnVector.y) > BurnVectorTolerance
            || Math.Abs(burnVector.z - BurnVector.z) > BurnVectorTolerance
            || Math.Abs(time - Time) > TimeTolerance;
    }

    /// <summary>
    /// In-memory registry of the target orbit of each maneuver node the Orbit tab created, by
    /// NodeID. The Node tab flies a registered node with the closed-loop TargetOrbitBurn instead
    /// of the game's maneuver model; any other node (made by the player) is not here and keeps the
    /// usual path. Entries go when their node is executed or removed (ManeuverCreator.RemoveNode /
    /// RemoveAllNodes, Prune against the live plan), and all of them on a vessel change.
    /// </summary>
    public static class NodeTargets
    {
        static readonly Dictionary<Guid, NodeTarget> targets = new Dictionary<Guid, NodeTarget>();

        public static int Count => targets.Count;

        public static void Register(Guid nodeId, TargetOrbit target, Vector3d burnVector, double time) =>
            targets[nodeId] = new NodeTarget { Target = target, BurnVector = burnVector, Time = time };

        public static bool TryGet(Guid nodeId, out NodeTarget entry) => targets.TryGetValue(nodeId, out entry);

        public static void Remove(Guid nodeId) => targets.Remove(nodeId);

        public static void Clear() => targets.Clear();

        /// <summary>
        /// The nodes a burn that ends at burnEndUT replaces: every node at or before that time (and a
        /// node with no valid time). Nodes after it are the player's plan for later and stay
        /// (K2-D2 1.4.0 no longer deletes them on circularize; user decision 2026-11-19).
        /// </summary>
        public static List<T> NodesToRemove<T>(IEnumerable<T> nodes, Func<T, double> time, double burnEndUT)
        {
            var doomed = new List<T>();
            if (nodes == null)
                return doomed;
            foreach (var node in nodes)
            {
                double t = time(node);
                if (!(t > burnEndUT))   // NaN too
                    doomed.Add(node);
            }
            return doomed;
        }

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
