using System;
using System.Globalization;
using K2D2.Landing.Braking;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Precision
{
    public struct StartInput
    {
        public Vector3d R, V;          // body-relative inertial Zup, at UT
        public double UT;
        public Body Body;              // terrain in the body's orientation at UT
        public Vehicle Vehicle;        // StopHeight = the hand-over height (start_touchdown_altitude)
        public Vector3d TargetDirNow;  // unit, in the body's orientation at UT
        public double TouchDownSpeed;  // m/s, the player's setting
        public double MaxTiltDeg;      // terminal guidance tilt limit (the player's steering angle)
    }

    public struct StartPlan
    {
        public bool Ok;
        public string Reason;              // why not Ok; null when Ok
        public bool NeedsPreparation;      // not circular within CircularTolerance: circularize at the periapsis first
        public double PreparationUT;       // the periapsis passage the preparation burn is centred on
        public double PreparationDeltaV;   // m/s, 0 when not needed
        public double PlanUT;              // state the plans start from: now, or the periapsis after the preparation
        public Vector3d PlanR, PlanV;
        public PlaneChangePlan PlaneChange;
        public DeorbitPlan Deorbit;        // the precision deorbit (after the plane change when Needed)
        public DeorbitPlan Baseline;       // the landing without target from the same orbit (StopHeight 0)
        public double CorrectionReserve, TerminalReserve, LateralReserve, BaselineTerminalReserve;  // m/s
        public PrecisionBudget.Budget Budget;
        public double TotalDeltaV;         // m/s, everything the landing needs: preparation + plane change + deorbit + brake + reserves
        public int PredictCalls;
        public OrbitClearance Clearance;   // terrain clearance of the circular orbit the approach starts from
    }

    public struct OrbitClearance
    {
        public double RequiredM;        // lowest safe altitude above Radius for the circular orbit
        public double HighestTerrainM;  // highest terrain sampled under its ground track; NaN when not read
        public bool TerrainRead;        // false: a read failed or was not finite, RequiredM is the floor alone
        public int Samples;             // terrain reads made
        public string Note;             // why the terrain was not read; null otherwise
    }

    // The plans a precision landing makes before it flies, from the current orbit: orbit
    // preparation (circularization at the periapsis), plane change, deorbit, the baseline landing
    // without target, the reserves and the Δv budget. Pure maths, frame body-relative inertial
    // Zup, tested in EditMode. The flight re-plans each burn from the orbit it really reaches.
    public static class PrecisionStart
    {
        public const double CircularTolerance = 1500;     // m between apoapsis and periapsis
        public const double MaxPeriapsisAltitude = 100000; // m
        public const string PeriapsisTooHighMessage = "Periapsis above 100 km: lower the orbit first (Orbit tab).";
        public const string NotClosedMessage = "Orbit not closed: capture into orbit first.";
        public const string HitsGroundMessage = "The orbit hits the ground: raise the periapsis first (Orbit tab).";
        // The largest mid-course correction (m/s), also counted in the budget.
        public const double CorrectionReserve = 5;
        // How far (m) terminal guidance must be able to move the hand-over point: the success radius.
        public const double LateralReach = 100;
        // Terrain clearance of the circular orbit the approach starts from: never below
        // MinClearance, nor below the highest terrain under its ground track + TerrainMargin. The
        // track is sampled ClearanceSamplesPerOrbit times per orbit over ClearanceOrbits orbits
        // (the coast before the deorbit lasts up to about two orbits).
        public const double MinClearance = 10000;
        public const double TerrainMargin = 2000;
        public const int ClearanceSamplesPerOrbit = 72;
        public const int ClearanceOrbits = 2;

        public static string TooLowMessage(double peAltM, OrbitClearance c) => c.TerrainRead
            ? string.Format(CultureInfo.InvariantCulture,
                "Orbit too low for a safe precision approach (Pe {0:0.0} km, terrain up to {1:0.0} km): raise it with the Orbit tab.",
                peAltM / 1000, c.HighestTerrainM / 1000)
            : string.Format(CultureInfo.InvariantCulture,
                "Orbit too low for a safe precision approach (Pe {0:0.0} km, terrain not read, {1:0.0} km minimum): raise it with the Orbit tab.",
                peAltM / 1000, c.RequiredM / 1000);

        /// <summary>
        /// Clearance of the circular orbit of the given radius in the plane of normal planeNormal,
        /// the vessel at p0's direction t0 seconds after the body's UT. Each sample point, at time
        /// t, is turned back by the body's rotation (the terrain reads positions in the orientation
        /// at UT), so the samples follow the real ground track. A failed or non-finite read keeps
        /// the MinClearance floor alone, with the reason in Note. Flat (null terrain) reads 0.
        /// </summary>
        public static OrbitClearance Clearance(Vector3d p0, Vector3d planeNormal, double radius, double t0, Body body)
        {
            var c = new OrbitClearance { RequiredM = MinClearance, HighestTerrainM = 0, TerrainRead = true };
            if (body.TerrainHeight == null)
                return c;
            Vector3d u = p0.normalized;
            Vector3d w = Vector3d.Cross(planeNormal.normalized, u).normalized;   // prograde at p0
            double n = Math.Sqrt(body.Mu / (radius * radius * radius));
            int count = ClearanceSamplesPerOrbit * ClearanceOrbits;
            double step = ClearanceOrbits * 2 * Math.PI / n / count;
            double highest = double.NegativeInfinity;
            try
            {
                for (int k = 0; k < count; k++)
                {
                    double t = k * step;
                    double a = n * t;
                    Vector3d p = radius * (Math.Cos(a) * u + Math.Sin(a) * w);
                    double h = body.TerrainHeight(SurfaceGeometry.AtTime(p, body.Omega, -(t0 + t)));
                    c.Samples++;
                    if (double.IsNaN(h) || double.IsInfinity(h))
                        return NotRead(c, $"terrain read {h} at sample {k} of {count}");
                    highest = Math.Max(highest, h);
                }
            }
            catch (Exception e)
            {
                return NotRead(c, $"terrain read failed at sample {c.Samples + 1} of {count} ({e.GetType().Name}: {e.Message})");
            }
            c.HighestTerrainM = highest;
            c.RequiredM = Math.Max(MinClearance, highest + TerrainMargin);
            return c;
        }

        static OrbitClearance NotRead(OrbitClearance c, string note)
        {
            c.TerrainRead = false;
            c.HighestTerrainM = double.NaN;
            c.RequiredM = MinClearance;
            c.Note = note;
            return c;
        }

        public static StartPlan Plan(StartInput input)
        {
            var plan = new StartPlan { PlanUT = input.UT, PlanR = input.R, PlanV = input.V };
            Body body = input.Body;
            double mu = body.Mu;
            OrbitElements el = OrbitMath.Elements(input.R, input.V, mu);
            if (!el.IsClosed || double.IsNaN(el.ApoapsisRadius))
                return Refuse(plan, NotClosedMessage);
            double peAlt = el.PeriapsisRadius - body.Radius;
            if (peAlt > MaxPeriapsisAltitude)
                return Refuse(plan, PeriapsisTooHighMessage);
            if (peAlt < 0)
                return Refuse(plan, HitsGroundMessage);

            Body planBody = body;
            Vector3d target = input.TargetDirNow / input.TargetDirNow.magnitude;
            Vehicle vehicle = input.Vehicle;
            bool prepare = el.ApoapsisRadius - el.PeriapsisRadius > CircularTolerance;
            double dt = 0;
            Vector3d rp = input.R, vp = input.V;
            if (prepare)
            {
                dt = OrbitMath.TimeToTrueAnomaly(input.R, input.V, mu, 0);
                KeplerPropagator.Propagate(input.R, input.V, mu, dt, out rp, out vp);
            }

            // The circular orbit the approach starts from (the periapsis one after a preparation,
            // the current one otherwise) must clear the terrain under its ground track.
            plan.Clearance = Clearance(rp, el.AngularMomentum, el.PeriapsisRadius, dt, body);
            if (peAlt < plan.Clearance.RequiredM)
                return Refuse(plan, TooLowMessage(peAlt, plan.Clearance));

            if (prepare)
            {
                Vector3d vc = vp.normalized * Math.Sqrt(mu / rp.magnitude);
                plan.NeedsPreparation = true;
                plan.PreparationUT = input.UT + dt;
                plan.PreparationDeltaV = (vc - vp).magnitude;
                plan.PlanUT = input.UT + dt;
                plan.PlanR = rp;
                plan.PlanV = vc;
                AtLaterTime(body, target, dt, out planBody, out target);
                vehicle.MassT = AfterBurn(vehicle, plan.PreparationDeltaV);
            }

            PlaneChangePlan pc = PlaneChange.Plan(plan.PlanR, plan.PlanV, plan.PlanUT, planBody, vehicle, target);
            plan.PlaneChange = pc;
            plan.PredictCalls = pc.PredictCalls;
            if (!pc.Ok)
                return Refuse(plan, pc.Needed ? "Plane change: " + pc.Reason : "Deorbit: " + pc.Reason);
            plan.Deorbit = pc.Deorbit;

            // The baseline flies from the same orbit, in the same plane (after the plane change).
            Vehicle noTarget = vehicle;
            noTarget.StopHeight = 0;
            if (pc.Needed)
            {
                double lead = pc.BurnUT - plan.PlanUT;
                KeplerPropagator.Propagate(plan.PlanR, plan.PlanV, mu, lead, out Vector3d rb, out Vector3d vb);
                Vector3d vNew = IntoPlane(rb, vb, pc.Target.PlaneNormal);
                AtLaterTime(planBody, target, lead, out Body burnBody, out _);
                noTarget.MassT = AfterBurn(vehicle, pc.DeltaV);
                plan.Baseline = DeorbitSearch.Baseline(rb, vNew, pc.BurnUT, burnBody, noTarget,
                    PlaneChange.EarliestDeorbitUT(pc.BurnUT, pc.DeltaV, vehicle));
            }
            else
                plan.Baseline = DeorbitSearch.Baseline(plan.PlanR, plan.PlanV, plan.PlanUT, planBody, noTarget, plan.PlanUT);
            plan.PredictCalls += plan.Baseline.PredictCalls;

            double g = mu / (body.Radius * body.Radius);
            double maxAccel = vehicle.ThrustKN / vehicle.MassT;
            double handOver = vehicle.StopHeight > 0 ? vehicle.StopHeight : BrakeSimulator.SafeAltitude;
            plan.CorrectionReserve = CorrectionReserve;
            plan.TerminalReserve = PrecisionBudget.TerminalReserve(handOver, g, input.TouchDownSpeed, maxAccel);
            plan.BaselineTerminalReserve = PrecisionBudget.TerminalReserve(BrakeSimulator.SafeAltitude, g, input.TouchDownSpeed, maxAccel);
            if (double.IsNaN(plan.TerminalReserve))
                return Refuse(plan, "The thrust cannot hold the vessel up on this body.");
            plan.LateralReserve = LateralReserve(LateralReach, g, input.MaxTiltDeg,
                DescentTime(handOver, g, input.TouchDownSpeed, maxAccel));
            if (double.IsNaN(plan.LateralReserve))
                return Refuse(plan, "Max Steering Angle is 0°: the terminal guidance cannot steer toward the target.");

            double planeChange = pc.Needed ? pc.DeltaV : 0;
            plan.Budget = PrecisionBudget.Evaluate(plan.Baseline, plan.Deorbit, plan.CorrectionReserve,
                plan.TerminalReserve + plan.LateralReserve, plan.PreparationDeltaV, planeChange, plan.BaselineTerminalReserve);
            plan.TotalDeltaV = plan.PreparationDeltaV + planeChange + plan.Budget.PrecisionDeltaV;
            plan.Ok = true;
            return plan;
        }

        /// <summary>
        /// Δv (m/s) for terminal guidance to move the vessel distanceM sideways from rest during a
        /// descent of descentTimeS seconds. ZEM/ZEV from rest over T spends 3·d/T and peaks at
        /// 6·d/T²; when the tilt's lateral acceleration near hover, g·tan(tilt), is below that
        /// peak, the move takes 2·sqrt(d/a) at that acceleration (2·sqrt(d·a)) plus the hover
        /// beyond the descent time (g per second). NaN without tilt (no lateral authority).
        /// </summary>
        public static double LateralReserve(double distanceM, double gravity, double maxTiltDeg, double descentTimeS)
        {
            if (!(distanceM > 0))
                return 0;
            double tilt = Math.Min(89, double.IsNaN(maxTiltDeg) ? 0 : maxTiltDeg) * Math.PI / 180;
            double accel = gravity * Math.Tan(tilt);
            if (!(accel > 0))
                return double.NaN;
            double t = Math.Max(descentTimeS, 1e-3);
            if (accel >= 6 * distanceM / (t * t))
                return 3 * distanceM / t;
            double move = 2 * Math.Sqrt(distanceM / accel);
            return 2 * Math.Sqrt(distanceM * accel) + gravity * Math.Max(0, move - t);
        }

        /// <summary>
        /// Duration (s) of the vertical descent PrecisionBudget.TerminalReserve models from rest at
        /// heightM: free fall until the profile speed, then braking at PlanFraction·(maxAccel − g).
        /// NaN when the thrust cannot hold the vessel up.
        /// </summary>
        public static double DescentTime(double heightM, double gravity, double touchDownSpeed, double maxAccel)
        {
            if (!(maxAccel > gravity) || !(gravity > 0))
                return double.NaN;
            double h = Math.Max(0, heightM);
            double vf = FinalDescent.ContactSpeed(touchDownSpeed);
            double d = TerminalGuidance.PlanFraction * (maxAccel - gravity);
            double hb = Math.Min(h, Math.Max(0, (2 * gravity * h - vf * vf) / (2 * (gravity + d))));
            double sb = Math.Sqrt(vf * vf + 2 * d * hb);
            return sb / gravity + Math.Max(0, (sb - vf) / d);
        }

        /// <summary>
        /// The same body and target dt seconds later: the terrain function reads positions given in
        /// the orientation at the later time, and the target is where it will be then.
        /// </summary>
        public static void AtLaterTime(Body body, Vector3d targetDirNow, double dt, out Body later, out Vector3d targetLater)
        {
            later = body;
            targetLater = SurfaceGeometry.AtTime(targetDirNow, body.Omega, dt);
            if (body.TerrainHeight != null && dt != 0)
            {
                Func<Vector3d, double> atNow = body.TerrainHeight;
                Vector3d omega = body.Omega;
                later.TerrainHeight = p => atNow(SurfaceGeometry.AtTime(p, omega, -dt));
            }
        }

        // The velocity at rb turned about rb into the plane of normal n (as PlaneChange does).
        static Vector3d IntoPlane(Vector3d rb, Vector3d vb, Vector3d n)
        {
            Vector3d axis = rb.normalized;
            Vector3d nb = Vector3d.Cross(rb, vb).normalized;
            Vector3d n2 = n.normalized;
            double angle = Math.Atan2(Vector3d.Dot(Vector3d.Cross(nb, n2), axis), Vector3d.Dot(nb, n2));
            return OrbitMath.Rotate(vb, axis, angle);
        }

        static double AfterBurn(Vehicle vehicle, double deltaV) =>
            vehicle.Isp > 0 ? vehicle.MassT * Math.Exp(-Math.Abs(deltaV) / (vehicle.Isp * BrakeSimulator.G0)) : vehicle.MassT;

        static StartPlan Refuse(StartPlan plan, string reason)
        {
            plan.Ok = false;
            plan.Reason = reason;
            return plan;
        }
    }
}
