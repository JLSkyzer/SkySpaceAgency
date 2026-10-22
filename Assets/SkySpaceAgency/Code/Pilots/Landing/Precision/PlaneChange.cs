using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Precision
{
    /// <summary>
    /// A plane change toward the landing target. It does not check the vessel's Δv. Consumers
    /// must re-plan the deorbit after the plane-change burn (a finite burn does not end exactly
    /// in the predicted state) with DeorbitSearch.Find starting at EarliestDeorbitUT(BurnUT,
    /// DeltaV, vehicle) or later.
    /// </summary>
    public struct PlaneChangePlan
    {
        public bool Needed;            // the best deorbit from the current plane misses by more than Threshold across the track
        public bool Ok;
        public string Reason;          // why not Ok; null when Ok
        public double BurnUT;          // the earlier node of the two planes at least MinLead ahead; NaN when not needed
        public double BurnStartUT;     // BurnUT − half the burn (BurnTiming), never before ut + MinLead; where TargetOrbitBurn starts
        public TargetOrbit Target;     // the current orbit's shape in the new plane (TargetOrbitBurn's goal)
        public double DeltaV;          // m/s, the instant rotation of the velocity at BurnUT
        public int Iterations;         // plane choices tried, both nodes included
        public int PredictCalls;       // cost of all the deorbit searches, for the log
        public DeorbitPlan Deorbit;    // the predicted deorbit: from the new orbit when Needed, else from the current one
    }

    // Plane change that puts the landing target under the ground track at arrival, on a rotating
    // body. The new plane keeps the orbit's shape and contains, at the vessel's node, the target
    // where it will be when the descent stops there. Pure maths, frame body-relative inertial Zup,
    // tested in EditMode.
    public static class PlaneChange
    {
        public const double Threshold = 1000;           // m of cross-track miss before a plane change is needed
        // s between now and the burn: PrecisionPhases.PlanLead, so that TargetOrbitBurn's longest
        // alignment lead (BurnWarp.MaxLead) fits before the burn starts.
        public const double MinLead = PrecisionPhases.PlanLead;
        public const double ArrivalPrecision = 1;       // s
        public const double CrossPrecision = 100;       // m, residual cross-track miss after the change
        public const int MaxIterations = 10;            // per node
        public const double SettleTime = 60;            // s between the end of the plane change and the deorbit

        /// <summary>
        /// Earliest deorbit after a plane change of deltaV at burnUT: burnUT plus the whole burn at
        /// full thrust with the mass kept constant (pessimistic), plus SettleTime.
        /// </summary>
        public static double EarliestDeorbitUT(double burnUT, double deltaV, Vehicle vehicle)
        {
            double accel = vehicle.MassT > 0 ? vehicle.ThrustKN / vehicle.MassT : 0;  // kN/t = m/s²
            double duration = accel > 0 ? deltaV / accel : 0;
            return burnUT + duration + SettleTime;
        }

        /// <summary>
        /// Needed when DeorbitSearch.Find from (r, v) misses targetDirNow (unit, body orientation
        /// at ut) by more than Threshold across the track. The plan then iterates, for one of the
        /// two nodes: target placed where it is at the estimated arrival (SurfaceGeometry.AtTime),
        /// node 90° from it in the current plane (the smallest inclination change), new plane
        /// through the node and the target, velocity rotated at the node, Find again from there
        /// for the new arrival; it stops when the arrival moves less than ArrivalPrecision and the
        /// cross miss is under CrossPrecision. The stop point's drift out of the plane (braking
        /// against the rotating surface) is fed back by aiming the plane that far from the target.
        /// The node that comes first (at least MinLead ahead) is tried first, the other one only
        /// if it fails. Plan does not check the vessel's Δv; consumers re-plan the deorbit after
        /// the burn with DeorbitSearch.Find from EarliestDeorbitUT or later.
        /// </summary>
        public static PlaneChangePlan Plan(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d targetDirNow) =>
            Plan(r, v, ut, body, vehicle, targetDirNow,
                (rr, vv, uu, imp) => StopPointPredictor.Predict(rr, vv, uu, body, vehicle, imp),
                body.Radius - DeorbitSearch.DescentPeriapsisMargin);

        /// <summary>The same plan with any stop predictor and descent periapsis radius (vehicle: thrust and mass of the plane-change burn).</summary>
        public static PlaneChangePlan Plan(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d targetDirNow,
            StopPredictorFn predict, double descentPeriapsisRadius)
        {
            Vector3d target = targetDirNow / targetDirNow.magnitude;
            DeorbitPlan current = DeorbitSearch.Find(r, v, ut, body, target, ut, predict, descentPeriapsisRadius);
            OrbitElements el = OrbitMath.Elements(r, v, body.Mu);
            Vector3d n = el.AngularMomentum.normalized;
            var plan = new PlaneChangePlan
            {
                BurnUT = double.NaN,
                BurnStartUT = double.NaN,
                Target = new TargetOrbit { Ra = el.ApoapsisRadius, Rp = el.PeriapsisRadius, PlaneNormal = n },
                PredictCalls = current.PredictCalls,
                Deorbit = current,
            };
            if (!current.Ok)
            {
                plan.Reason = current.Reason;
                return plan;
            }
            if (Math.Abs(current.CrossMissM) <= Threshold)
            {
                plan.Ok = true;
                return plan;
            }
            plan.Needed = true;

            // The two nodes for the first aim; the earlier one at least MinLead ahead goes first.
            Vector3d aim0 = SurfaceGeometry.AtTime(target, body.Omega, current.Stop.StopUT - ut);
            Vector3d node0 = Vector3d.Cross(n, aim0);
            if (!(node0.magnitude > 1e-9))
                return Refuse(plan, PoleReason);
            double leadPlus = OrbitMath.TimeToDirection(r, v, body.Mu, node0);
            double leadMinus = OrbitMath.TimeToDirection(r, v, body.Mu, -1.0 * node0);
            bool plusFirst = leadPlus >= MinLead && (leadMinus < MinLead || leadPlus <= leadMinus);
            int[] sides = plusFirst ? new[] { 1, -1 } : new[] { -1, 1 };

            PlaneChangePlan first = Iterate(plan, sides[0], r, v, ut, body, vehicle, target, n, current.Stop.StopUT, predict, descentPeriapsisRadius);
            if (first.Ok)
                return first;
            PlaneChangePlan second = first;
            second.Ok = false;
            second = Iterate(second, sides[1], r, v, ut, body, vehicle, target, n, current.Stop.StopUT, predict, descentPeriapsisRadius);
            if (second.Ok)
                return second;
            // Both failed: report the earlier node's attempt, with the total cost.
            first.PredictCalls = second.PredictCalls;
            first.Iterations = second.Iterations;
            return first;
        }

        const string PoleReason = "The target is at the pole of the orbit: no plane change node";

        // The iteration for one node: side +1 is along n × aim, −1 the opposite one. Iterations
        // and PredictCalls add to those already in plan.
        static PlaneChangePlan Iterate(PlaneChangePlan plan, int side, Vector3d r, Vector3d v, double ut, Body body,
            Vehicle vehicle, Vector3d target, Vector3d n, double arrival, StopPredictorFn predict, double descentPeriapsisRadius)
        {
            Vector3d aimOffset = new Vector3d(0, 0, 0);  // −(the stop's drift out of the plane), unit sphere
            for (int i = 1; i <= MaxIterations; i++)
            {
                plan.Iterations++;
                Vector3d aim = SurfaceGeometry.AtTime(target, body.Omega, arrival - ut) + aimOffset;
                Vector3d node = Vector3d.Cross(n, aim);
                if (!(node.magnitude > 1e-9 * aim.magnitude))
                    return Refuse(plan, PoleReason);
                node = (side / node.magnitude) * node;
                double lead = OrbitMath.TimeToDirection(r, v, body.Mu, node);
                if (lead < MinLead)
                    return Refuse(plan, $"The plane change node is less than {MinLead:F0} s ahead");
                double burnUT = ut + lead;

                KeplerPropagator.Propagate(r, v, body.Mu, lead, out Vector3d rb, out Vector3d vb);
                Vector3d axis = rb.normalized;
                Vector3d nb = Vector3d.Cross(rb, vb).normalized;
                Vector3d n2 = Vector3d.Cross(axis, aim).normalized;
                if (Vector3d.Dot(n2, nb) < 0)
                    n2 = -1.0 * n2;
                double angle = Math.Atan2(Vector3d.Dot(Vector3d.Cross(nb, n2), axis), Vector3d.Dot(nb, n2));
                Vector3d vNew = OrbitMath.Rotate(vb, axis, angle);
                double dv = (vNew - vb).magnitude;

                Vector3d targetAtBurn = SurfaceGeometry.AtTime(target, body.Omega, burnUT - ut);
                DeorbitPlan after = DeorbitSearch.Find(rb, vNew, burnUT, body, targetAtBurn,
                    EarliestDeorbitUT(burnUT, dv, vehicle), predict, descentPeriapsisRadius);
                plan.PredictCalls += after.PredictCalls;
                plan.BurnUT = burnUT;
                plan.BurnStartUT = BurnTiming.CenteredStart(burnUT,
                    BurnTiming.Duration(dv, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp), ut + MinLead);
                plan.DeltaV = dv;
                plan.Target.PlaneNormal = n2;
                plan.Deorbit = after;
                if (!after.Ok)
                    return Refuse(plan, "After the plane change: " + after.Reason);

                double newArrival = after.Stop.StopUT;
                Vector3d stopAtArrival = SurfaceGeometry.AtTime(after.Stop.StopDirNow, body.Omega, newArrival - burnUT);
                // The descent's drift out of its own plane: aim the next plane that far from the target.
                aimOffset = -Vector3d.Dot(stopAtArrival, n2) * n2;
                bool settled = Math.Abs(newArrival - arrival) < ArrivalPrecision;
                arrival = newArrival;
                if (settled && Math.Abs(after.CrossMissM) < CrossPrecision)
                {
                    plan.Ok = true;
                    plan.Reason = null;
                    return plan;
                }
            }
            return Refuse(plan, $"The plane change does not converge on the target ({Math.Abs(plan.Deorbit.CrossMissM):F0} m across the track)");
        }

        static PlaneChangePlan Refuse(PlaneChangePlan plan, string reason)
        {
            plan.Ok = false;
            plan.Reason = reason;
            return plan;
        }
    }
}
