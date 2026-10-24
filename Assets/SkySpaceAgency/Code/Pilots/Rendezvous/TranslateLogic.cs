using System;
using System.Collections.Generic;
using KSP.Sim;

namespace K2D2.Rendezvous
{
    // The translation controller's pure part (Translate.cs is the game glue). Vectors are in one
    // inertial frame (the caller uses the body's celestial frame, Zup), times are UT seconds.
    // Pure code, tested in EditMode (TranslateTests).

    // Our port's frame: Forward = its outward axis, Up = its roll reference, Right = Up × Forward
    // (so Forward = Right × Up, as RcsMapping's axes 0, 1, 2). Body-fixed: the RCS mapping and
    // the main engine's thrust axis are expressed in it.
    public struct PortFrame
    {
        public Vector3d Right, Up, Forward;
        public bool Ok;

        public static PortFrame From(Vector3d axis, Vector3d roll)
        {
            var f = new PortFrame();
            if (!Finite(axis) || !Finite(roll) || axis.magnitude < 1e-9) return f;
            f.Forward = axis.normalized;
            Vector3d up = roll - f.Forward * Vector3d.Dot(roll, f.Forward);
            if (up.magnitude < 1e-6) return f;
            f.Up = up.normalized;
            f.Right = Vector3d.Cross(f.Up, f.Forward).normalized;
            f.Ok = true;
            return f;
        }

        public Vector3d Axis(int k) => k == RcsMapping.Right ? Right : k == RcsMapping.Up ? Up : Forward;
        public Vector3d ToWorld(Vector3d local) => Right * local.x + Up * local.y + Forward * local.z;
        public Vector3d ToLocal(Vector3d w) => new Vector3d(Vector3d.Dot(w, Right), Vector3d.Dot(w, Up), Vector3d.Dot(w, Forward));

        internal static bool Finite(Vector3d v)
        {
            double s = v.x + v.y + v.z;
            return !double.IsNaN(s) && !double.IsInfinity(s);
        }
    }

    // The RCS inputs for one frame, as KSPVessel.X, .Y, .Z (the probe measured those).
    public struct RcsCommand
    {
        public float X, Y, Z;
        public Vector3d Uncovered;   // the part of the Δv error along axes no live input moves
        public bool Saturated;       // an input was clamped to ±1
    }

    // A wanted Δv (the velocity error) to the RCS inputs, through the measured mapping:
    // input = sign · component / (accel · Tau), clamped to ±1, zero inside Deadband.
    public static class RcsAllocation
    {
        public const double Tau = 1.0;          // s: the error is taken out over about a second
        public const double Deadband = 0.005;   // m/s per axis: no input below this

        // accel: m/s² per input at full deflection (RcsProbeSequence.Accel).
        public static RcsCommand Allocate(RcsMapping m, double[] accel, Vector3d dvError, PortFrame f,
            double tau = Tau, double deadband = Deadband)
        {
            var c = new RcsCommand { Uncovered = Vector3d.zero };
            var inputs = new float[3];
            if (m == null || !m.Ok || !f.Ok || !PortFrame.Finite(dvError)) return c;
            for (int k = 0; k < 3; k++)
            {
                Vector3d axis = f.Axis(k);
                double comp = Vector3d.Dot(dvError, axis);
                int input = -1;
                for (int i = 0; i < 3; i++)
                    if (!m.Dead[i] && m.AxisOf[i] == k) input = i;
                if (input < 0 || !(accel[input] > 0))
                {
                    c.Uncovered += axis * comp;
                    continue;
                }
                if (Math.Abs(comp) < deadband) continue;
                double u = m.Sign[input] * comp / (accel[input] * tau);
                if (Math.Abs(u) > 1) { u = Math.Sign(u); c.Saturated = true; }
                inputs[input] = (float)u;
            }
            c.X = inputs[0]; c.Y = inputs[1]; c.Z = inputs[2];
            return c;
        }
    }

    // The RCS probe: for each input X, Y, Z a 0.5 s pulse at +1, a 0.5 s settle, then the Δv
    // measured; a pulse at −1 of the same length cancels it (so the vessel ends at rest), and a
    // settle. Three measuring pulses in all. Then RcsMapping.FromResponses, and each input's
    // acceleration (|Δv| / time on) for the allocation's gain.
    public class RcsProbeSequence
    {
        public const double PulseSeconds = 0.5;
        public const double SettleSeconds = 0.5;
        public const double MinResponse = 0.002;   // m/s, the velocity read's noise floor (assumed)

