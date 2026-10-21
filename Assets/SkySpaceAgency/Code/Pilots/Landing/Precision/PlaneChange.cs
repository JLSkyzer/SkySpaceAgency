using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Precision
{
    public struct PlaneChangePlan
    {
        public bool Needed;            // the best deorbit from the current plane misses by more than Threshold across the track
        public bool Ok;
        public string Reason;          // why not Ok; null when Ok
        public double BurnUT;          // at the node of the two planes; NaN when not needed
        public TargetOrbit Target;     // the current orbit's shape in the new plane (TargetOrbitBurn's goal)
        public double DeltaV;          // m/s, the instant rotation of the velocity at BurnUT
        public int Iterations;         // plane choices tried
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
        public const double MinLead = 60;               // s between now and the burn
        public const double ArrivalPrecision = 1;       // s
        public const double CrossPrecision = 100;       // m, residual cross-track miss after the change
        public const int MaxIterations = 10;
        public const double SettleTime = 60;            // s between the end of the plane change and the deorbit

        /// <summary>
        /// Earliest deorbit after a plane change of deltaV starting at burnUT: the burn at full
        /// thrust with the mass kept constant (pessimistic), plus SettleTime.
        /// </summary>
        public static double EarliestDeorbitUT(double burnUT, double deltaV, Vehicle vehicle)
        {
            double accel = vehicle.MassT > 0 ? vehicle.ThrustKN / vehicle.MassT : 0;  // kN/t = m/s²
            double duration = accel > 0 ? deltaV / accel : 0;
            return burnUT + duration + SettleTime;
        }

        /// <summary>
        /// Needed when DeorbitSearch.Find from (r, v) misses targetDirNow (unit, body orientation
        /// at ut) by more than Threshold across the track. The plan then iterates: target placed
        /// where it is at the estimated arrival (SurfaceGeometry.AtTime), node 90° from it in the
        /// current plane (the smallest inclination change), new plane through the node and the
        /// target, velocity rotated at the node, Find again from there for the new arrival; stops
        /// when the arrival moves less than ArrivalPrecision and the cross miss is under
        /// CrossPrecision. The stop point's drift out of the plane (braking against the rotating
        /// surface) is fed back by aiming the plane that far from the target.
        /// </summary>
        public static PlaneChangePlan Plan(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d targetDirNow)
        {
            Vector3d target = targetDirNow / targetDirNow.magnitude;
            DeorbitPlan current = DeorbitSearch.Find(r, v, ut, body, vehicle, target, ut);
            OrbitElements el = OrbitMath.Elements(r, v, body.Mu);
            Vector3d n = el.AngularMomentum.normalized;
            var plan = new PlaneChangePlan
            {
                BurnUT = double.NaN,
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
            double arrival = current.Stop.StopUT;
            Vector3d aimOffset = new Vector3d(0, 0, 0);  // −(the stop's drift out of the plane), unit sphere
            for (int i = 1; i <= MaxIterations; i++)
            {
                plan.Iterations = i;
                Vector3d aim = SurfaceGeometry.AtTime(target, body.Omega, arrival - ut) + aimOffset;
                Vector3d node = Vector3d.Cross(n, aim);
                if (!(node.magnitude > 1e-9 * aim.magnitude))
                    return Refuse(plan, "The target is at the pole of the orbit: no plane change node");
                node = node.normalized;
                double lead = OrbitMath.TimeToDirection(r, v, body.Mu, node);
                if (lead < MinLead)
                    lead = OrbitMath.TimeToDirection(r, v, body.Mu, -1.0 * node);  // the other node, same plane
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
                DeorbitPlan after = DeorbitSearch.Find(rb, vNew, burnUT, body, vehicle, targetAtBurn,
                    EarliestDeorbitUT(burnUT, dv, vehicle));
                plan.PredictCalls += after.PredictCalls;
                plan.BurnUT = burnUT;
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
