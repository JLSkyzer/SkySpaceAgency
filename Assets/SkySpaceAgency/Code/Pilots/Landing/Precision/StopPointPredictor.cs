using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Precision
{
    public struct Vehicle
    {
        public double ThrustKN;  // active engines at full throttle
        public double MassT;
        public double Isp;       // s; 0 = unknown (constant mass, pessimistic)
        // Height above the terrain (m) where the predicted brake ends; 0 = BrakeSimulator.SafeAltitude.
        // The precision landing brakes to a stop at start_touchdown_altitude above the target:
        // TerminalGuidance is built for a near-hover hand-over, not for a brake still flying
        // ~70° from vertical (end-to-end test, 2026-10-21).
        public double StopHeight;
    }

    public struct Body
    {
        public double Mu;        // m³/s²
        public double Radius;    // m
        public Vector3d Omega;   // rad/s, along z (BodyRotation.AngularVelocity)
        // Terrain height above Radius (m) under a position given in the body's orientation at
        // the prediction's UT, as BrakeInput.TerrainHeight. Null = flat.
        public Func<Vector3d, double> TerrainHeight;
    }

    // A burn applied instantly at UT (>= the prediction's UT), before the coast.
    public struct Impulse
    {
        public double UT;
        public Vector3d DeltaV;  // inertial, Zup
    }

    public struct StopPrediction
    {
        public bool Ok;
        public string Reason;          // why not Ok, or the BelowHandOver note; null otherwise
        public double StopUT;
        public Vector3d StopDirNow;    // unit, body-fixed stop point in the orientation at the prediction's UT
        public double StopAltitude;    // m above the terrain at the stop
        public double BrakeDeltaV;     // m/s
        public double BrakeStartUT;
        // Vehicle.StopHeight above SafeAltitude only: braking now (BrakeStartUT = the
        // prediction's start) stops below StopHeight but at or above SafeAltitude. Safe, so Ok;
        // the hand-over to terminal guidance then starts lower. Reason says so.
        public bool BelowHandOver;
        // Ground track where the descent arrives (unit, surface-relative, body-fixed in the
        // orientation at the prediction's UT); zero = not given, DeorbitSearch.GroundTrack then
        // reads it at BrakeStartUT. ReentryPredictor gives it: its arc from the interface to the
        // stop can exceed 90°, where the interface velocity projected at the stop points backwards.
        public Vector3d TrackDirNow;
    }

    // Where a powered descent really stops: coast (after the optional impulse) to the terrain
    // impact, then BrakeSimulator's latest safe start and its simulated burn. Pure maths, frame
    // body-relative inertial Zup (z = rotation axis), tested in EditMode.
    public static class StopPointPredictor
    {
        public const double MinImpactStep = 0.5;      // s
        public const double MaxImpactStep = 60;       // s
        public const double ImpactPrecision = 0.1;    // s
        public const double MaxCoast = 20000;         // s, cap for very long orbits

        /// <summary>
        /// Predicts where the descent stops when braking starts at the latest safe time.
        /// <para>A vessel already at or below the terrain at t0 (after the impulse) gets
        /// impactUT = t0: FindStart then reports TooLate or Impossible, so Ok is false.</para>
        /// <para>A hyperbolic (escape) trajectory is refused, even one that would hit the body.
        /// Callers searching trial Δv should treat that as "no solution".</para>
        /// </summary>
        public static StopPrediction Predict(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Impulse? impulse)
        {
            if (!(vehicle.ThrustKN > 0) || !(vehicle.MassT > 0))
                return Fail("No thrust: activate an engine");

            // State right after the impulse.
            double t0 = ut;
            if (impulse.HasValue)
            {
                Impulse burn = impulse.Value;
                if (burn.UT < ut - 1e-6)
                    return Fail("The burn is in the past");
                if (burn.UT > ut)
                    KeplerPropagator.Propagate(r, v, body.Mu, burn.UT - ut, out r, out v);
                v = v + burn.DeltaV;
                t0 = Math.Max(burn.UT, ut);
                // The brake starts lighter by the propellant the impulse used.
                if (vehicle.Isp > 0)
                    vehicle.MassT *= Math.Exp(-burn.DeltaV.magnitude / (vehicle.Isp * BrakeSimulator.G0));
            }

            double energy = 0.5 * v.sqrMagnitude - body.Mu / r.magnitude;
            if (energy >= 0)
                return Fail("Escape trajectory: no landing on this body");

            double a = -body.Mu / (2 * energy);
            double period = 2 * Math.PI * Math.Sqrt(a * a * a / body.Mu);
            if (!FindImpact(r, v, t0, ut, body, Math.Min(period, MaxCoast), out double impactUT))
                return Fail("The trajectory does not reach the ground");

            Func<Vector3d, double> terrain = body.TerrainHeight;
            double turn = t0 - ut;
            if (terrain != null && turn != 0)
            {
                Func<Vector3d, double> atUT = body.TerrainHeight;
                Vector3d omega = body.Omega;
                // BrakeInput reads terrain in the orientation at its own UT (t0): turn back to ut.
                terrain = p => atUT(SurfaceGeometry.AtTime(p, omega, -turn));
            }

            var input = new BrakeInput
            {
                Position = r,
                Velocity = v,
                UT = t0,
                Mu = body.Mu,
                BodyRadius = body.Radius,
                AngularVelocity = body.Omega,
                ThrustKN = vehicle.ThrustKN,
                MassT = vehicle.MassT,
                Isp = vehicle.Isp,
                ImpactUT = impactUT,
                TerrainHeight = terrain ?? (p => 0),
                StopHeight = vehicle.StopHeight,
            };
            BrakeResult brake = BrakeSimulator.FindStart(input);

            double stopUT = brake.StartUT + brake.BurnDuration;
            Vector3d stopNow = SurfaceGeometry.AtTime(brake.StopPosition, body.Omega, -(stopUT - ut));
            double m = stopNow.magnitude;
            var prediction = new StopPrediction
            {
                Ok = brake.Status == BrakeStatus.Ok,
                StopUT = stopUT,
                StopDirNow = m > 0 ? stopNow / m : stopNow,
                StopAltitude = brake.StopAltitude,
                BrakeDeltaV = brake.DeltaVNeeded,
                BrakeStartUT = brake.StartUT,
            };
            if (brake.Status == BrakeStatus.TooLate && vehicle.StopHeight > BrakeSimulator.SafeAltitude
                && brake.StopAltitude >= BrakeSimulator.SafeAltitude)
            {
                prediction.Ok = true;
                prediction.BelowHandOver = true;
                prediction.Reason = $"Braking now stops below the hand-over altitude ({brake.StopAltitude:F0} m)";
            }
            else if (brake.Status == BrakeStatus.TooLate)
                prediction.Reason = "Too late: braking now stops below the safe altitude";
            else if (brake.Status == BrakeStatus.Impossible)
                prediction.Reason = "Cannot stop before the ground!";
            return prediction;
        }

        static StopPrediction Fail(string reason) => new StopPrediction { Ok = false, Reason = reason };

        // Height above the terrain at time t of the coast that starts at (r0, v0, t0).
        static double Altitude(Vector3d r0, Vector3d v0, double t0, double ut, Body body, double t, out Vector3d r, out Vector3d v)
        {
            r = r0;
            v = v0;
            if (t - t0 > 1e-9)
                KeplerPropagator.Propagate(r0, v0, body.Mu, t - t0, out r, out v);
            double terrain = 0;
            if (body.TerrainHeight != null)
                terrain = body.TerrainHeight(SurfaceGeometry.AtTime(r, body.Omega, -(t - ut)));
            return r.magnitude - body.Radius - terrain;
        }

        // First time the coast reaches the terrain, slightly late (an unsafe upper bound for
        // BrakeSimulator.FindStart). The step never exceeds the time to fall the current height
        // at the current speed, so flat-ish terrain cannot be stepped over.
        static bool FindImpact(Vector3d r0, Vector3d v0, double t0, double ut, Body body, double horizon, out double impactUT)
        {
            impactUT = t0;
            double t = t0;
            double h = Altitude(r0, v0, t0, ut, body, t, out _, out Vector3d v);
            if (h <= 0)
                return true;
            while (t - t0 < horizon)
            {
                double step = Math.Max(MinImpactStep, Math.Min(MaxImpactStep, h / Math.Max(v.magnitude, 1)));
                double next = t + step;
                double hn = Altitude(r0, v0, t0, ut, body, next, out _, out Vector3d vn);
                if (hn <= 0)
                {
                    double lo = t, hi = next;
                    while (hi - lo > ImpactPrecision)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Altitude(r0, v0, t0, ut, body, mid, out _, out _) <= 0)
                            hi = mid;
                        else
                            lo = mid;
                    }
                    impactUT = hi;
                    return true;
                }
                t = next;
                h = hn;
                v = vn;
            }
            return false;
        }
    }
}
