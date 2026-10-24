using System;
using KSP.Sim;

namespace K2D2.Rendezvous
{
    // Hold50, Approach10 and Hold10 are named after the RCS distances: the real ones are
    // DockLimits.HoldFar and HoldNear (100 m and 20 m with the main engine).
    public enum DockPhase { Align, Hold50, Approach10, Hold10, Final, Backoff, Docked, Aborted }

    // What the guidance sees, every step. Inertial vectors (any frame that does not rotate), all
    // relative to the target port's face.
    public struct DockState
    {
        public Vector3d RelPos;          // our port face minus the target port face
        public Vector3d RelVel;          // our velocity minus the target port face's
        public Vector3d TargetAxis;      // the target port's outward axis (normalised here)
        public Vector3d TargetAxisRate;  // d(TargetAxis)/dt: the target's turn, about the port face
        public double Time;              // UT, for the caller's logs; the holds are timed with dt

        // Added to the plan's interface, all needed by the behaviour it asks for:
        public Vector3d TargetCenter;    // the target's centre of mass minus its port face (keep-out)
        public Vector3d OurAxis;         // our port's outward axis; zero when unknown (never aligned)
        public Vector3d OurRoll;         // our port's roll reference; zero when unknown
        public Vector3d TargetRoll;      // the target port's roll reference; zero when unknown
    }

    // The approach limits. Build them with Rcs or MainEngine; the capture values come from the
    // target port's Data_DockingNode.
    public struct DockLimits
    {
        public double HoldFar;            // m, the first hold point on the target port's axis
        public double HoldNear;           // m, the second one
        public double FinalSpeedMin;      // m/s, the final approach never slows below this
        public double FinalSpeedMax;      // m/s, nor goes faster than this (or 0.9 × CaptureMaxRvel)
        public double CaptureMinFwdDot;   // our port axis · −target axis needed to hold and to dock
        public double CaptureMaxRvel;     // m/s, the closing speed the port captures at
        public double MinRollDot;         // 0 = any roll; else our roll · the target's roll needed
        public double ConeHalfAngleDeg;   // the approach cone, from the port face
        public bool MainEngineMode;

        public double TargetRadius;       // m; keep-out sphere = TargetRadius + KeepOutMargin around its centre
        public double MaxSpeed;           // m/s, the speed profile's cap
        public double SpeedGain;          // k in v = min(vmax, k·sqrt(d)): a constant deceleration k²/2
        public double LinearGain;         // 1/s, the profile near a point (v ≤ gain·d): no chatter at d → 0
        public double LateralGain;        // 1/s, toward the axis in the corridor
        public double HoldTolerance;      // m, how close to a hold point counts as there
        public double HoldSpeed;          // m/s, how slow counts as settled
        public double HoldSeconds;        // s, settled and aligned this long ends a hold
        public double LateralMargin;      // m, added to the cone, and the lateral error allowed at contact
        public double BackoffDistance;    // m, where an abort backs off to

        public const double KeepOutMargin = 10;
        public const double ContactDistance = 0.1;   // m from the face: the contact check
        public const double CoastDistance = 10;      // m, main engine: no correction closer than this

        public static DockLimits Rcs(double captureMinFwdDot, double captureMaxRvel, double minRollDot, double targetRadius)
        {
            return new DockLimits
            {
                HoldFar = 50, HoldNear = 10, FinalSpeedMin = 0.2, FinalSpeedMax = 0.5,
                CaptureMinFwdDot = captureMinFwdDot, CaptureMaxRvel = captureMaxRvel, MinRollDot = minRollDot,
                ConeHalfAngleDeg = 10, MainEngineMode = false, TargetRadius = targetRadius,
                MaxSpeed = 1.0, SpeedGain = 0.14, LinearGain = 0.2, LateralGain = 0.1,
                HoldTolerance = 1, HoldSpeed = 0.05, HoldSeconds = 5, LateralMargin = 0.5, BackoffDistance = 20,
            };
        }

