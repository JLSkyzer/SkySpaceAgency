using System;
using System.Collections.Generic;
using System.Linq;
using KSP.Modules;
using KSP.OAB;
using KSP.Sim;

namespace K2D2.Rendezvous
{
    // One docking port's capture limits, from its Data_DockingNode.
    public struct PortCapture
    {
        public double AcquireMinFwdDot;
        public double CaptureMinFwdDot;
        public double CaptureMaxRvel;     // m/s
        public double MinRollDot;
        public double CaptureMinRollDot;
    }

    // One vessel of the target list, as the game gives it.
    public struct VesselEntry
    {
        public string Id;
        public string Name;
        public string Body;
        public bool IsDebris;
        public bool IsOurs;
    }

    // Pure helpers of the rendezvous game reads (RendezvousSnapshot, DockingParts), tested in
    // EditMode. Every vector is in the reference body's celestial frame, Zup (as 4.1's
    // VesselSnapshot), so it does not rotate.
    public static class DockingReads
    {
        // Above this spin about the approach axis the docking is refused (Global Constraints).
        public const double MaxSpinDegPerSec = 1;

        // A part's half size, added to the farthest part centre for TargetRadius: an estimate,
        // the keep-out sphere adds DockLimits.KeepOutMargin on top.
        public const double PartHalfSize = 2;

        public static PortInfo Port(string partId, string[] nodeTypes, bool ready, bool gendered, bool female,
            Vector3d position, Vector3d axis, double size)
        {
            return new PortInfo
            {
                PartId = partId,
                NodeTypes = nodeTypes == null ? new string[0] : nodeTypes.Where(t => !string.IsNullOrEmpty(t)).ToArray(),
                Free = ready && !string.IsNullOrEmpty(partId),
                Gendered = gendered,
                Female = gendered && female,
                Position = Finite(position) ? position : Vector3d.zero,
                Axis = Unit(axis),
                Size = size,
            };
        }

        // A part size category in metres (PortInfo.Size): the KSP2 diameters, XS 0.625 m to XL 5 m.
        // The "PLUS" sizes are taken halfway to the next size, an estimate. 0 when unknown (Auto).
        public static double SizeMetres(MetaAssemblySizeFilterType size)
        {
            switch (size)
            {
                case MetaAssemblySizeFilterType.XS: return 0.625;
                case MetaAssemblySizeFilterType.XSPLUS: return 0.9375;
                case MetaAssemblySizeFilterType.S: return 1.25;
                case MetaAssemblySizeFilterType.SPLUS: return 1.875;
                case MetaAssemblySizeFilterType.M: return 2.5;
                case MetaAssemblySizeFilterType.MPLUS: return 3.125;
                case MetaAssemblySizeFilterType.L: return 3.75;
                case MetaAssemblySizeFilterType.LPLUS: return 4.375;
                case MetaAssemblySizeFilterType.XL: return 5;
                case MetaAssemblySizeFilterType.XLPLUS: return 5.625;
                default: return 0;
            }
        }

        // The approach limits for a pair of ports: the stricter value of the two ports, and of the
        // acquire and capture steps (the game checks AcquireMinFwdDot on the docker, then captures).
        // A roll dot ≤ 0 means any roll will do, which DockLimits writes as 0. The port size given
        // to DockLimits (its contact tolerance) is the smaller of the pair's PortInfo.Size, in m;
        // an unknown size (≤ 0) is left out.
        public static DockLimits Limits(PortCapture ours, PortCapture theirs, bool rcs, double targetRadius,
            double ourSize = 0, double theirSize = 0)
        {
            double fwd = Max(ours.AcquireMinFwdDot, ours.CaptureMinFwdDot, theirs.AcquireMinFwdDot, theirs.CaptureMinFwdDot);
            double rvel = Math.Min(Positive(ours.CaptureMaxRvel), Positive(theirs.CaptureMaxRvel));
            double roll = Max(ours.MinRollDot, ours.CaptureMinRollDot, theirs.MinRollDot, theirs.CaptureMinRollDot);
            if (double.IsInfinity(rvel)) rvel = 0.5;   // no port gave a usable value: the RCS final speed cap
            fwd = Math.Max(-1, Math.Min(1, fwd));
            roll = roll > 0 ? Math.Min(1, roll) : 0;
            double radius = targetRadius > 0 && !double.IsInfinity(targetRadius) ? targetRadius : PartHalfSize;
            double size = PairSize(ourSize, theirSize);
            return rcs ? DockLimits.Rcs(fwd, rvel, roll, radius, size) : DockLimits.MainEngine(fwd, rvel, roll, radius, size);
        }

