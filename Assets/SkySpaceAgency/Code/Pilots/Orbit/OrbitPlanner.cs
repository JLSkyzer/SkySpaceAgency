using System;
using System.Collections.Generic;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.OrbitPlanning
{
    public enum OrbitPlanError
    {
        None,
        EscapeTrajectory,
        UnstableCurrentOrbit,
        PeBelowSurface,
        PeInAtmosphere,
        PeAboveAp,
        InclinationOutOfRange,
        // Open trajectory with its periapsis below the surface (or in the atmosphere), and the
        // earliest capture point is too low as well, or comes after that periapsis.
        CaptureBelowSurface,
        CaptureInAtmosphere,
        // Open trajectory that leaves the SOI before the earliest capture point, or a target Ap
        // beyond the SOI.
        CaptureLeavesSoi,
        // The capture burn needs more Δv than the vessel has (see OrbitPlanner.CheckCaptureBudget).
        CaptureBeyondDeltaV
    }

    public struct BodyInfo
    {
        public double Mu;
        public double Radius;
        public double AtmosphereDepth;
        // Radius of the body's sphere of influence (m), +∞ for the star. 0 (unknown) keeps the
        // behaviour from before the capture: an open trajectory is refused (EscapeTrajectory).
        public double SoiRadius;
    }

    // The capture step that starts a plan made from an open trajectory.
    public sealed class CaptureInfo
    {
        // True at the trajectory's periapsis; false at the earliest point (MinLeadSeconds from now).
        public bool AtPeriapsis;
        public double Radius;
        public double DeltaV;
    }

    // Altitudes in meters above the surface, inclination in degrees. null keeps the current value.
    public struct OrbitTargets
    {
        public double? ApAltitude;
        public double? PeAltitude;
        public double? InclinationDeg;
    }

    public sealed class PlannedBurn
    {
        public double UT;
        public Vector3d DeltaV;   // body-relative Zup frame
        public double Radial;     // DeltaV along radial out (v̂ × ĥ)
        public double Normal;     // DeltaV along the orbit normal ĥ = (r × v)/|r × v|
        public double Prograde;   // DeltaV along v̂
        public string Label;
        // The orbit right after this burn (apsis radii and plane), for the closed-loop executor.
        public TargetOrbit Target;
    }

    public sealed class OrbitPlan
    {
        public OrbitPlanError Error;
        public List<PlannedBurn> Burns = new List<PlannedBurn>();
        public OrbitElements Final;
        // Not null when the plan starts with a capture (open trajectory inside the SOI).
        public CaptureInfo Capture;
        public bool Ok => Error == OrbitPlanError.None;

        public double TotalDeltaV
        {
            get
            {
                double sum = 0;
                foreach (var burn in Burns)
                    sum += burn.DeltaV.magnitude;
                return sum;
            }
        }
    }

    // Turns the current state vector and the targets into impulsive burns: orbit shape first
    // (at the apsides), then the plane change at the farther orbital node. Each burn is computed
    // on the state propagated from the previous one. See the Orbit tab spec.
    public static class OrbitPlanner
    {
        public const double MinLeadSeconds = 60;
        public const double CircularBurnDelaySeconds = 180;
        public const double CircularThresholdMeters = 2000;
        public const double InclinationToleranceDeg = 0.05;
        const double MinSurfaceClearance = 1000;
        const double Deg = Math.PI / 180;

        struct State
        {
            public Vector3d R;
            public Vector3d V;
            public double UT;
        }

        public static bool SameRadius(double a, double b) =>
            Math.Abs(a - b) < Math.Max(100, 0.001 * Math.Max(a, b));

        /// <summary>
        /// True when the state is an open trajectory that the planner captures first: hyperbolic
        /// (or parabolic), or elliptic with its apoapsis beyond the SOI, around a body whose SOI is
        /// known (BodyInfo.SoiRadius > 0).
        /// </summary>
        public static bool NeedsCapture(Vector3d r, Vector3d v, BodyInfo body)
        {
            if (!(body.SoiRadius > 0))
                return false;
            var el = OrbitMath.Elements(r, v, body.Mu);
            return !el.IsClosed || el.ApoapsisRadius > body.SoiRadius;
        }

        /// <summary>
        /// CaptureBeyondDeltaV when the plan's capture burn needs more than the vessel's Δv (m/s).
        /// The plan's own error otherwise, also when there is no capture or the vessel's Δv is
        /// unknown (NaN).
        /// </summary>
        public static OrbitPlanError CheckCaptureBudget(OrbitPlan plan, double vesselDeltaV)
        {
            if (plan == null)
                return OrbitPlanError.None;
            if (!plan.Ok || plan.Capture == null || double.IsNaN(vesselDeltaV))
                return plan.Error;
            return plan.Capture.DeltaV > vesselDeltaV ? OrbitPlanError.CaptureBeyondDeltaV : OrbitPlanError.None;
        }

        public static OrbitPlan Plan(Vector3d r0, Vector3d v0, double ut0, BodyInfo body, OrbitTargets targets)
        {
            if (NeedsCapture(r0, v0, body))
                return PlanCapture(r0, v0, ut0, body, targets);
            return PlanClosed(new OrbitPlan(), r0, v0, ut0, body, targets);
        }

        // An open trajectory: a capture burn into an orbit of the current plane through the
        // capture point, then the closed-orbit plan from that orbit. The capture is at the
        // periapsis when it is ahead, above the surface and atmosphere and at least MinLeadSeconds
        // away; otherwise at the earliest point, MinLeadSeconds from now. The capture orbit has the
        // ticked apsides, the capture radius standing for an unticked one (so both unticked give a
        // circular orbit there). When the capture radius lies outside them it becomes an apsis of
        // the capture orbit, and the closed plan that follows moves it to its target.
        static OrbitPlan PlanCapture(Vector3d r0, Vector3d v0, double ut0, BodyInfo body, OrbitTargets targets)
        {
            var plan = new OrbitPlan();
            double mu = body.Mu;
            double surface = body.Radius + MinSurfaceClearance;
            double minPe = body.Radius + Math.Max(body.AtmosphereDepth, MinSurfaceClearance);
            var current = OrbitMath.Elements(r0, v0, mu);
            bool inbound = Vector3d.Dot(r0, v0) < 0;
            double tPe = inbound ? TimeToPeriapsis(r0, v0, mu) : 0;

            double dt = MinLeadSeconds;
            bool atPeriapsis = inbound && current.PeriapsisRadius >= minPe && tPe >= MinLeadSeconds;
            if (atPeriapsis)
                dt = tPe;

            KeplerPropagator.Propagate(r0, v0, mu, dt, out var rb, out var vb);
            double rc = rb.magnitude;
            if (!atPeriapsis)
            {
                // Inbound, the radius only falls until the periapsis: a capture point above the
                // limits and before a low periapsis means a path above them all the way there.
                bool passesLowPe = inbound && current.PeriapsisRadius < minPe && tPe <= dt;
                if (rc < minPe || passesLowPe)
                {
                    double lowest = inbound ? Math.Min(rc, current.PeriapsisRadius) : rc;
                    return Fail(plan, lowest < surface ? OrbitPlanError.CaptureBelowSurface : OrbitPlanError.CaptureInAtmosphere);
                }
            }
            if (rc >= body.SoiRadius)
                return Fail(plan, OrbitPlanError.CaptureLeavesSoi);

            double? raTicked = targets.ApAltitude.HasValue ? body.Radius + targets.ApAltitude.Value : (double?)null;
            double? rpTicked = targets.PeAltitude.HasValue ? body.Radius + targets.PeAltitude.Value : (double?)null;
            if (rpTicked.HasValue && rpTicked.Value < surface)
                return Fail(plan, OrbitPlanError.PeBelowSurface);
            if (rpTicked.HasValue && rpTicked.Value < minPe)
                return Fail(plan, OrbitPlanError.PeInAtmosphere);
            if (raTicked.HasValue && rpTicked.HasValue && rpTicked.Value > raTicked.Value && !SameRadius(rpTicked.Value, raTicked.Value))
                return Fail(plan, OrbitPlanError.PeAboveAp);
            if (targets.InclinationDeg.HasValue && (targets.InclinationDeg.Value < 0 || targets.InclinationDeg.Value > 180))
                return Fail(plan, OrbitPlanError.InclinationOutOfRange);
            if (raTicked.HasValue && raTicked.Value >= body.SoiRadius)
                return Fail(plan, OrbitPlanError.CaptureLeavesSoi);

            // An unticked apsis stands at the capture radius, kept on its side of the ticked one.
            double raTarget = raTicked ?? Math.Max(rc, rpTicked ?? rc);
            double rpTarget = rpTicked ?? Math.Min(rc, raTicked ?? rc);
            double ra = Math.Max(raTarget, rc);
            double rp = Math.Min(rpTarget, rc);
            // At the periapsis the burn stays tangential: a capture radius between the targets
            // becomes the Pe of the capture orbit (the cheaper capture), set to its target next.
            if (atPeriapsis && rp < rc && rc < ra)
                rp = rc;

            Vector3d dv = OrbitGuidance.DeltaVToOrbit(rb, vb, mu, ra, rp, current.AngularMomentum);
            string where = atPeriapsis ? "at periapsis" : $"in {Math.Max(1, Math.Round(dt / 60)):n0} min";
            AddBurn(plan, mu, ut0 + dt, rb, vb, dv, $"Capture {where} ({(rc - body.Radius) / 1000:n1} km)");
            plan.Capture = new CaptureInfo { AtPeriapsis = atPeriapsis, Radius = rc, DeltaV = dv.magnitude };

            return PlanClosed(plan, rb, vb + dv, ut0 + dt, body, targets);
        }

        // Seconds until the periapsis from an inbound state (radial speed < 0), elliptic or
        // hyperbolic. Hyperbolic: e·cosh F = 1 + r/|a|, M = e·sinh F − F, n = sqrt(mu/|a|³).
        public static double TimeToPeriapsis(Vector3d r, Vector3d v, double mu)
        {
            var el = OrbitMath.Elements(r, v, mu);
            if (el.IsClosed)
                return OrbitMath.TimeToTrueAnomaly(r, v, mu, 0);
            double a = -el.SemiMajorAxis;
            double e = el.Eccentricity;
            double coshF = (1 + r.magnitude / a) / e;
            double F = Math.Log(coshF + Math.Sqrt(Math.Max(0, coshF * coshF - 1)));
            if (Vector3d.Dot(r, v) < 0)
                F = -F;
            double M = e * Math.Sinh(F) - F;
            return -M / Math.Sqrt(mu / (a * a * a));
        }

        // The closed-orbit plan from (r0, v0), added to 'plan' (empty, or holding the capture).
        static OrbitPlan PlanClosed(OrbitPlan plan, Vector3d r0, Vector3d v0, double ut0, BodyInfo body, OrbitTargets targets)
        {
            double mu = body.Mu;
            var current = OrbitMath.Elements(r0, v0, mu);
            double minPe = body.Radius + Math.Max(body.AtmosphereDepth, MinSurfaceClearance);

            if (!current.IsClosed)
                return Fail(plan, OrbitPlanError.EscapeTrajectory);
            if (current.PeriapsisRadius < minPe)
                return Fail(plan, OrbitPlanError.UnstableCurrentOrbit);

            double raTarget = targets.ApAltitude.HasValue ? body.Radius + targets.ApAltitude.Value : current.ApoapsisRadius;
            double rpTarget = targets.PeAltitude.HasValue ? body.Radius + targets.PeAltitude.Value : current.PeriapsisRadius;
            if (rpTarget < body.Radius + MinSurfaceClearance)
                return Fail(plan, OrbitPlanError.PeBelowSurface);
            if (rpTarget < minPe)
                return Fail(plan, OrbitPlanError.PeInAtmosphere);
            if (rpTarget > raTarget && !SameRadius(rpTarget, raTarget))
                return Fail(plan, OrbitPlanError.PeAboveAp);
            if (targets.InclinationDeg.HasValue && (targets.InclinationDeg.Value < 0 || targets.InclinationDeg.Value > 180))
                return Fail(plan, OrbitPlanError.InclinationOutOfRange);

            var state = new State { R = r0, V = v0, UT = ut0 };
            bool apChange = !SameRadius(raTarget, current.ApoapsisRadius);
            bool peChange = !SameRadius(rpTarget, current.PeriapsisRadius);

            if (apChange && peChange)
            {
                ShapeBurn(plan, ref state, mu, 0, raTarget, $"Set Ap {(raTarget - body.Radius) / 1000:n1} km");
                // Second burn at the apsis that now sits at the target Ap radius.
                var mid = OrbitMath.Elements(state.R, state.V, mu);
                double nu = Math.Abs(mid.PeriapsisRadius - raTarget) < Math.Abs(mid.ApoapsisRadius - raTarget) ? 0 : Math.PI;
                ShapeBurn(plan, ref state, mu, nu, rpTarget, $"Set Pe {(rpTarget - body.Radius) / 1000:n1} km");
            }
            else if (apChange)
            {
                ShapeBurn(plan, ref state, mu, 0, raTarget, $"Set Ap {(raTarget - body.Radius) / 1000:n1} km");
            }
            else if (peChange)
            {
                ShapeBurn(plan, ref state, mu, Math.PI, rpTarget, $"Set Pe {(rpTarget - body.Radius) / 1000:n1} km");
            }

            if (targets.InclinationDeg.HasValue)
            {
                double iTarget = targets.InclinationDeg.Value * Deg;
                var shaped = OrbitMath.Elements(state.R, state.V, mu);
                if (Math.Abs(iTarget - shaped.InclinationRad) > InclinationToleranceDeg * Deg)
                    PlaneBurn(plan, ref state, mu, iTarget);
            }

            plan.Final = OrbitMath.Elements(state.R, state.V, mu);
            return plan;
        }

        // Rotates the velocity about the radius vector at the orbital node farther from the body
        // (slower there, so cheaper). An equatorial orbit has no node line: the burn point becomes
        // the node (apoapsis, or in 3 minutes on a near-circular orbit).
        static void PlaneBurn(OrbitPlan plan, ref State state, double mu, double iTarget)
        {
            var el = OrbitMath.Elements(state.R, state.V, mu);
            bool circular = el.ApoapsisRadius - el.PeriapsisRadius < CircularThresholdMeters;
            Vector3d h = el.AngularMomentum;
            Vector3d nodeLine = Vector3d.Cross(OrbitMath.North, h);

            double dt;
            string where;
            if (nodeLine.magnitude / h.magnitude < 1e-4)
            {
                dt = circular ? CircularBurnDelaySeconds : Lead(OrbitMath.TimeToTrueAnomaly(state.R, state.V, mu, Math.PI), el.Period);
                where = circular ? "in 3 min" : "at apoapsis";
            }
            else
            {
                Vector3d ascending = nodeLine.normalized;
                Vector3d descending = -1.0 * ascending;
                double rAsc = OrbitMath.RadiusAtDirection(el, mu, ascending);
                double rDesc = OrbitMath.RadiusAtDirection(el, mu, descending);
                double tAsc = Lead(OrbitMath.TimeToDirection(state.R, state.V, mu, ascending), el.Period);
                double tDesc = Lead(OrbitMath.TimeToDirection(state.R, state.V, mu, descending), el.Period);
                bool useAscending = SameRadius(rAsc, rDesc) ? tAsc <= tDesc : rAsc > rDesc;
                dt = useAscending ? tAsc : tDesc;
                where = useAscending ? "at ascending node" : "at descending node";
            }

            KeplerPropagator.Propagate(state.R, state.V, mu, dt, out var rb, out var vb);
            Vector3d axis = rb.normalized;
            double delta = Math.Abs(iTarget - el.InclinationRad);
            Vector3d vPlus = OrbitMath.Rotate(vb, axis, delta);
            Vector3d vMinus = OrbitMath.Rotate(vb, axis, -delta);
            double errPlus = Math.Abs(OrbitMath.Elements(rb, vPlus, mu).InclinationRad - iTarget);
            double errMinus = Math.Abs(OrbitMath.Elements(rb, vMinus, mu).InclinationRad - iTarget);
            Vector3d vNew = errPlus <= errMinus ? vPlus : vMinus;

            AddBurn(plan, mu, state.UT + dt, rb, vb, vNew - vb, $"Inclination {iTarget / Deg:n2}° {where}");
            state = new State { R = rb, V = vNew, UT = state.UT + dt };
        }

        // Burn at true anomaly 'nu' (0 = periapsis, π = apoapsis) so that the opposite apsis
        // becomes 'oppositeRadius'. On a near-circular orbit the burn happens in 3 minutes.
        static void ShapeBurn(OrbitPlan plan, ref State state, double mu, double nu, double oppositeRadius, string label)
        {
            var el = OrbitMath.Elements(state.R, state.V, mu);
            bool circular = el.ApoapsisRadius - el.PeriapsisRadius < CircularThresholdMeters;
            double dt = circular
                ? CircularBurnDelaySeconds
                : Lead(OrbitMath.TimeToTrueAnomaly(state.R, state.V, mu, nu), el.Period);
            string where = circular ? "in 3 min" : nu == 0 ? "at periapsis" : "at apoapsis";

            KeplerPropagator.Propagate(state.R, state.V, mu, dt, out var rb, out var vb);
            double rMag = rb.magnitude;
            double vNew = Math.Sqrt(mu * (2 / rMag - 2 / (rMag + oppositeRadius)));
            Vector3d dv = (vNew - vb.magnitude) * vb.normalized;
            AddBurn(plan, mu, state.UT + dt, rb, vb, dv, $"{label} {where}");
            state = new State { R = rb, V = vb + dv, UT = state.UT + dt };
        }

        static void AddBurn(OrbitPlan plan, double mu, double ut, Vector3d r, Vector3d v, Vector3d dv, string label)
        {
            Vector3d prograde = v.normalized;
            Vector3d normal = Vector3d.Cross(r, v).normalized;
            Vector3d radial = Vector3d.Cross(prograde, normal);
            plan.Burns.Add(new PlannedBurn
            {
                UT = ut,
                DeltaV = dv,
                Prograde = Vector3d.Dot(dv, prograde),
                Normal = Vector3d.Dot(dv, normal),
                Radial = Vector3d.Dot(dv, radial),
                Label = label,
                Target = TargetOrbit.FromState(r, v + dv, mu)
            });
        }

        // Leaves the Node tab at least MinLeadSeconds to turn the vessel.
        static double Lead(double dt, double period)
        {
            while (dt < MinLeadSeconds)
                dt += period;
            return dt;
        }

        static OrbitPlan Fail(OrbitPlan plan, OrbitPlanError error)
        {
            plan.Error = error;
            plan.Burns.Clear();
            plan.Capture = null;
            return plan;
        }
    }
}