        // Without RCS the main engine translates: turn, short burn, turn back. The holds are farther
        // out, the tolerances are wider, and the final approach is a coast at a constant 0.3 m/s: a
        // change of the wanted velocity there would make the vessel turn away from the port. Its
        // last CoastDistance metres get no lateral correction either.
        public static DockLimits MainEngine(double captureMinFwdDot, double captureMaxRvel, double minRollDot, double targetRadius)
        {
            return new DockLimits
            {
                HoldFar = 100, HoldNear = 20, FinalSpeedMin = 0.3, FinalSpeedMax = 0.3,
                CaptureMinFwdDot = captureMinFwdDot, CaptureMaxRvel = captureMaxRvel, MinRollDot = minRollDot,
                ConeHalfAngleDeg = 20, MainEngineMode = true, TargetRadius = targetRadius,
                MaxSpeed = 1.0, SpeedGain = 0.1, LinearGain = 0.05, LateralGain = 0.05,
                HoldTolerance = 3, HoldSpeed = 0.15, HoldSeconds = 5, LateralMargin = 1.5, BackoffDistance = 20,
            };
        }
    }

    public struct DockCommand
    {
        public Vector3d WantedRelVel;   // inertial, relative to the target port face
        public Vector3d Forward;        // our port axis wanted: −TargetAxis (zero when the state is unreadable)
        public Vector3d Up;             // our roll reference wanted: the target's roll; zero = any roll
        public Vector3d AimPoint;       // relative to the target port face, for the logs
        public DockPhase Phase;
    }

    // The docking approach: align on the target port's axis at HoldFar, hold, approach to HoldNear,
    // hold, then the final approach at FinalSpeedMin..max inside the cone. On the far side of the
    // target it goes around a keep-out sphere to the first hold point (Review Focus 3).
    // An abort (lateral error outside the cone, closing too fast, contact outside the capture
    // limits) backs off along the axis to BackoffDistance and retries; a second abort gives up.
    //
    // Everything is computed in the target port's frame: it turns with the target (ω = a × ȧ, the
    // spin about the port axis itself is not seen and is the caller's refusal above 1°/s), so the
    // wanted velocity is the co-rotating ω × r plus the approach terms.
    // Pure code, tested in EditMode against DockSim.
    public class DockingGuidance
    {
        public DockingGuidance(DockLimits limits) { Limits = limits; }

        public DockLimits Limits { get; }
        public DockPhase Phase { get; private set; } = DockPhase.Align;
        public int Aborts { get; private set; }
        public string LastAbortReason { get; private set; }

        double settled;   // s spent settled at the current hold point

        // True once the docking must stop: the second abort. The first one only backs off.
        public bool AbortNeeded(out string reason)
        {
            reason = Phase == DockPhase.Aborted ? LastAbortReason : null;
            return Phase == DockPhase.Aborted;
        }

