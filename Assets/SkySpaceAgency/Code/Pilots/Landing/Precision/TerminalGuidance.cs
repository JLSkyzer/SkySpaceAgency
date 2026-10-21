using System;
using K2D2.Landing.Braking;
using KSP.Sim;

namespace K2D2.Landing.Precision
{
    public struct Command
    {
        public Vector3d Accel;       // thrust acceleration only (gravity not included), local frame x east, y north, z up
        public bool TargetDropped;   // this call gave up the target for the safe stop; the caller decides whether to latch it
        public string Reason;        // why the target was dropped; null otherwise
        public double PredictedMissM; // with TargetDropped: horizontal distance from the target to the point now aimed at
    }

    // Terminal guidance from about 1500 m down to FinalDescent.FinalHeight, in a local flat-ground
    // frame (x east, y north, z up, origin at the target on the ground).
    //
    // Vertical channel first: a coast-then-brake profile at the planned deceleration
    // PlanFraction × (maxAccel − g), gravity compensated. Its time-to-go, minus the time of the
    // last FinalHeight metres, is the time-to-go of the horizontal channel: a ZEM/ZEV law
    // a = 6·ZEM/t² − 2·ZEV/t toward rest above the target. The horizontal part only gets what
    // the vertical channel leaves, within maxAccel and the tilt limit. When the target is out of
    // reach in the time left, the law aims at the closest reachable point instead (TargetDropped,
    // with a braking reserve so the drift is gone by FinalHeight). When DescentEnvelope.CanStop
    // fails with the lateral share taken, or thrust cannot hold the vessel up, it thrusts
    // straight up. Below FinalHeight the horizontal channel only nulls the drift, as
    // FinalDescent does in game. Pure maths, tested in EditMode.
    public static class TerminalGuidance
    {
        public const double DefaultMaxTiltDegrees = 45;
        // Share of the net deceleration (maxAccel − g) the vertical profile plans on.
        public const double PlanFraction = 0.7;
        // Floor of the horizontal time-to-go (s): keeps the gains bounded near FinalHeight and
        // the law tolerant of a 1–2 s attitude lag.
        public const double MinHorizontalTimeToGo = 5;
        // Time constant (s) of the drift nulling below FinalHeight; also sets the drift-stop point.
        public const double DriftNullTime = 4;
        // Out-of-reach target: bisection steps for the closest reachable aim, and the share of
        // the lateral authority × time to FinalHeight that the horizontal speed may use, the
        // rest being kept to brake.
        public const int AimBisections = 12;
        public const double BrakeReserve = 0.5;
        // Horizontal speed allowed at FinalHeight (m/s), the speed the give-way aims at below
        // it, and the share of lateral room × time to FinalHeight it may count on before the
        // vertical channel gives way.
        public const double GateSpeed = 3;
        public const double GateSpeedAim = 2;
        public const double GiveWayReserve = 0.7;
        // The profile blends from coast (ratio 0.9) to full braking (ratio 1) of speed over profile speed.
        const double BlendStart = 0.9;
        const double MinHeight = 0.1;   // m, keeps the vertical demand finite at the ground
        const double MinGravity = 1e-3; // m/s², keeps the coast time finite in near-zero gravity

