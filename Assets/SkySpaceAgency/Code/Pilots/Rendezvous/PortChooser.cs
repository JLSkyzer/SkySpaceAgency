using System;
using System.Collections.Generic;
using KSP.Sim;

namespace K2D2.Rendezvous
{
    // A docking port, as the chooser needs it (read from PartComponentModule_DockingNode /
    // Data_DockingNode and the view's docking transform). Position and Axis are inertial.
    public struct PortInfo
    {
        public string PartId;
        public string[] NodeTypes;
        public bool Free;        // Data_DockingNode.CurrentState == Ready
        public bool Gendered;
        public bool Female;      // meaningful only when Gendered
        // Not used by the chooser: the port face, for DockState.RelPos and TargetCenter.
        public Vector3d Position;
        public Vector3d Axis;    // the port's outward axis
        // Not used by the chooser: the port's size, m, for DockLimits' portSize (ContactLateral).
        public double Size;
    }

    // Picks our port and the target's for the docking.
    // - Compatible: both free, their NodeTypes overlap, and either both are ungendered or both
    //   gendered and of opposite gender.
    // - Cost, in degrees: the rotation our vessel needs to point our port's axis at −(their axis),
    //   plus 90 × (1 − facing), facing = their axis · the direction from the target to us. A port
    //   facing us adds nothing, one side-on 90, one facing away 180: a pair on the side facing us
    //   always beats one on the far side, which is still chosen when there is no other (the
    //   guidance goes around the target).
    // - Ties: the pair whose port is closest to our forward.
    // Pure code, tested in EditMode.
    public static class PortChooser
    {
        // reason is null on success, else why no pair was found.
        public static (PortInfo ours, PortInfo theirs, string reason) Choose(IReadOnlyList<PortInfo> ours,
            IReadOnlyList<PortInfo> theirs, Vector3d ourForward, Vector3d fromTargetToUs)
        {
            if (ours == null || ours.Count == 0) return Refuse("our vessel has no docking port");
            if (theirs == null || theirs.Count == 0) return Refuse("the target has no docking port");
            if (!AnyFree(ours)) return Refuse("our vessel has no free docking port");
            if (!AnyFree(theirs)) return Refuse("the target has no free docking port");

            var toUs = Unit(fromTargetToUs);
            var fwd = Unit(ourForward);
            bool typeMatch = false, found = false;
            double bestCost = double.PositiveInfinity, bestTie = double.PositiveInfinity;
            PortInfo bestOurs = default, bestTheirs = default;

            foreach (var o in ours)
            {
                if (!o.Free) continue;
                foreach (var t in theirs)
                {
                    if (!t.Free || !Overlap(o.NodeTypes, t.NodeTypes)) continue;
                    typeMatch = true;
                    if (o.Gendered != t.Gendered || (o.Gendered && o.Female == t.Female)) continue;

                    double rotation = AngleDeg(Unit(o.Axis), -Unit(t.Axis));
                    double facing = Vector3d.Dot(Unit(t.Axis), toUs);
                    double cost = rotation + 90 * (1 - facing);
                    double tie = AngleDeg(Unit(o.Axis), fwd);
                    if (!found || cost < bestCost - 1e-9 || (Math.Abs(cost - bestCost) <= 1e-9 && tie < bestTie))
                    {
                        found = true;
                        bestCost = cost;
                        bestTie = tie;
                        bestOurs = o;
                        bestTheirs = t;
                    }
                }
            }

            if (found) return (bestOurs, bestTheirs, null);
            return Refuse(typeMatch
                ? "no free pair of ports of opposite gender"
                : "no free pair of ports with a common node type");
        }

        static (PortInfo, PortInfo, string) Refuse(string why) => (default, default, why);

        static bool AnyFree(IReadOnlyList<PortInfo> ports)
        {
            foreach (var p in ports)
                if (p.Free) return true;
            return false;
        }

        static bool Overlap(string[] a, string[] b)
        {
            if (a == null || b == null) return false;
            foreach (var x in a)
                if (!string.IsNullOrEmpty(x) && Array.IndexOf(b, x) >= 0) return true;
            return false;
        }

        // 180° when either vector is unknown: the worst rotation.
        static double AngleDeg(Vector3d u, Vector3d v)
        {
            if (u.sqrMagnitude == 0 || v.sqrMagnitude == 0) return 180;
            return Math.Acos(Math.Max(-1, Math.Min(1, Vector3d.Dot(u, v)))) * 180 / Math.PI;
        }

        static Vector3d Unit(Vector3d v)
        {
            double sum = v.x + v.y + v.z;
            if (double.IsNaN(sum) || double.IsInfinity(sum) || v.magnitude < 1e-9) return Vector3d.zero;
            return v.normalized;
        }
    }
}