        public DockCommand Step(DockState s, double dt)
        {
            if (!(dt > 0) || double.IsInfinity(dt)) dt = 0;
            dt = Math.Min(dt, 1);
            var L = Limits;

            if (!Finite(s.RelPos) || !Finite(s.RelVel) || !Finite(s.TargetAxis) || s.TargetAxis.magnitude < 1e-6
                || Math.Abs(s.RelPos.x) + Math.Abs(s.RelPos.y) + Math.Abs(s.RelPos.z) > 1e9)
                return new DockCommand { Phase = Phase };   // unreadable: no command, no phase change

            var a = s.TargetAxis.normalized;
            var r = s.RelPos;
            var adot = Finite(s.TargetAxisRate) ? s.TargetAxisRate : Vector3d.zero;
            var omega = Vector3d.Cross(a, adot);
            var coRot = Vector3d.Cross(omega, r);
            var vRot = s.RelVel - coRot;
            double ax = Vector3d.Dot(r, a);
            var lat = r - ax * a;
            double closing = -Vector3d.Dot(vRot, a);
            var center = Finite(s.TargetCenter) ? s.TargetCenter : Vector3d.zero;

            bool aligned = Aligned(s, a);
            Vector3d aim, approach;

            switch (Phase)
            {
                case DockPhase.Align:
                {
                    var hold = L.HoldFar * a;
                    aim = RouteAround(r, hold, center, a);
                    approach = PointApproach(r, aim);
                    if ((r - hold).magnitude < L.HoldTolerance)
                        Enter(DockPhase.Hold50);
                    break;
                }
                case DockPhase.Hold50:
                {
                    aim = L.HoldFar * a;
                    approach = PointApproach(r, aim);
                    double off = (r - aim).magnitude;
                    if (off > 3 * L.HoldTolerance)
                        Enter(DockPhase.Align);
                    else if (Settle(off, vRot, aligned, dt))
                        Enter(DockPhase.Approach10);
                    break;
                }
                case DockPhase.Approach10:
                case DockPhase.Hold10:
                {
                    aim = L.HoldNear * a;
                    approach = Corridor(ax, lat, L.HoldNear, a);
                    if (CheckAbort(ax, lat.magnitude, closing, ApproachSpeedLimit(ax)))
                        break;
                    double off = (r - aim).magnitude;
                    if (Phase == DockPhase.Approach10 && off < L.HoldTolerance)
                        Enter(DockPhase.Hold10);
                    else if (Phase == DockPhase.Hold10 && Settle(off, vRot, aligned, dt))
                        Enter(DockPhase.Final);
                    break;
                }
                case DockPhase.Final:
                {
                    aim = Vector3d.zero;
                    double v = Math.Max(L.FinalSpeedMin, Math.Min(FinalCap, L.SpeedGain * Math.Sqrt(Math.Max(ax, 0))));
                    approach = -v * a;
                    if (!L.MainEngineMode || ax > DockLimits.CoastDistance)
                        approach -= L.LateralGain * lat;
                    if (CheckAbort(ax, lat.magnitude, closing, FinalSpeedLimit))
                        break;
                    if (ax <= DockLimits.ContactDistance)
                    {
                        if (closing > L.CaptureMaxRvel)
                            Abort($"contact at {closing:F2} m/s, over the port's {L.CaptureMaxRvel:F2} m/s");
                        else if (!aligned)
                            Abort("contact with the ports misaligned");
                        else if (lat.magnitude > L.LateralMargin)
                            Abort($"contact {lat.magnitude:F2} m off the axis");
                        else
                            Enter(DockPhase.Docked);
                    }
                    break;
                }
                case DockPhase.Backoff:
                {
                    aim = L.BackoffDistance * a;
                    approach = Corridor(ax, lat, L.BackoffDistance, a);
                    double off = (r - aim).magnitude;
                    if (off < L.HoldTolerance && vRot.magnitude < Math.Max(L.HoldSpeed, 0.1))
                        Enter(L.BackoffDistance > L.HoldNear + L.HoldTolerance ? DockPhase.Approach10 : DockPhase.Hold10);
                    break;
                }
                default:   // Docked, Aborted: keep station with the port
                    aim = r;
                    approach = Vector3d.zero;
                    break;
            }

            // A phase change this step already flies the new phase: an abort backs off at once.
            if (Phase == DockPhase.Backoff)
            {
                aim = L.BackoffDistance * a;
                approach = Corridor(ax, lat, L.BackoffDistance, a);
            }
            else if (Phase == DockPhase.Aborted || Phase == DockPhase.Docked)
            {
                aim = r;
                approach = Vector3d.zero;
            }

            var up = Vector3d.zero;
            if (L.MinRollDot > 0 && Finite(s.TargetRoll))
            {
                var tr = s.TargetRoll - Vector3d.Dot(s.TargetRoll, a) * a;
                if (tr.magnitude > 1e-6) up = tr.normalized;
            }

            var cmd = new DockCommand
            {
                WantedRelVel = coRot + approach,
                Forward = -a,
                Up = up,
                AimPoint = aim,
                Phase = Phase,
            };
            if (!Finite(cmd.WantedRelVel)) cmd.WantedRelVel = Vector3d.zero;
            if (!Finite(cmd.AimPoint)) cmd.AimPoint = Vector3d.zero;
            return cmd;
        }

        double FinalCap => Math.Max(0.01, Math.Min(Limits.FinalSpeedMax, 0.9 * Limits.CaptureMaxRvel));
        double FinalSpeedLimit => Math.Max(1.5 * FinalCap, FinalCap + 0.1);
        double ApproachSpeedLimit(double ax) =>
            Math.Max(FinalSpeedLimit, 1.5 * Profile(Math.Abs(ax - Limits.HoldNear)) + 0.1);

        // v = min(vmax, k·sqrt(d), gain·d)
        double Profile(double d)
        {
            var L = Limits;
            if (!(d > 0)) return 0;
            return Math.Max(0, Math.Min(L.MaxSpeed, Math.Min(L.SpeedGain * Math.Sqrt(d), L.LinearGain * d)));
        }

        Vector3d PointApproach(Vector3d r, Vector3d aim)
        {
            var e = aim - r;
            double d = e.magnitude;
            return d > 1e-9 ? e / d * Profile(d) : Vector3d.zero;
        }

        // Along the axis to the distance aimAx, the lateral error driven toward the axis.
        Vector3d Corridor(double ax, Vector3d lat, double aimAx, Vector3d a)
        {
            double da = aimAx - ax;
            var vLat = -Limits.LateralGain * lat;
            if (vLat.magnitude > Limits.MaxSpeed) vLat = vLat.normalized * Limits.MaxSpeed;
            return Math.Sign(da) * Profile(Math.Abs(da)) * a + vLat;
        }