        public static Command Compute(Vector3d offset, Vector3d velocity, double gravity, double maxAccel,
            double touchDownSpeed, double maxTiltDeg = DefaultMaxTiltDegrees)
        {
            if (!Finite(offset) || !Finite(velocity) || !IsFinite(gravity) || gravity < 0 || double.IsNaN(maxAccel))
                return Drop(new Vector3d(0, 0, IsFinite(maxAccel) && maxAccel > 0 ? maxAccel : 0),
                    "Invalid guidance input: target dropped, thrust straight up", double.NaN);
            if (!(maxAccel > gravity))
                return Drop(new Vector3d(0, 0, Math.Max(maxAccel, 0)),
                    $"Thrust too low to land (max accel {maxAccel:F2} m/s² vs gravity {gravity:F2} m/s²): target dropped, full thrust up",
                    Horizontal(offset).magnitude);
            if (double.IsPositiveInfinity(maxAccel))
                maxAccel = 1e6;

            double tilt = Clamp(double.IsNaN(maxTiltDeg) ? DefaultMaxTiltDegrees : maxTiltDeg, 0, 89) * Math.PI / 180;
            double tanTilt = Math.Tan(tilt);
            double g = gravity;
            if (!IsFinite(touchDownSpeed))
                touchDownSpeed = 0;
            double vf = FinalDescent.ContactSpeed(touchDownSpeed);
            double h = Math.Max(offset.z, 0);
            double s = -velocity.z; // descent speed, + down
            double d = PlanFraction * (maxAccel - g);

            // Vertical: gravity plus the deceleration that ends at vf on the ground, once the
            // descent speed reaches the planned profile sqrt(vf² + 2·d·h); a free fall before.
            double need = (s * s - vf * vf) / (2 * Math.Max(h, MinHeight));
            double profile = Math.Sqrt(vf * vf + 2 * d * h);
            double blend = Clamp((s / profile - BlendStart) / (1 - BlendStart), 0, 1);
            double az = Math.Max(0, blend * (g + need));

            // Time-to-go of that profile: coast until it meets the profile, then brake at d.
            double coast = 0, brake;
            if (s >= profile)
                brake = 2 * h / Math.Max(s + vf, 1e-6);
            else
            {
                double gc = Math.Max(g, MinGravity);
                double hb = Math.Max(0, (s * s + 2 * g * h - vf * vf) / (2 * (g + d)));
                double sb = Math.Sqrt(vf * vf + 2 * d * hb);
                coast = Math.Max(0, (sb - s) / gc);
                brake = 2 * hb / (sb + vf);
            }
            double gateSpeed = Math.Sqrt(vf * vf + 2 * d * FinalDescent.FinalHeight);
            double lastLeg = 2 * FinalDescent.FinalHeight / (gateSpeed + vf);
            double t = Math.Max(coast + brake - lastLeg, MinHorizontalTimeToGo);

            Vector3d p = Horizontal(offset);
            Vector3d v = Horizontal(velocity);
            Vector3d ah;
            bool dropped = false;
            string reason = null;
            double miss = 0;
            if (h > FinalDescent.FinalHeight)
            {
                // ZEM/ZEV toward rest above the target: 6·ZEM/t² − 2·ZEV/t with ZEM = −(p + v·t), ZEV = −v.
                Vector3d zem = (p + v * t) * -1;
                double coastShare = Math.Min(coast, t);
                double reachAccel = (coastShare * Lateral(g, maxAccel, tanTilt)
                    + (t - coastShare) * Lateral(g + d, maxAccel, tanTilt)) / t;
                if (!Reachable(zem, v * -1, t, reachAccel))
                {
                    // Closest reachable point: q = (1 − k)·S on the way from the drift-stop point
                    // S to the target, the largest k in [0, 1] that Reachable accepts.
                    dropped = true;
                    Vector3d stop = p + v * DriftNullTime;
                    double lo = 0, hi = 1;
                    for (int i = 0; i < AimBisections; i++)
                    {
                        double k = (lo + hi) / 2;
                        if (Reachable(stop * (1 - k) - (p + v * t), v * -1, t, reachAccel))
                            lo = k;
                        else
                            hi = k;
                    }
                    Vector3d aim = stop * (1 - lo);
                    miss = aim.magnitude;
                    reason = $"Target out of reach: landing about {miss:F0} m from it";
                    ah = (aim - (p + v * t)) * (6 / (t * t)) + v * (2 / t);
                    // The aim sits on the edge of the (approximate) reachable set: keep a braking
                    // reserve so the drift is cancelled by FinalHeight, whatever the aim says.
                    double toGate = Math.Max(0, coast + brake - lastLeg);
                    double speed = v.magnitude;
                    if (speed > BrakeReserve * reachAccel * toGate)
                        ah = v * (-maxAccel / speed); // full lateral braking, capped below
                }
                else
                    ah = zem * (6 / (t * t)) + v * (2 / t);

                // Hand-over at FinalHeight with at most GateSpeed of drift (FinalDescent only
                // leans 15°): when the lateral room of the profile cannot cancel it in the time
                // left, the vertical channel gives way: it thrusts at maxAccel·cos(tilt), where
                // the lateral room is largest, which also slows the descent (more time), and only
                // holds the descent (g) once it is down to the touch-down speed. CanStop still
                // guards below.
                double timeToGate = Math.Max(0, coast + brake - lastLeg);
                double drift = v.magnitude;
                if (drift - GateSpeedAim > GiveWayReserve * Lateral(g + d, maxAccel, tanTilt) * timeToGate)
                {
                    if (dropped && drift > 0)
                        ah = v * (-maxAccel / drift);
                    az = Math.Max(az, s > vf ? Math.Max(g, maxAccel * Math.Cos(tilt)) : g);
                }
            }
            else
                ah = v * (-1 / DriftNullTime);

            // The horizontal part may lift the vertical one up to a hover so that it can lean.
            double demand = ah.magnitude;
            az = Math.Min(Math.Max(az, Math.Min(tanTilt > 0 ? demand / tanTilt : 0, g)), maxAccel);
            double room = Lateral(az, maxAccel, tanTilt);
            if (demand > room)
            {
                ah = demand > 0 ? ah * (room / demand) : ah;
                demand = room;
            }

            // Safety first: with the lateral share taken, full thrust must still stop before the ground.
            double left = Math.Sqrt(Math.Max(0, maxAccel * maxAccel - demand * demand));
            if (s > 0 && !DescentEnvelope.CanStop(s, h, left, g, touchDownSpeed))
            {
                if (!dropped)
                    miss = (p + v * DriftNullTime).magnitude;
                return Drop(new Vector3d(0, 0, maxAccel), "Cannot stop before the ground with the lateral demand: target dropped, braking first", miss);
            }

            return new Command
            {
                Accel = new Vector3d(ah.x, ah.y, az),
                TargetDropped = dropped,
                Reason = reason,
                PredictedMissM = dropped ? miss : 0,
            };
        }