        // The RCS action group value to restore: on for True, off for False, null (leave it as it
        // is) for Mixed or an unknown state.
        public static bool? RcsRestore(KSPActionGroupState? before)
        {
            if (before == KSPActionGroupState.True) return true;
            if (before == KSPActionGroupState.False) return false;
            return null;
        }

        // The smaller of two port sizes, ignoring unknown (≤ 0 or non-finite) ones; 0 when both are unknown.
        public static double PairSize(double a, double b)
        {
            bool okA = a > 0 && !double.IsInfinity(a), okB = b > 0 && !double.IsInfinity(b);
            if (okA && okB) return Math.Min(a, b);
            return okA ? a : okB ? b : 0;
        }

        // The target's bounding radius about its centre of mass: the farthest part centre plus
        // PartHalfSize. Unreadable positions are skipped.
        public static double BoundingRadius(IEnumerable<Vector3d> partCentres, Vector3d centre)
        {
            double far = 0;
            if (partCentres != null)
                foreach (var p in partCentres)
                    if (Finite(p)) far = Math.Max(far, (p - centre).magnitude);
            return far + PartHalfSize;
        }

        // Unity and the game's celestial frames are Yup and left-handed, Zup is right-handed:
        // a position or a velocity swaps y and z, an angular velocity (a pseudovector) is also
        // negated, so v = ω × r holds in both.
        public static Vector3d ZupFromYup(Vector3d v) => new Vector3d(v.x, v.z, v.y);
        public static Vector3d ZupAngularFromYup(Vector3d w) => -ZupFromYup(w);

        // The spin about an axis, in °/s; +∞ when unreadable (so the caller refuses).
        public static double SpinAboutAxisDegPerSec(Vector3d angularVelocity, Vector3d axis)
        {
            var a = Unit(axis);
            if (!Finite(angularVelocity) || a.sqrMagnitude == 0) return double.PositiveInfinity;
            return Math.Abs(Vector3d.Dot(angularVelocity, a)) * 180 / Math.PI;
        }

        // null when the target's spin about its port axis allows the docking, else why not.
        public static string SpinRefusal(Vector3d targetAngularVelocity, Vector3d targetPortAxis)
        {
            double spin = SpinAboutAxisDegPerSec(targetAngularVelocity, targetPortAxis);
            if (double.IsInfinity(spin)) return "the target's spin is unreadable";
            return spin > MaxSpinDegPerSec
                ? $"the target spins at {spin:0.00}°/s about its port axis (limit {MaxSpinDegPerSec:0}°/s)"
                : null;
        }

        // The velocity of a point of a rigid body.
        public static Vector3d PointVelocity(Vector3d centreVelocity, Vector3d angularVelocity, Vector3d pointMinusCentre) =>
            centreVelocity + Vector3d.Cross(angularVelocity, pointMinusCentre);

        // The guidance state from both ports' frames and the vessels' motions, all inertial
        // (celestial, Zup) and at the same UT.
        public static DockState State(double ut,
            Vector3d ourCentre, Vector3d ourVelocity, Vector3d ourAngularVelocity,
            Vector3d ourPortFace, Vector3d ourPortAxis, Vector3d ourPortUp,
            Vector3d targetCentre, Vector3d targetVelocity, Vector3d targetAngularVelocity,
            Vector3d targetPortFace, Vector3d targetPortAxis, Vector3d targetPortUp)
        {
            var ourFaceVel = PointVelocity(ourVelocity, ourAngularVelocity, ourPortFace - ourCentre);
            var theirFaceVel = PointVelocity(targetVelocity, targetAngularVelocity, targetPortFace - targetCentre);
            var axis = Unit(targetPortAxis);
            return new DockState
            {
                RelPos = ourPortFace - targetPortFace,
                RelVel = ourFaceVel - theirFaceVel,
                TargetAxis = axis,
                TargetAxisRate = Finite(targetAngularVelocity) ? Vector3d.Cross(targetAngularVelocity, axis) : Vector3d.zero,
                Time = ut,
                TargetCenter = targetCentre - targetPortFace,
                OurAxis = Unit(ourPortAxis),
                OurRoll = Unit(ourPortUp),
                TargetRoll = Unit(targetPortUp),
            };
        }

