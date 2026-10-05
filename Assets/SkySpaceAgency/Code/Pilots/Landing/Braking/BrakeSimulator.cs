using System;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Braking
{
    public enum BrakeStatus
    {
        Ok,         // StartUT is the latest start that stops SafeAltitude above the terrain
        TooLate,    // braking now stops, but below SafeAltitude: brake now
        Impossible, // braking now still hits the ground (or there is no thrust)
    }

    // Everything the braking simulation needs. Vectors are in the game's Zup frame relative to
    // the body (z = rotation axis), inertial. Plain data, so tests can build it.
    public struct BrakeInput
    {
        public Vector3d Position;          // m, at UT
        public Vector3d Velocity;          // m/s, at UT
        public double UT;
        public double Mu;                  // m³/s²
        public double BodyRadius;          // m
        public Vector3d AngularVelocity;   // rad/s, along z (BodyRotation.AngularVelocity)
        public double ThrustKN;            // active engines at full throttle
        public double MassT;
        public double Isp;                 // s; 0 = unknown, mass kept constant (pessimistic)
        public double ImpactUT;            // unpowered impact: upper bound of the search
        // Terrain height above BodyRadius (m) under a position given in the body's orientation at UT.
        public Func<Vector3d, double> TerrainHeight;
    }

    // One simulated braking burn.
    public struct BrakeRun
    {
        public bool Stopped;
        public double StopAltitude;   // m above the terrain where the run ended (<= 0: crash)
        public double BurnDuration;   // s
        public double DeltaV;         // m/s spent
        public Vector3d StopPosition; // inertial, Zup
    }

    public struct BrakeResult
    {
        public BrakeStatus Status;
        public double StartUT;
        public double BurnDuration;
        public double DeltaVNeeded;
        public double StopAltitude;
        public Vector3d StopPosition;
    }

    // Forward simulation of the braking burn, like MechJeb's landing prediction: coast on the
    // Kepler orbit until the start time, then burn ThrustFraction of the active thrust against the
    // surface velocity (RK4), with varying gravity, mass loss, body rotation and the terrain
    // under the path. FindStart bisects the latest start that still stops SafeAltitude up.
    public static class BrakeSimulator
    {
        public const double SafeAltitude = 50;     // m
        public const double StopSpeed = 1;         // m/s
        public const double Step = 0.5;            // s
        public const double TerrainInterval = 2;   // s between terrain readings
        public const double CloseToTerrain = 5000; // m: closer than this, read the terrain every step
        public const double MaxBurnTime = 3600;    // s
        public const double StartPrecision = 0.01; // s
        public const int MaxIterations = 40;
        public const double G0 = 9.80665;
        // Numeric floor only (real dry mass is unknown): keeps a very long burn from dividing by ~0.
        const double MinMassFraction = 0.05;

        public static BrakeResult FindStart(BrakeInput input)
        {
            if (!(input.ThrustKN > 0) || !(input.MassT > 0))
                return new BrakeResult { Status = BrakeStatus.Impossible, StartUT = input.UT };

            BrakeRun now = Simulate(input, input.UT);
            if (!now.Stopped)
                return ToResult(BrakeStatus.Impossible, input.UT, now);
            if (now.StopAltitude < SafeAltitude)
                return ToResult(BrakeStatus.TooLate, input.UT, now);

            // lo is always a verified safe start, hi an unsafe one (the impact itself).
            double lo = input.UT, hi = Math.Max(input.ImpactUT, input.UT);
            BrakeRun best = now;
            for (int i = 0; i < MaxIterations && hi - lo > StartPrecision; i++)
            {
                double mid = 0.5 * (lo + hi);
                BrakeRun run = Simulate(input, mid);
                if (run.Stopped && run.StopAltitude >= SafeAltitude)
                {
                    lo = mid;
                    best = run;
                }
                else
                {
                    hi = mid;
                }
            }
            return ToResult(BrakeStatus.Ok, lo, best);
        }

        public static BrakeRun Simulate(BrakeInput input, double startUT)
        {
            Vector3d r = input.Position, v = input.Velocity;
            double coast = startUT - input.UT;
            if (coast > 1e-6)
                KeplerPropagator.Propagate(input.Position, input.Velocity, input.Mu, coast, out r, out v);

            double thrust = DescentEnvelope.ThrustFraction * input.ThrustKN;   // kN
            double flow = input.Isp > 0 ? thrust / (input.Isp * G0) : 0;      // t/s
            double minMass = input.MassT * MinMassFraction;
            double m = input.MassT;
            double t = startUT, burn = 0;
            double terrain = 0, nextTerrainUT = double.NegativeInfinity;

            while (true)
            {
                double aboveRadius = r.magnitude - input.BodyRadius;
                if (t >= nextTerrainUT || aboveRadius - terrain < CloseToTerrain)
                {
                    terrain = TerrainUnder(input, r, t);
                    nextTerrainUT = t + TerrainInterval;
                }
                double altitude = aboveRadius - terrain;

                double speed = BodyRotation.SurfaceVelocity(r, v, input.AngularVelocity).magnitude;
                // Within one step of zero speed: going on would only let RK4 chatter around zero.
                bool stopped = speed <= Math.Max(StopSpeed, thrust / m * Step);

                if (altitude <= 0 || stopped || burn >= MaxBurnTime)
                {
                    return new BrakeRun
                    {
                        Stopped = stopped && altitude > 0,
                        StopAltitude = altitude,
                        BurnDuration = burn,
                        DeltaV = input.Isp > 0 ? input.Isp * G0 * Math.Log(input.MassT / m) : thrust / input.MassT * burn,
                        StopPosition = r,
                    };
                }

                RK4Step(input, thrust, flow, ref r, ref v, ref m);
                if (m < minMass)
                    m = minMass;
                t += Step;
                burn += Step;
            }
        }

        static BrakeResult ToResult(BrakeStatus status, double startUT, BrakeRun run)
        {
            return new BrakeResult
            {
                Status = status,
                StartUT = startUT,
                BurnDuration = run.BurnDuration,
                DeltaVNeeded = run.DeltaV,
                StopAltitude = run.StopAltitude,
                StopPosition = run.StopPosition,
            };
        }

        // Terrain under r at time t. A point fixed to a body turning at +omega_z around z is, at
        // t, rotated by +omega_z * (t - UT) from where it was at UT: undo that turn to read the
        // terrain in the body's orientation at UT (the only one TerrainHeight knows).
        static double TerrainUnder(BrakeInput input, Vector3d r, double t)
        {
            if (input.TerrainHeight == null)
                return 0;
            double angle = -input.AngularVelocity.z * (t - input.UT);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            var bodyFixed = new Vector3d(r.x * c - r.y * s, r.x * s + r.y * c, r.z);
            return input.TerrainHeight(bodyFixed);
        }

        static void RK4Step(BrakeInput input, double thrust, double flow, ref Vector3d r, ref Vector3d v, ref double m)
        {
            double h = Step;
            Vector3d a1 = Acceleration(input, thrust, r, v, m);
            Vector3d v1 = v;
            Vector3d v2 = v + a1 * (h / 2);
            Vector3d a2 = Acceleration(input, thrust, r + v1 * (h / 2), v2, m - flow * h / 2);
            Vector3d v3 = v + a2 * (h / 2);
            Vector3d a3 = Acceleration(input, thrust, r + v2 * (h / 2), v3, m - flow * h / 2);
            Vector3d v4 = v + a3 * h;
            Vector3d a4 = Acceleration(input, thrust, r + v3 * h, v4, m - flow * h);

            r = r + (v1 + v2 * 2 + v3 * 2 + v4) * (h / 6);
            v = v + (a1 + a2 * 2 + a3 * 2 + a4) * (h / 6);
            m -= flow * h; // linear in time: exact
        }

        // Gravity, plus thrust against the surface velocity.
        static Vector3d Acceleration(BrakeInput input, double thrust, Vector3d r, Vector3d v, double m)
        {
            double rm = r.magnitude;
            Vector3d accel = r * (-input.Mu / (rm * rm * rm));
            Vector3d vs = BodyRotation.SurfaceVelocity(r, v, input.AngularVelocity);
            double speed = vs.magnitude;
            if (speed > 1e-9)
                accel = accel + vs * (-thrust / (m * speed));
            return accel;
        }
    }
}
