using System;
using System.Collections.Generic;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Braking
{
    public enum BrakeStatus
    {
        Ok,         // StartUT is the latest start that stops StopHeight (default SafeAltitude) above the terrain
        TooLate,    // braking now stops, but below StopHeight: brake now
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
        // Height above the terrain (m) the burn must stop at or above; 0 = SafeAltitude. The
        // precision landing stops at start_touchdown_altitude, where terminal guidance takes over.
        public double StopHeight;
        // Landing with a chosen stage: the stages from the active one to the landing one, in
        // firing order. Null or empty = one stage from ThrustKN, MassT and Isp, as before. With
        // stages, ThrustKN, MassT and Isp are not read.
        public BrakeStage[] Stages;
        // Height above the terrain (m) at or below which a lower stage is dropped even if it still
        // has fuel (StagedBrakePlan). 0 = only when dry.
        public double DropHeight;
    }

    // One stage of a staged braking. The stages before the last are dropped when dry or at
    // BrakeInput.DropHeight; the last one, the landing stage, burns without a fuel limit (its Δv
    // is checked apart, as the one-stage simulation never limited it).
    public struct BrakeStage
    {
        public int Stage;       // the game's stage number, for the logs
        public double ThrustKN; // full throttle
        public double MassT;    // vessel mass when this stage starts (lower stages already dropped)
        public double Isp;      // s; 0 = unknown (constant mass)
        public double DeltaV;   // m/s this stage can give (vacuum)
    }

    // A lower stage dropped during a simulated braking.
    public struct BrakeDrop
    {
        public int Index;           // in the simulated stage list
        public int Stage;           // the game's stage number
        public bool Dry;            // out of fuel; false = forced at BrakeInput.DropHeight
        public double UT;
        public double Altitude;     // m above the terrain
        public double SurfaceSpeed; // m/s
        public Vector3d Position, Velocity;
    }

    // The state before each step of a simulated braking (BrakeSimulator.Simulate's onStep).
    public struct BrakeSample
    {
        public double UT;
        public Vector3d Position, Velocity;
        public double Altitude;     // m above the terrain
        public int StageIndex;      // in the simulated stage list
        public bool Thrusting;      // false during the gap after a drop
    }

    // One simulated braking burn.
    public struct BrakeRun
    {
        public bool Stopped;
        public double StopAltitude;   // m above the terrain where the run ended (<= 0: crash)
        public double BurnDuration;   // s
        public double DeltaV;         // m/s spent, every stage
        public Vector3d StopPosition; // inertial, Zup
        // The lower stages dropped (empty with one stage), the stage index the run ended on, and
        // the Δv that stage spent.
        public List<BrakeDrop> Drops;
        public int FinalStageIndex;
        public double FinalStageDeltaV;
    }

    public struct BrakeResult
    {
        public BrakeStatus Status;
        public double StartUT;
        public double BurnDuration;
        public double DeltaVNeeded;
        public double StopAltitude;
        public Vector3d StopPosition;
        public List<BrakeDrop> Drops;       // see BrakeRun
        public int FinalStageIndex;
        public double FinalStageDeltaV;
    }

    // Forward simulation of the braking burn, like MechJeb's landing prediction: coast on the
    // Kepler orbit until the start time, then burn ThrustFraction of the active thrust against the
    // surface velocity (RK4), with varying gravity, mass loss, body rotation and the terrain
    // under the path. FindStart bisects the latest start that still stops StopHeight up
    // (BrakeInput.StopHeight, SafeAltitude by default). With BrakeInput.Stages, the lower stages
    // burn first and leave when dry or at BrakeInput.DropHeight, StageGap seconds without thrust
    // each; the landing stage finishes the braking.
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
        // s without thrust after a drop: the dry wait, the activation and the engines' ignition.
        public const double StageGap = 1.0;
        // Numeric floor only (real dry mass is unknown): keeps a very long burn from dividing by ~0.
        const double MinMassFraction = 0.05;

        public static BrakeResult FindStart(BrakeInput input)
        {
            if (!CanThrust(input))
                return new BrakeResult { Status = BrakeStatus.Impossible, StartUT = input.UT, Drops = new List<BrakeDrop>() };

            double stopHeight = input.StopHeight > 0 ? input.StopHeight : SafeAltitude;
            BrakeRun now = Simulate(input, input.UT);
            if (!now.Stopped)
                return ToResult(BrakeStatus.Impossible, input.UT, now);
            if (now.StopAltitude < stopHeight)
                return ToResult(BrakeStatus.TooLate, input.UT, now);

            // lo is always a verified safe start, hi an unsafe one (the impact itself).
            double lo = input.UT, hi = Math.Max(input.ImpactUT, input.UT);
            BrakeRun best = now;
            for (int i = 0; i < MaxIterations && hi - lo > StartPrecision; i++)
            {
                double mid = 0.5 * (lo + hi);
                BrakeRun run = Simulate(input, mid);
                if (run.Stopped && run.StopAltitude >= stopHeight)
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

        // The vessel can brake at all: the (landing) stage has thrust and every mass is known.
        static bool CanThrust(BrakeInput input)
        {
            if (input.Stages == null || input.Stages.Length == 0)
                return input.ThrustKN > 0 && input.MassT > 0;
            foreach (BrakeStage st in input.Stages)
                if (!(st.MassT > 0))
                    return false;
            return input.Stages[input.Stages.Length - 1].ThrustKN > 0;
        }

        // The stages simulated: input.Stages, or one stage from ThrustKN, MassT and Isp.
        public static BrakeStage[] StagesOf(BrakeInput input) =>
            input.Stages != null && input.Stages.Length > 0
                ? input.Stages
                : new[] { new BrakeStage { Stage = -1, ThrustKN = input.ThrustKN, MassT = input.MassT, Isp = input.Isp, DeltaV = double.PositiveInfinity } };

        // Δv a stage has given since it started at m0 (constant mass without Isp).
        static double StageDeltaV(BrakeStage stage, double thrust, double m0, double m, double burn) =>
            stage.Isp > 0 ? stage.Isp * G0 * Math.Log(m0 / m) : thrust / m0 * burn;

        public static BrakeRun Simulate(BrakeInput input, double startUT) => Simulate(input, startUT, null);

        // onStep, when given, sees the state before every step and at the end.
        public static BrakeRun Simulate(BrakeInput input, double startUT, Action<BrakeSample> onStep)
        {
            Vector3d r = input.Position, v = input.Velocity;
            double coast = startUT - input.UT;
            if (coast > 1e-6)
                KeplerPropagator.Propagate(input.Position, input.Velocity, input.Mu, coast, out r, out v);

            BrakeStage[] stages = StagesOf(input);
            int last = stages.Length - 1;
            int k = 0;
            var drops = new List<BrakeDrop>();
            double spentBefore = 0;                                                 // Δv of the dropped stages

            double thrust = DescentEnvelope.ThrustFraction * stages[0].ThrustKN;   // kN
            double flow = stages[0].Isp > 0 ? thrust / (stages[0].Isp * G0) : 0;  // t/s
            double m0 = stages[0].MassT;
            double minMass = m0 * MinMassFraction;
            double m = m0;
            double t = startUT, burn = 0, stageBurn = 0, gap = 0;
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
                    onStep?.Invoke(new BrakeSample { UT = t, Position = r, Velocity = v, Altitude = altitude, StageIndex = k, Thrusting = false });
                    double finalDv = StageDeltaV(stages[k], thrust, m0, m, stageBurn);
                    return new BrakeRun
                    {
                        Stopped = stopped && altitude > 0,
                        StopAltitude = altitude,
                        BurnDuration = burn,
                        DeltaV = spentBefore + finalDv,
                        StopPosition = r,
                        Drops = drops,
                        FinalStageIndex = k,
                        FinalStageDeltaV = finalDv,
                    };
                }

                // A lower stage leaves when dry or at the drop height; the next pushes after StageGap.
                if (k < last && gap <= 0)
                {
                    double stageDv = StageDeltaV(stages[k], thrust, m0, m, stageBurn);
                    bool dry = !(thrust > 0) || !(stageDv < stages[k].DeltaV);
                    bool forced = input.DropHeight > 0 && altitude <= input.DropHeight;
                    if (dry || forced)
                    {
                        drops.Add(new BrakeDrop
                        {
                            Index = k, Stage = stages[k].Stage, Dry = dry, UT = t, Altitude = altitude,
                            SurfaceSpeed = speed, Position = r, Velocity = v,
                        });
                        spentBefore += stageDv;
                        k++;
                        thrust = DescentEnvelope.ThrustFraction * stages[k].ThrustKN;
                        flow = stages[k].Isp > 0 ? thrust / (stages[k].Isp * G0) : 0;
                        m0 = stages[k].MassT;
                        minMass = m0 * MinMassFraction;
                        m = m0;
                        stageBurn = 0;
                        gap = StageGap;
                    }
                }

                onStep?.Invoke(new BrakeSample { UT = t, Position = r, Velocity = v, Altitude = altitude, StageIndex = k, Thrusting = gap <= 0 });
                if (gap > 0)
                {
                    RK4Step(input, 0, 0, ref r, ref v, ref m);
                    gap -= Step;
                }
                else
                {
                    RK4Step(input, thrust, flow, ref r, ref v, ref m);
                    if (m < minMass)
                        m = minMass;
                    stageBurn += Step;
                }
                t += Step;
                burn += Step;
            }
        }

        /// <summary>
        /// The same input from another state (r, v at ut) of the same body: the terrain function
        /// is turned so that it still reads the body's orientation at the new UT.
        /// </summary>
        public static BrakeInput Rebase(BrakeInput input, Vector3d r, Vector3d v, double ut)
        {
            BrakeInput moved = input;
            moved.Position = r;
            moved.Velocity = v;
            moved.UT = ut;
            if (input.TerrainHeight != null && ut != input.UT)
            {
                Func<Vector3d, double> atOld = input.TerrainHeight;
                double angle = -input.AngularVelocity.z * (ut - input.UT);
                double c = Math.Cos(angle), s = Math.Sin(angle);
                moved.TerrainHeight = p => atOld(new Vector3d(p.x * c - p.y * s, p.x * s + p.y * c, p.z));
            }
            return moved;
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
                Drops = run.Drops,
                FinalStageIndex = run.FinalStageIndex,
                FinalStageDeltaV = run.FinalStageDeltaV,
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