        enum Part { Pulse, Settle, Cancel, CancelSettle }

        int input;
        Part part;
        double partStart = double.NaN;
        double onTime;
        Vector3d before;
        readonly Vector3d[] dv = new Vector3d[3];

        public bool Done { get; private set; }
        public RcsMapping Mapping { get; private set; }
        public double[] Accel { get; } = new double[3];
        public Vector3d[] Responses => dv;

        // t: now; relVel: our velocity relative to the target (its change is our Δv); frame: our
        // port now. Returns the inputs (X, Y, Z) to apply until the next call.
        public float[] Step(double t, Vector3d relVel, PortFrame frame)
        {
            var outIn = new float[3];
            if (Done) return outIn;
            if (double.IsNaN(partStart)) { partStart = t; before = relVel; part = Part.Pulse; }
            double el = t - partStart;
            switch (part)
            {
                case Part.Pulse:
                    if (el < PulseSeconds) { outIn[input] = 1; return outIn; }
                    onTime = el;
                    Next(Part.Settle, t);
                    return outIn;
                case Part.Settle:
                    if (el < SettleSeconds) return outIn;
                    dv[input] = relVel - before;
                    Accel[input] = onTime > 0 ? dv[input].magnitude / onTime : 0;
                    Next(Part.Cancel, t);
                    outIn[input] = -1;
                    return outIn;
                case Part.Cancel:
                    if (el < onTime) { outIn[input] = -1; return outIn; }
                    Next(Part.CancelSettle, t);
                    return outIn;
                case Part.CancelSettle:
                    if (el < SettleSeconds) return outIn;
                    if (++input < 3)
                    {
                        before = relVel;
                        Next(Part.Pulse, t);
                        outIn[input] = 1;
                        return outIn;
                    }
                    Done = true;
                    Mapping = RcsMapping.FromResponses(dv[0], dv[1], dv[2], frame.Right, frame.Up, frame.Forward, MinResponse);
                    for (int i = 0; i < 3; i++)
                        if (Mapping.Dead[i]) Accel[i] = 0;
                    return outIn;
            }
            return outIn;
        }

        void Next(Part p, double t) { part = p; partStart = t; }

        public string Describe()
        {
            if (Mapping == null) return "not probed";
            string[] names = { "X", "Y", "Z" }, axes = { "right", "up", "forward" };
            var parts = new List<string>();
            for (int i = 0; i < 3; i++)
                parts.Add(Mapping.Dead[i]
                    ? $"{names[i]} dead (Δv {dv[i].magnitude:n4} m/s)"
                    : $"{names[i]} → {(Mapping.Sign[i] > 0 ? "+" : "-")}{axes[Mapping.AxisOf[i]]} {Accel[i]:n3} m/s²");
            return string.Join(", ", parts) + (Mapping.Ok ? "" : $"; refused: {Mapping.Refusal}");
        }
    }

    public enum TranslateMode { Rcs, RcsWithEngine, MainEngine, Refused }

    public static class TranslateModes
    {
        public const string NoRcsNote = "No RCS: docking with the main engine, less precise";

        // rcsUsable: the RCS group can be on and a thruster is usable; mapping: the probe's (null
        // when not probed); engineActive: an engine is ignited, never staged for this.
        public static TranslateMode Choose(bool rcsUsable, RcsMapping mapping, bool engineActive, out string note)
        {
            note = null;
            if (rcsUsable && mapping != null && mapping.Ok)
            {
                var dead = new List<string>();
                string[] names = { "X", "Y", "Z" };
                for (int i = 0; i < 3; i++)
                    if (mapping.Dead[i]) dead.Add(names[i]);
                if (dead.Count == 0) return TranslateMode.Rcs;
                string which = string.Join(", ", dead);
                if (engineActive)
                {
                    note = $"RCS input {which} moves nothing: the main engine covers that axis";
                    return TranslateMode.RcsWithEngine;
                }
                note = $"RCS input {which} moves nothing and no engine is active: cannot dock";
                return TranslateMode.Refused;
            }
            if (engineActive)
            {
                note = !rcsUsable || mapping == null ? NoRcsNote : $"{NoRcsNote} ({mapping.Refusal})";
                return TranslateMode.MainEngine;
            }
            note = !rcsUsable || mapping == null
                ? "No RCS and no active engine: cannot dock"
                : $"RCS unusable ({mapping.Refusal}) and no active engine: cannot dock";
            return TranslateMode.Refused;
        }
    }

