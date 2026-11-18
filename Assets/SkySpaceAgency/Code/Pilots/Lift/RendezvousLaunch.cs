using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using K2D2.OrbitPlanning;
using K2D2.Rendezvous;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Lift
{
    // What the pass choice needs. Every vector is in the launch body's celestial frame (z = rotation
    // axis), read from orbits around that body at UT0: the site, the target and ω share it, and
    // nothing here converts frames.
    public class RendezvousLaunchRequest
    {
        public string BodyName = "";
        public double BodyRadius, Mu, LowOrbitRadius, ClearRadius;   // ClearRadius: radius + max(atmosphere, terrain)
        public Vector3d Omega, SiteAtUT0;
        public double UT0;
        public Vector3d TargetR, TargetV;                            // at UT0
        public double LeadSeconds, DownrangeRad;
        // No pass before UT0 + this: the plan itself takes game time on the pad.
        public double StartDelaySeconds;
    }

    // One launch pass and the rendezvous it would leave after the ascent.
    public class RendezvousPass
    {
        public LaunchWindowResult Window;
        public RvPlan Rendezvous;
        public double DeltaV = double.NaN;   // NaN when the planner refused
        public double PhaseDeg;              // the target's lead on us at the insertion, (−180, 180]
    }

    public class RendezvousLaunchPlan
    {
        public bool Ok;
        public string Refusal = "";
        public double ParkingRadius;
        public string ParkingReason = "";
        public Vector3d TargetPlaneNormal;           // unit, rT × vT
        public LaunchWindowResult Window;            // the chosen pass
        public RvPlan Rendezvous;                    // planned from that pass's predicted insertion
        public List<RendezvousPass> Passes = new List<RendezvousPass>();
        public int Chosen = -1;
        public string Explanation = "";
    }

    // Lift for a rendezvous (sub-project 4.4): which launch pass, among the next ones, both reaches
    // the target's plane and leaves the cheapest rendezvous after the ascent. Each pass's insertion is
    // predicted (a circular orbit at the parking radius, in the plane the pass reaches) and scored by
    // the 4.3 planner. Pure code: no game API, safe on a worker thread.
    public static class RendezvousLaunch
    {
        public const double BelowTarget = 10_000;     // m under the target's periapsis
        public const double MaxAboveLow = 150_000;    // m above the low orbit, at most (user, 2026-11-18)
        public const double TieDeltaV = 5;            // m/s: within this of the cheapest, the earliest wins
        public const double HorizonRotations = 4;
        public const int MaxPasses = 12;
        public const double PlaneResidualDeg = 0.05;  // above this, the explanation names the plane match

        const double Deg = Math.PI / 180;

        public static string TargetRefusal(bool exists, bool landed, string targetBody, string launchBody,
            OrbitElements target, double clearRadius, double bodyRadius)
        {
            if (!exists)
                return "Choose the rendezvous target.";
            if (landed)
                return "The target is landed: a rendezvous needs a vessel in orbit.";
            if (!string.Equals(targetBody, launchBody, StringComparison.Ordinal))
                return $"The target orbits {targetBody}, not {launchBody}: lift to orbit with the Orbit goal, then use Transfer + rendezvous + dock in the Rendezvous tab.";
            if (!target.IsClosed || double.IsNaN(target.PeriapsisRadius))
                return "The target is not on a closed orbit.";
            if (!(target.PeriapsisRadius > clearRadius))
                return Inv($"The target's periapsis ({(target.PeriapsisRadius - bodyRadius) / 1000:F0} km) is inside the atmosphere or under the terrain of {launchBody}.");
            return null;
        }

        // A circular parking orbit 10 km under the target's periapsis, never under the low orbit,
        // never above the low orbit + 150 km (4.3's phasing raises the rest).
        public static double ParkingRadius(double targetPeriapsisRadius, double lowOrbitRadius, double bodyRadius, out string reason)
        {
            double under = targetPeriapsisRadius - BelowTarget;
            if (!(under >= lowOrbitRadius))
            {
                reason = Inv($"the low orbit ({(lowOrbitRadius - bodyRadius) / 1000:F0} km): the target's periapsis is too low for 10 km under it");
                return lowOrbitRadius;
            }
            if (under > lowOrbitRadius + MaxAboveLow)
            {
                reason = "capped at the low orbit + 150 km: the 4.3 phasing raises the rest";
                return lowOrbitRadius + MaxAboveLow;
            }
            reason = "10 km under the target's periapsis";
            return under;
        }

        // The predicted state at the end of the ascent: circular at the parking radius, prograde in
        // the plane the pass reaches.
        public static void Insertion(LaunchWindowResult w, double parkingRadius, double mu, out Vector3d r, out Vector3d v)
        {
            Vector3d q = w.InsertionPoint.normalized;
            r = parkingRadius * q;
            v = Math.Sqrt(mu / parkingRadius) * Vector3d.Cross(w.ReachedPlaneNormal.normalized, q).normalized;
        }

        // The target's angle ahead of us in our direction of motion (positive: ahead), (−180, 180].
        public static double PhaseDeg(Vector3d r, Vector3d rT, Vector3d planeNormal)
        {
            Vector3d n = planeNormal.normalized;
            Vector3d t = rT - Vector3d.Dot(rT, n) * n;
            double a = Math.Atan2(Vector3d.Dot(Vector3d.Cross(r, t), n), Vector3d.Dot(r, t)) / Deg;
            return a <= -180 ? 180 : a;
        }

        // The cheapest pass; an earlier one within TieDeltaV of it wins. −1 when none has a rendezvous.
        public static int Choose(IReadOnlyList<RendezvousPass> passes)
        {
            double min = double.PositiveInfinity;
            foreach (var p in passes)
                if (IsFinite(p.DeltaV) && p.DeltaV < min)
                    min = p.DeltaV;
            if (double.IsPositiveInfinity(min))
                return -1;
            for (int k = 0; k < passes.Count; k++)
                if (IsFinite(passes[k].DeltaV) && passes[k].DeltaV <= min + TieDeltaV)
                    return k;
            return -1;
        }

        public static RendezvousLaunchPlan Plan(RendezvousLaunchRequest q, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var plan = new RendezvousLaunchPlan();
            var elT = OrbitMath.Elements(q.TargetR, q.TargetV, q.Mu);
            string refusal = TargetRefusal(true, false, q.BodyName, q.BodyName, elT, q.ClearRadius, q.BodyRadius);
            if (refusal != null)
                return Refuse(plan, refusal);

            plan.ParkingRadius = ParkingRadius(elT.PeriapsisRadius, q.LowOrbitRadius, q.BodyRadius, out plan.ParkingReason);
            plan.TargetPlaneNormal = elT.AngularMomentum.normalized;

            double omega = Math.Abs(q.Omega.z);
            double horizon = omega > 1e-15 ? HorizonRotations * 2 * Math.PI / omega : 2 * elT.Period;
            double step = elT.Period / 12;
            double delay = q.StartDelaySeconds > 0 && !double.IsInfinity(q.StartDelaySeconds) ? q.StartDelaySeconds : 0;
            double start = q.UT0 + delay;
            Vector3d site = TurnZ(q.SiteAtUT0, q.Omega.z * delay);
            var windows = LaunchWindow.Passes(q.BodyRadius, q.Mu, q.Omega, site, start, plan.TargetPlaneNormal,
                plan.ParkingRadius, q.LeadSeconds, q.DownrangeRad, horizon, MaxPasses, step);
            if (windows.Count > 0 && AnyTimeIsCheap(windows[0], plan.TargetPlaneNormal, plan.ParkingRadius, q.Mu))
                windows = AnyTimePasses(q, site, start, plan.TargetPlaneNormal, plan.ParkingRadius, step);
            if (windows.Count == 0)
            {
                var first = LaunchWindow.Find(q.BodyRadius, q.Mu, q.Omega, site, start, plan.TargetPlaneNormal,
                    plan.ParkingRadius, q.LeadSeconds, q.DownrangeRad, horizon);
                return Refuse(plan, $"No launch pass within {KerbinCalendar.Duration(horizon)}: {(first.Ok ? LaunchWindow.NoPass : first.Refusal)}");
            }

            foreach (var w in windows)
            {
                ct.ThrowIfCancellationRequested();
                Insertion(w, plan.ParkingRadius, q.Mu, out var r, out var v);
                KeplerPropagator.Propagate(q.TargetR, q.TargetV, q.Mu, w.InsertionUT - q.UT0, out var rT, out var vT);
                var rv = RendezvousPlanner.Plan(r, v, rT, vT, q.Mu, w.InsertionUT, q.LowOrbitRadius, ct: ct);
                plan.Passes.Add(new RendezvousPass
                {
                    Window = w,
                    Rendezvous = rv,
                    DeltaV = rv.Ok ? rv.TotalDeltaV : double.NaN,
                    PhaseDeg = PhaseDeg(r, rT, w.ReachedPlaneNormal),
                });
            }

            plan.Chosen = Choose(plan.Passes);
            if (plan.Chosen < 0)
                return Refuse(plan, $"No rendezvous from any of the {plan.Passes.Count} launch passes: {plan.Passes[0].Rendezvous.Refusal}");

            var chosen = plan.Passes[plan.Chosen];
            plan.Window = chosen.Window;
            plan.Rendezvous = chosen.Rendezvous;
            plan.Ok = true;
            plan.Explanation = Explain(plan, q, elT);
            return plan;
        }

        // A plane out of reach (the first pass is under its vertex, with a residual): launching at
        // any other time costs at most the chord of 2i more. When that is within TieDeltaV (a nearly
        // equatorial target), the passes sample the target's orbit instead of one per rotation.
        public static bool AnyTimeIsCheap(LaunchWindowResult first, Vector3d targetPlaneNormal, double parkingRadius, double mu)
        {
            if (first == null || !first.Ok || first.Pass != PlanePass.Any || !(first.PlaneErrorDeg > 0))
                return false;
            double iDeg = Math.Acos(Math.Min(1, Math.Abs(targetPlaneNormal.normalized.z))) / Deg;
            double vo = Math.Sqrt(mu / parkingRadius);
            double extra = 2 * vo * (Math.Sin((first.PlaneErrorDeg + 2 * iDeg) * Deg / 2) - Math.Sin(first.PlaneErrorDeg * Deg / 2));
            return extra <= TieDeltaV;
        }

        static List<LaunchWindowResult> AnyTimePasses(RendezvousLaunchRequest q, Vector3d siteAtStart, double start,
            Vector3d planeNormal, double parkingRadius, double step)
        {
            var list = new List<LaunchWindowResult>();
            for (int k = 0; k < MaxPasses; k++)
            {
                double t = start + k * step;
                var w = LaunchWindow.AtTime(q.BodyRadius, q.Mu, q.Omega, TurnZ(siteAtStart, q.Omega.z * (t - start)), t,
                    planeNormal, parkingRadius, q.LeadSeconds, q.DownrangeRad);
                if (!w.Ok)
                    break;
                list.Add(w);
            }
            return list;
        }

        static string Explain(RendezvousLaunchPlan plan, RendezvousLaunchRequest q, OrbitElements elT)
        {
            var c = plan.Passes[plan.Chosen];
            double dv1 = plan.Passes[0].DeltaV;
            string first = IsFinite(dv1) ? Inv($"the first pass would cost {dv1:F0} m/s.") : "the first pass has no rendezvous.";
            string text = Inv($"Launch on pass {plan.Chosen + 1} of {plan.Passes.Count} (in {KerbinCalendar.Duration(Math.Max(0, c.Window.LaunchUT - q.UT0))}): rendezvous for {c.DeltaV:F0} m/s ({c.Rendezvous.Revolutions} revolution(s), arrival {KerbinCalendar.Date(c.Rendezvous.ArrivalUT)}); {first}");
            if (c.Window.PlaneErrorDeg > PlaneResidualDeg)
            {
                double inc = Math.Acos(Math.Max(-1, Math.Min(1, plan.TargetPlaneNormal.z))) / Deg;
                Vector3d up = q.SiteAtUT0.normalized;
                double lat = Math.Abs(Math.Asin(Math.Max(-1, Math.Min(1, up.z)))) / Deg;
                double dvp = c.Window.PlaneChangeDeltaV;
                foreach (var s in c.Rendezvous.Steps)
                    if (s.Kind == RvStepKind.PlaneMatch)
                        dvp = s.DeltaVMagnitude;
                text += Inv($" The target's plane ({inc:F2}°) is below the site's latitude ({lat:F2}°): the closest plane is flown, then a {c.Window.PlaneErrorDeg:F2}° plane match ({dvp:F0} m/s) in orbit.");
            }
            return text;
        }

        static RendezvousLaunchPlan Refuse(RendezvousLaunchPlan plan, string why)
        {
            plan.Ok = false;
            plan.Refusal = why;
            return plan;
        }

        static Vector3d TurnZ(Vector3d p, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return new Vector3d(c * p.x - s * p.y, s * p.x + c * p.y, p.z);
        }

        static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