        // Horizontal acceleration available next to a vertical az: within the tilt and maxAccel.
        static double Lateral(double az, double maxAccel, double tanTilt) =>
            Math.Max(0, Math.Min(az * tanTilt, Math.Sqrt(Math.Max(0, maxAccel * maxAccel - az * az))));

        // Whether |a| ≤ accel over t can cancel zem while changing the velocity by zev, along the
        // zem direction: bang-bang (+accel then −accel) bound A·(2·t·t1 − t1² − t²/2) with
        // t1 = (t + w/accel)/2, w the part of zev along zem.
        static bool Reachable(Vector3d zem, Vector3d zev, double t, double accel)
        {
            if (!(accel > 0))
                return zem.magnitude < 1e-9 && zev.magnitude < 1e-9;
            double distance = zem.magnitude;
            if (distance < 1e-9)
                return zev.magnitude <= accel * t;
            double w = Vector3d.Dot(zev, zem) / distance;
            if (Math.Abs(w) > accel * t)
                return false;
            double t1 = (t + w / accel) / 2;
            return distance <= accel * (2 * t * t1 - t1 * t1 - t * t / 2);
        }

        static Command Drop(Vector3d accel, string reason, double miss) =>
            new Command { Accel = accel, TargetDropped = true, Reason = reason, PredictedMissM = miss };

        static Vector3d Horizontal(Vector3d a) => new Vector3d(a.x, a.y, 0);

        static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        static bool Finite(Vector3d a) => IsFinite(a.x) && IsFinite(a.y) && IsFinite(a.z);

        static double Clamp(double x, double lo, double hi) => Math.Max(lo, Math.Min(hi, x));
    }
}