        // null when both vessels can be read for the docking, else why not. The port axes come
        // from the view's docking transform, which exists only for a loaded vessel.
        public static string LoadedRefusal(bool ourLoaded, bool targetLoaded, bool ourPortRead, bool targetPortRead)
        {
            if (!ourLoaded) return "our vessel is not loaded";
            if (!targetLoaded) return "the target is out of physics range";
            if (!ourPortRead) return "our docking port's transform cannot be read";
            if (!targetPortRead) return "the target docking port's transform cannot be read";
            return null;
        }

        // The other vessels grouped by body: ourBody first, then by name; inside a body, by name.
        // Our own vessel and debris are left out.
        public static List<(string body, List<VesselEntry> vessels)> GroupByBody(IEnumerable<VesselEntry> vessels, string ourBody)
        {
            var groups = new Dictionary<string, List<VesselEntry>>();
            if (vessels != null)
                foreach (var v in vessels)
                {
                    if (v.IsOurs || v.IsDebris || string.IsNullOrEmpty(v.Id)) continue;
                    string body = v.Body ?? "";
                    if (!groups.TryGetValue(body, out var list)) groups[body] = list = new List<VesselEntry>();
                    list.Add(v);
                }
            return groups
                .OrderBy(g => g.Key == ourBody ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Value.OrderBy(v => v.Name ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Id).ToList()))
                .ToList();
        }

        static double Max(params double[] values)
        {
            double m = double.NegativeInfinity;
            foreach (var v in values)
                if (!double.IsNaN(v) && !double.IsInfinity(v)) m = Math.Max(m, v);
            return double.IsNegativeInfinity(m) ? 0 : m;
        }

        static double Positive(double v) => v > 0 && !double.IsInfinity(v) ? v : double.PositiveInfinity;

        static bool Finite(Vector3d v)
        {
            double sum = v.x + v.y + v.z;
            return !double.IsNaN(sum) && !double.IsInfinity(sum);
        }

        static Vector3d Unit(Vector3d v) => Finite(v) && v.magnitude > 1e-9 ? v.normalized : Vector3d.zero;
    }

    // How many thrusters are in each state. Usable = Ready (can fire, not firing) or Operational
    // (firing): the API documents Operational as "actively firing", so a thruster at rest that can
    // fire reads Ready. Pure, tested in EditMode.
    public struct RcsStatus
    {
        public int Total, Off, Ready, Operational, Deprived, Occluded;
        public int Usable => Ready + Operational;

        public static RcsStatus From(IEnumerable<Data_RCS.ThrusterStatusState> statuses)
        {
            var s = new RcsStatus();
            if (statuses == null) return s;
            foreach (var st in statuses)
            {
                s.Total++;
                switch (st)
                {
                    case Data_RCS.ThrusterStatusState.Off: s.Off++; break;
                    case Data_RCS.ThrusterStatusState.Ready: s.Ready++; break;
                    case Data_RCS.ThrusterStatusState.Operational: s.Operational++; break;
                    case Data_RCS.ThrusterStatusState.Deprived: s.Deprived++; break;
                    case Data_RCS.ThrusterStatusState.Occluded: s.Occluded++; break;
                }
            }
            return s;
        }

        public override string ToString() =>
            Total == 0 ? "no RCS thruster"
                : $"{Total} RCS thruster(s): {Ready} ready, {Operational} firing, {Off} off, {Deprived} without propellant, {Occluded} occluded";
    }
}