    // The throttle law shared by the engine probe and the sequence: proportional to the Δv left,
    // taken out over ThrottleTau, so the last frames are as fine as the engine allows.
    public static class FineThrottle
    {
        public const double ThrottleTau = 0.25;   // s

        public static double For(double left, double maxAccel) =>
            maxAccel > 0 && left > 0 ? Math.Min(1, left / (maxAccel * ThrottleTau)) : 0;
    }

    // The main-engine probe: a closed-loop burn of ProbeDv along whatever the engine pushes, the
    // attitude held. Measures the thrust axis in our port frame and the residual (how finely the
    // engine stops: the first sample of the achieved deadband). Refused without a response.
    public class EngineProbe
    {
        public const double ProbeDv = 0.03;          // m/s
        public const double SettleSeconds = 0.5;
        public const double TimeoutSeconds = 10;
        public const double MinResponse = 0.002;     // m/s, as the RCS probe

        double start = double.NaN, cut = double.NaN;
        Vector3d v0;

        public bool Done { get; private set; }
        public bool Ok { get; private set; }
        public string Refusal { get; private set; }
        public Vector3d ThrustLocal { get; private set; }   // unit, in the port frame
        public Vector3d Response { get; private set; }      // the measured Δv, inertial
        public double Residual { get; private set; }        // m/s, | |Δv| − ProbeDv |

        // Returns the throttle until the next call.
        public double Step(double t, Vector3d relVel, PortFrame frame, double maxAccel)
        {
            if (Done) return 0;
            if (double.IsNaN(start)) { start = t; v0 = relVel; }
            if (double.IsNaN(cut))
            {
                double left = ProbeDv - (relVel - v0).magnitude;
                if (left > MainEngineSequence.FinishTolerance && t - start < TimeoutSeconds && maxAccel > 0)
                    return FineThrottle.For(left, maxAccel);
                cut = t;
                return 0;
            }
            if (t - cut < SettleSeconds) return 0;

            Done = true;
            Response = relVel - v0;
            Residual = Math.Abs(Response.magnitude - ProbeDv);
            if (!(maxAccel > 0))
                Refusal = "no active engine";
            else if (!(Response.magnitude >= MinResponse) || !frame.Ok)
                Refusal = $"the main engine gave {Response.magnitude:n4} m/s in {cut - start:n1} s";
            else
            {
                ThrustLocal = frame.ToLocal(Response).normalized;
                Ok = true;
            }
            return 0;
        }
    }

    public enum EnginePhase { Idle, Turning, Burning, TurningBack }

    public struct EngineStep
    {
        public double Throttle;
        public bool AimBurn;       // true: put the thrust on BurnDir; false: hold the port attitude
        public Vector3d BurnDir;   // inertial, unit
    }

    // Translation with the main engine: when the velocity error exceeds TriggerDeadband, capture
    // the wanted velocity, turn the thrust onto the error (no turn when it is already within
    // AlignDeg), burn closed-loop along that direction with FineThrottle until within
    // FinishTolerance, then turn back to the port attitude. As DockSim's main-engine model.
    public class MainEngineSequence
    {
        // The docking simulation assumes the engine holds 5 mm/s (DockSim.Deadband).
        public const double AssumedDeadband = 0.005;
        public const double FinishTolerance = 0.25 * AssumedDeadband;
        public const double AlignDeg = 1.0;           // thrust on the burn within this to start
        public const double BurnAbortDeg = 5.0;       // off by more while burning: cut, turn again
        public const double SettledRateDeg = 0.5;     // °/s, the turn has stopped
        public const double TurnTimeout = 90;         // s, one turn (a half turn at 2°/s)
        public const double BurnMargin = 10;          // s over the ideal burn time before giving up

        public double TriggerDeadband = AssumedDeadband;

        public EnginePhase Phase { get; private set; } = EnginePhase.Idle;
        public string Failure { get; private set; }
        public int Burns { get; private set; }
        public double LastResidual { get; private set; }
        public double WorstResidual { get; private set; }
        public double LastAsked { get; private set; }   // m/s, the last burn's planned Δv
        public double LastFlown { get; private set; }   // m/s, what it changed along its direction
        public double MinThrottleSeen { get; private set; } = 1;

        double phaseStart;
        Vector3d burnTarget, burnDir, burnStartVel;

