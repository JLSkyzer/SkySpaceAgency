using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;

// Point-mass landing simulator for the terminal guidance tests: flat ground at z = 0, local
// frame x east, y north, z up, constant gravity, explicit Euler at a fixed step. The thrust
// points along an attitude that follows the commanded direction with a first-order lag (as
// SAS does in game) and is scaled by FinalDescent.ThrustFactor of the pointing error, as the
// game's throttle is. Shared test helper (Task 7 reuses it).
public static class PointMassSim
{
    public struct Result
    {
        public bool Landed;               // reached the ground before maxTime
        public double Time;               // s
        public double FinalOffsetM;       // horizontal distance to the target at contact
        public double FinalVerticalSpeed; // m/s, negative = down
        public double FinalHorizontalSpeed;
        public double MaxCommandTiltDeg;  // largest commanded tilt from vertical
        public double MaxCommandAccel;    // largest commanded |Accel|
        public double DeltaV;             // m/s of thrust actually applied
        public bool EverDropped;
        public bool DroppedAboveGate;     // dropped while above FinalDescent.FinalHeight
        public string FirstDropReason;
        public double FirstDropMissM;
        public bool HasNaN;               // a command had a NaN component
        public double GateOffsetM;        // state when crossing FinalDescent.FinalHeight, NaN if never
        public double GateHorizontalSpeed;
        public double GateVerticalSpeed;

        public override string ToString() =>
            $"landed {Landed} t {Time:F1} s, offset {FinalOffsetM:F1} m, vz {FinalVerticalSpeed:F2} m/s, vh {FinalHorizontalSpeed:F2} m/s, "
            + $"max tilt {MaxCommandTiltDeg:F1}°, Δv {DeltaV:F1} m/s, dropped {EverDropped} (above gate {DroppedAboveGate}: {FirstDropReason}), "
            + $"gate: offset {GateOffsetM:F1} m, vh {GateHorizontalSpeed:F2} m/s, vz {GateVerticalSpeed:F1} m/s";
    }

    public static Result Run(Vector3d offset, Vector3d velocity, double gravity, double maxAccel, double touchDownSpeed,
        double maxTiltDeg = TerminalGuidance.DefaultMaxTiltDegrees, double attitudeLag = 0, double dt = 0.02, double maxTime = 600)
    {
        return Run(offset, velocity, gravity, maxAccel, attitudeLag, dt, maxTime,
            (p, v) => TerminalGuidance.Compute(p, v, gravity, maxAccel, touchDownSpeed, maxTiltDeg));
    }

    public static Result Run(Vector3d offset, Vector3d velocity, double gravity, double maxAccel,
        double attitudeLag, double dt, double maxTime, Func<Vector3d, Vector3d, Command> guidance)
    {
        var r = new Result { GateOffsetM = double.NaN, GateHorizontalSpeed = double.NaN, GateVerticalSpeed = double.NaN };
        Vector3d p = offset, v = velocity;
        Vector3d attitude = new Vector3d(0, 0, 1);
        double t = 0;
        while (p.z > 0 && t < maxTime)
        {
            Command c = guidance(p, v);
            Vector3d a = c.Accel;
            if (double.IsNaN(a.x) || double.IsNaN(a.y) || double.IsNaN(a.z))
            {
                r.HasNaN = true;
                break;
            }
            if (c.TargetDropped)
            {
                if (!r.EverDropped)
                {
                    r.FirstDropReason = c.Reason;
                    r.FirstDropMissM = c.PredictedMissM;
                }
                r.EverDropped = true;
                if (p.z > FinalDescent.FinalHeight)
                    r.DroppedAboveGate = true;
            }
            double magnitude = a.magnitude;
            r.MaxCommandAccel = Math.Max(r.MaxCommandAccel, magnitude);
            Vector3d wanted = attitude;
            if (magnitude > 1e-9)
            {
                wanted = a * (1 / magnitude);
                double tilt = Math.Atan2(Math.Sqrt(a.x * a.x + a.y * a.y), a.z) * 180 / Math.PI;
                r.MaxCommandTiltDeg = Math.Max(r.MaxCommandTiltDeg, tilt);
            }
            attitude = attitudeLag > 0 ? (attitude + (wanted - attitude) * Math.Min(1, dt / attitudeLag)).normalized : wanted;
            double error = Math.Acos(Math.Max(-1, Math.Min(1, Vector3d.Dot(attitude, wanted)))) * 180 / Math.PI;
            double applied = magnitude * (attitudeLag > 0 ? FinalDescent.ThrustFactor(error) : 1);
            r.DeltaV += applied * dt;

            double zBefore = p.z;
            v = v + (attitude * applied + new Vector3d(0, 0, -gravity)) * dt;
            p = p + v * dt;
            t += dt;
            if (double.IsNaN(r.GateOffsetM) && zBefore >= FinalDescent.FinalHeight && p.z < FinalDescent.FinalHeight)
            {
                r.GateOffsetM = Math.Sqrt(p.x * p.x + p.y * p.y);
                r.GateHorizontalSpeed = Math.Sqrt(v.x * v.x + v.y * v.y);
                r.GateVerticalSpeed = v.z;
            }
        }
        r.Landed = p.z <= 0;
        r.Time = t;
        r.FinalOffsetM = Math.Sqrt(p.x * p.x + p.y * p.y);
        r.FinalVerticalSpeed = v.z;
        r.FinalHorizontalSpeed = Math.Sqrt(v.x * v.x + v.y * v.y);
        return r;
    }
}