        // The point to fly to on the way to target: target itself when the straight line stays out
        // of the keep-out sphere (or leaves it, when we start inside), else a point on a circle around
        // the centre, at most 40° ahead of us toward target. Recomputed every step, the path arcs
        // around the target at no less than 1.2 × the keep-out radius.
        Vector3d RouteAround(Vector3d r, Vector3d target, Vector3d c, Vector3d a)
        {
            double rk = Math.Max(0, Limits.TargetRadius) + DockLimits.KeepOutMargin;
            if (Clears(r, target, c, rk)) return target;

            var u = r - c;
            var h = target - c;
            double ru = u.magnitude;
            var un = ru > 1e-6 ? u / ru : -a;
            var hn = h.magnitude > 1e-6 ? h.normalized : a;
            double cos = Math.Max(-1, Math.Min(1, Vector3d.Dot(un, hn)));
            double angle = Math.Acos(cos);
            var k = Vector3d.Cross(un, hn);
            if (k.magnitude < 1e-6) k = Perpendicular(un);
            k = k.normalized;
            double step = Math.Min(angle, 40 * Math.PI / 180);
            var dir = un * Math.Cos(step) + Vector3d.Cross(k, un) * Math.Sin(step);
            return c + dir.normalized * Math.Max(1.2 * rk, ru);
        }

        static bool Clears(Vector3d p, Vector3d q, Vector3d c, double rk)
        {
            var d = q - p;
            double len2 = d.sqrMagnitude;
            if (len2 < 1e-12) return (p - c).magnitude >= rk;
            double t = Vector3d.Dot(c - p, d) / len2;
            if (t <= 0) return true;   // moving away from the centre all along
            t = Math.Min(t, 1);
            return (p + t * d - c).magnitude >= rk;
        }

        static Vector3d Perpendicular(Vector3d v)
        {
            var other = Math.Abs(v.x) < 0.9 ? new Vector3d(1, 0, 0) : new Vector3d(0, 1, 0);
            return Vector3d.Cross(v, other);
        }

        bool Aligned(DockState s, Vector3d a)
        {
            var L = Limits;
            if (!Finite(s.OurAxis) || s.OurAxis.magnitude < 1e-6) return false;
            if (Vector3d.Dot(s.OurAxis.normalized, -a) < L.CaptureMinFwdDot) return false;
            if (L.MinRollDot <= 0) return true;
            if (!Finite(s.OurRoll) || !Finite(s.TargetRoll) || s.OurRoll.magnitude < 1e-6 || s.TargetRoll.magnitude < 1e-6)
                return false;
            return Vector3d.Dot(s.OurRoll.normalized, s.TargetRoll.normalized) >= L.MinRollDot;
        }

        bool Settle(double off, Vector3d vRot, bool aligned, double dt)
        {
            var L = Limits;
            if (off < L.HoldTolerance && vRot.magnitude < L.HoldSpeed && aligned)
                settled += dt;
            else
                settled = 0;
            return settled >= L.HoldSeconds;
        }

        bool CheckAbort(double ax, double lat, double closing, double speedLimit)
        {
            var L = Limits;
            double allowed = Math.Tan(L.ConeHalfAngleDeg * Math.PI / 180) * Math.Max(ax, 0) + L.LateralMargin;
            if (ax < -DockLimits.ContactDistance)
                Abort($"{-ax:F2} m behind the target port's face");
            else if (lat > allowed)
                Abort($"lateral error {lat:F2} m at {ax:F1} m, outside the {L.ConeHalfAngleDeg:F0}° cone ({allowed:F2} m)");
            else if (closing > speedLimit)
                Abort($"closing at {closing:F2} m/s at {ax:F1} m, over the {speedLimit:F2} m/s limit");
            else
                return false;
            return true;
        }

        void Abort(string reason)
        {
            Aborts++;
            LastAbortReason = reason;
            Enter(Aborts > 1 ? DockPhase.Aborted : DockPhase.Backoff);
        }

        void Enter(DockPhase p)
        {
            Phase = p;
            settled = 0;
        }

        static bool Finite(Vector3d v)
        {
            double sum = v.x + v.y + v.z;
            return !double.IsNaN(sum) && !double.IsInfinity(sum);
        }
    }
}