        // thrustDir: the engine's thrust now, inertial; portAxis: our port axis now; wantedForward:
        // the port axis to turn back to (zero: none); angularRateDeg: our turn rate, °/s.
        public EngineStep Step(double t, Vector3d relVel, Vector3d wantedRelVel, Vector3d thrustDir,
            Vector3d portAxis, Vector3d wantedForward, double angularRateDeg, double maxAccel)
        {
            var s = new EngineStep { BurnDir = burnDir };
            if (Failure != null) return s;
            if (!(maxAccel > 0)) { Failure = "no active engine to translate with"; return s; }
            bool settled = angularRateDeg <= SettledRateDeg;

            switch (Phase)
            {
                case EnginePhase.Idle:
                {
                    Vector3d err = wantedRelVel - relVel;
                    if (!(err.magnitude > TriggerDeadband)) return s;
                    burnTarget = wantedRelVel;
                    burnDir = err.normalized;
                    burnStartVel = relVel;
                    LastAsked = err.magnitude;
                    s.BurnDir = burnDir;
                    s.AimBurn = true;
                    if (Angle(thrustDir, burnDir) <= AlignDeg && settled)
                        return Burn(t, relVel, maxAccel, s, starting: true);
                    Enter(EnginePhase.Turning, t);
                    return s;
                }
                case EnginePhase.Turning:
                    s.AimBurn = true;
                    if (Angle(thrustDir, burnDir) <= AlignDeg && settled)
                        return Burn(t, relVel, maxAccel, s, starting: true);
                    if (t - phaseStart > TurnTimeout)
                        Failure = $"could not turn the engine onto the burn in {TurnTimeout:n0} s";
                    return s;
                case EnginePhase.Burning:
                    s.AimBurn = true;
                    if (Angle(thrustDir, burnDir) > BurnAbortDeg)
                    {
                        Enter(EnginePhase.Turning, t);
                        return s;
                    }
                    return Burn(t, relVel, maxAccel, s, starting: false);
                case EnginePhase.TurningBack:
                    if (wantedForward.sqrMagnitude == 0 || (Angle(portAxis, wantedForward) <= 2 * AlignDeg && settled))
                    {
                        Enter(EnginePhase.Idle, t);
                        return s;
                    }
                    if (t - phaseStart > TurnTimeout)
                        Failure = $"could not turn back to the port attitude in {TurnTimeout:n0} s";
                    return s;
            }
            return s;
        }

        EngineStep Burn(double t, Vector3d relVel, double maxAccel, EngineStep s, bool starting)
        {
            if (starting) Enter(EnginePhase.Burning, t);
            double left = Vector3d.Dot(burnTarget - relVel, burnDir);
            double ideal = LastAsked / maxAccel;
            if (left > FinishTolerance && t - phaseStart < ideal + BurnMargin)
            {
                s.Throttle = FineThrottle.For(left, maxAccel);
                MinThrottleSeen = Math.Min(MinThrottleSeen, s.Throttle);
                return s;
            }
            // Done (or out of time): what is left, all directions, is the precision reached.
            Burns++;
            LastResidual = (burnTarget - relVel).magnitude;
            WorstResidual = Math.Max(WorstResidual, LastResidual);
            LastFlown = Vector3d.Dot(relVel - burnStartVel, burnDir);
            s.Throttle = 0;
            s.AimBurn = false;
            Enter(EnginePhase.TurningBack, t);
            return s;
        }

        void Enter(EnginePhase p, double t) { Phase = p; phaseStart = t; }

        static double Angle(Vector3d a, Vector3d b)
        {
            if (a.sqrMagnitude == 0 || b.sqrMagnitude == 0) return 180;
            double c = Vector3d.Dot(a.normalized, b.normalized);
            return Math.Acos(Math.Max(-1, Math.Min(1, c))) * 180 / Math.PI;
        }

        // The approach was tuned for AssumedDeadband. A coarser engine leaves a larger residual
        // velocity at the start of the final coast, so the lateral tolerance there (ContactLateral)
        // scales with the precision reached, up to 0.5 m (ContactLateralFor's own cap; beyond it
        // the port would not capture). Only main-engine limits change. note: null when unchanged.
        public static DockLimits ForDeadband(DockLimits limits, double achieved, out string note)
        {
            note = null;
            if (!limits.MainEngineMode || !(achieved > AssumedDeadband)) return limits;
            double scale = achieved / AssumedDeadband;
            var wide = limits;
            wide.ContactLateral = Math.Min(0.5, limits.ContactLateral * scale);
            wide.HoldTolerance = limits.HoldTolerance * Math.Min(scale, 3);
            wide.HoldSpeed = Math.Max(limits.HoldSpeed, 3 * achieved);
            note = $"main-engine deadband {achieved:n4} m/s, {scale:n1} × the {AssumedDeadband:n3} m/s assumed: " +
                $"contact tolerance {limits.ContactLateral:n2} → {wide.ContactLateral:n2} m, hold tolerance " +
                $"{limits.HoldTolerance:n1} → {wide.HoldTolerance:n1} m, hold speed {limits.HoldSpeed:n2} → {wide.HoldSpeed:n2} m/s" +
                (limits.ContactLateral * scale > 0.5 ? " (capped: the coast may drift off the port and back off)" : "");
            return wide;
        }
    }

