using System;
using System.Collections.Generic;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    // The body hierarchy and its ephemeris: each body moves on a two-body Kepler orbit around its
    // parent, propagated from its epoch state. Pure code: no game API, safe on a worker thread.
    // Only UT-independent values (periods) are cached.
    public class BodySystem
    {
        readonly BodyNode[] _nodes;
        readonly Dictionary<string, BodyNode> _byName = new Dictionary<string, BodyNode>();
        readonly double[] _periods;

        public BodySystem(IReadOnlyList<BodyNode> nodes)
        {
            if (nodes == null)
                throw new ArgumentNullException(nameof(nodes));
            _nodes = new BodyNode[nodes.Count];
            _periods = new double[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (n.Index != i)
                    throw new ArgumentException($"Body {n.Name} has index {n.Index} but sits at position {i}.");
                if (n.Name == null || _byName.ContainsKey(n.Name))
                    throw new ArgumentException(n.Name == null
                        ? $"Body {i} has no name."
                        : $"Two bodies are named {n.Name}: the name index would be ambiguous.");
                _nodes[i] = n;
                _byName[n.Name] = n;
            }
            for (int i = 0; i < _nodes.Length; i++)
            {
                var n = _nodes[i];
                if (n.ParentIndex < 0)
                {
                    _periods[i] = double.PositiveInfinity;
                    continue;
                }
                if (n.ParentIndex >= _nodes.Length || n.ParentIndex == i)
                    throw new ArgumentException($"Body {n.Name} has an invalid parent index {n.ParentIndex}.");
                _periods[i] = OrbitMath.Elements(n.EpochPosition, n.EpochVelocity, _nodes[n.ParentIndex].Mu).Period;
            }
        }

        public int Count => _nodes.Length;
        public BodyNode this[int index] => _nodes[index];
        public BodyNode this[string name] => _byName[name];

        public double OrbitalPeriod(int body) => _periods[body];

        public void StateRelativeToParent(int body, double ut, out Vector3d r, out Vector3d v)
        {
            var n = _nodes[body];
            if (n.ParentIndex < 0)
            {
                r = Vector3d.zero;
                v = Vector3d.zero;
                return;
            }
            double dt = ut - n.EpochUT;
            double period = _periods[body];
            if (!double.IsInfinity(period))
                dt -= period * Math.Floor(dt / period); // fewer Newton steps, and no precision lost over many years
            if (dt == 0)
            {
                r = n.EpochPosition;
                v = n.EpochVelocity;
                return;
            }
            KeplerPropagator.Propagate(n.EpochPosition, n.EpochVelocity, _nodes[n.ParentIndex].Mu, dt, out r, out v);
        }

        // State of 'body' relative to 'ancestor' (or itself), summed up the chain.
        public void StateRelativeTo(int body, int ancestor, double ut, out Vector3d r, out Vector3d v)
        {
            if (body != ancestor && !IsAncestor(ancestor, body))
                throw new ArgumentException($"{_nodes[ancestor].Name} is not an ancestor of {_nodes[body].Name}.");
            r = Vector3d.zero;
            v = Vector3d.zero;
            for (int b = body; b != ancestor; b = _nodes[b].ParentIndex)
            {
                StateRelativeToParent(b, ut, out var rb, out var vb);
                r += rb;
                v += vb;
            }
        }

        // [body, parent, …, root].
        public IReadOnlyList<int> PathToRoot(int body)
        {
            var path = new List<int>();
            for (int b = body; b >= 0; b = _nodes[b].ParentIndex)
                path.Add(b);
            return path;
        }

        // Strict: a body is not its own ancestor.
        public bool IsAncestor(int ancestor, int body)
        {
            for (int b = _nodes[body].ParentIndex; b >= 0; b = _nodes[b].ParentIndex)
                if (b == ancestor)
                    return true;
            return false;
        }

        // The lowest body that is 'a' or one of its ancestors and also 'b' or one of its ancestors.
        public int CommonAncestor(int a, int b)
        {
            var chainA = new HashSet<int>(PathToRoot(a));
            for (int x = b; x >= 0; x = _nodes[x].ParentIndex)
                if (chainA.Contains(x))
                    return x;
            throw new ArgumentException($"{_nodes[a].Name} and {_nodes[b].Name} share no root.");
        }

        // Time between two identical alignments of two bodies orbiting the same parent.
        public double SynodicPeriod(int a, int b)
        {
            int parent = _nodes[a].ParentIndex;
            if (a == b || parent < 0 || parent != _nodes[b].ParentIndex)
                throw new ArgumentException($"{_nodes[a].Name} and {_nodes[b].Name} are not siblings.");
            double diff = Math.Abs(1 / _periods[a] - 1 / _periods[b]);
            return diff > 0 ? 1 / diff : double.PositiveInfinity;
        }
    }
}
