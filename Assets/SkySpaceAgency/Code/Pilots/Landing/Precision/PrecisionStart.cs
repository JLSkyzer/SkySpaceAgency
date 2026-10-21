using System;
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
            if (el.ApoapsisRadius - el.PeriapsisRadius > CircularTolerance)
            {
                double dt = OrbitMath.TimeToTrueAnomaly(input.R, input.V, mu, 0);
                KeplerPropagator.Propagate(input.R, input.V, mu, dt, out Vector3d rp, out Vector3d vp);
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