    // Attitude math: SAS holds a whole-vessel rotation, and what must point is our port (or the
    // engine). The port-to-vessel offset is read, not assumed.
    public static class AttitudeAim
    {
        // The vessel nose and dorsal that put our port's axis on wantedAxis and its roll on
        // wantedRoll (zero: keep the roll as near as the new axis allows), keeping the current
        // offset between the vessel's axes (nose, dorsal) and the port frame. Same frame for all.
        public static bool VesselAxes(Vector3d nose, Vector3d dorsal, PortFrame port,
            Vector3d wantedAxis, Vector3d wantedRoll, out Vector3d wantedNose, out Vector3d wantedDorsal)
        {
            wantedNose = wantedDorsal = Vector3d.zero;
            if (!port.Ok) return false;
            Vector3d roll = wantedRoll.sqrMagnitude > 0 ? wantedRoll : port.Up;
            var want = PortFrame.From(wantedAxis, roll);
            if (!want.Ok && wantedRoll.sqrMagnitude == 0)
                want = PortFrame.From(wantedAxis, port.Right);   // the new axis is along the old roll
            if (!want.Ok) return false;
            wantedNose = want.ToWorld(port.ToLocal(nose));
            wantedDorsal = want.ToWorld(port.ToLocal(dorsal));
            return true;
        }

        // The port attitude that puts the thrust (thrustLocal, in the port frame) on burnDir with
        // the smallest rotation. A half turn goes about the port's up (or right).
        public static void BurnPortAxes(PortFrame port, Vector3d thrustLocal, Vector3d burnDir,
            out Vector3d axis, out Vector3d roll)
        {
            Vector3d from = port.ToWorld(thrustLocal).normalized, to = burnDir.normalized;
            Vector3d k = Vector3d.Cross(from, to);
            double c = Math.Max(-1, Math.Min(1, Vector3d.Dot(from, to)));
            double angle = Math.Acos(c);
            if (k.magnitude < 1e-9)
            {
                if (c > 0) { axis = port.Forward; roll = port.Up; return; }
                k = Vector3d.Cross(from, port.Up);
                if (k.magnitude < 1e-6) k = Vector3d.Cross(from, port.Right);
            }
            k = k.normalized;
            axis = Rotate(port.Forward, k, angle);
            roll = Rotate(port.Up, k, angle);
        }

        static Vector3d Rotate(Vector3d v, Vector3d k, double a) =>
            v * Math.Cos(a) + Vector3d.Cross(k, v) * Math.Sin(a) + k * (Vector3d.Dot(k, v) * (1 - Math.Cos(a)));

        // A rotation whose local Y ("nose") is nose and local Z (dorsal) is dorsal, in a Unity
        // (Yup) frame: the same build as Ascent's roll program (Shepperd's method).
        public static QuaternionD RotationFromNoseAndDorsal(Vector3d nose, Vector3d dorsal)
        {
            Vector3d ey = nose.normalized;
            Vector3d ez = (dorsal - ey * Vector3d.Dot(dorsal, ey)).normalized;
            Vector3d ex = Vector3d.Cross(ey, ez).normalized;
            double m00 = ex.x, m10 = ex.y, m20 = ex.z;
            double m01 = ey.x, m11 = ey.y, m21 = ey.z;
            double m02 = ez.x, m12 = ez.y, m22 = ez.z;
            double trace = m00 + m11 + m22, qx, qy, qz, qw;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1.0) * 2.0;
                qw = s / 4.0; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2.0;
                qw = (m21 - m12) / s; qx = s / 4.0; qy = (m01 + m10) / s; qz = (m02 + m20) / s;
            }
            else if (m11 > m22)
            {
                double s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2.0;
                qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = s / 4.0; qz = (m12 + m21) / s;
            }
            else
            {
                double s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2.0;
                qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = s / 4.0;
            }
            return new QuaternionD(new Vector3d(qx, qy, qz), qw);
        }
    }
}
