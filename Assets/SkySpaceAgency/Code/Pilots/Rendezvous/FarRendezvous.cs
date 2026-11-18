using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using K2D2.Lift;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Rendezvous
{
    // The transfer search the chain plans with. The glue passes TransferSearch.Find; a gravity assist
    // (4.6) would pass its own, assist-aware search (the plane rides on the request's final target).
    public delegate TransferPlan TransferFinder(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress);

    // What "Transfer + rendezvous (+ dock)" is asked for: our vessel, and a target vessel around another body.
    public class FarRendezvousRequest
    {
        public int FromBody, TargetBody;     // indices in the BodySystem
        public Vector3d R0, V0;              // our vessel, relative to FromBody, common frame
        public double UT0;
        public Vector3d TargetR, TargetV;    // the target vessel at UT0, relative to TargetBody, ITS body's own frame
        public string TargetName = "";
        public bool Dock = true;
        public double MaxTripSeconds = double.PositiveInfinity;
    }

    public class FarRendezvousPlan
    {
        public bool Ok;
        public string Refusal = "";
        public string TargetName = "";
        public TransferPlan Transfer;
        public double ParkingRadius;
        public string ParkingReason = "";
        public Vector3d TargetPlaneNormal;               // target body's own frame
        public Vector3d ArrivalAsymptote;                // the incoming asymptote, same frame
        public string AsymptoteSource = "";              // "propagated", "last leg", "closest approach"
        public double PlaneResidualDeg;
        public RvPlan Rendezvous;                        // the worst phase sample
        public double RendezvousBest, RendezvousWorst;   // m/s, over the phases planned
        public int PhasesPlanned;                        // of PhaseSamples
        public double DockReserve;
        public double PlanSeconds;                       // the estimate's time (search excluded), for the log
        public double TotalDeltaV => (Transfer?.TotalDeltaV ?? 0) + RendezvousWorst + DockReserve;
        public string Explanation = "";
    }

    // Sub-project 4.5: one button for a target vessel around another body. A 4.1 transfer into a
    // circular parking orbit under the target, in the plane closest to the target's that the arrival
    // allows (the arrival corrections aim it, ArrivalTargeting); then the 4.3 rendezvous, planned in
    // flight from the state reached, and budgeted here as the worst of PhaseSamples target phases (the
    // target moves during the transfer, its phase at the arrival is not known); then the docking
    // reserve. Pure code: no game API, safe on a worker thread.
    public static class FarRendezvous
    {
        public const int PhaseSamples = 12;
        // A change of the target's plane (°) above this is logged and taken by the corrections.
        public const double PlaneChangeLogDeg = 0.01;

        const double Deg = Math.PI / 180;
        const int ClosestSamples = 400;

        // ------------------------------------------------------------ target

        // Null when the target can be met this way, else why not (checked before any burn).
        public static string TargetRefusal(BodySystem s, int ourBody, int targetBody, bool exists, bool landed, OrbitElements target)
        {
            if (!exists || s == null || targetBody < 0 || targetBody >= s.Count)
                return "Choose a target vessel.";
            if (landed)
                return "The target is landed: a rendezvous needs a vessel in orbit.";
            var body = s[targetBody];
            if (targetBody == ourBody)
                return $"The target orbits {body.Name}, like us: use Rendezvous + dock.";
            if (body.ParentIndex < 0)
                return $"The target orbits {body.Name}: a rendezvous around the star is not planned.";
            if (!target.IsClosed || double.IsNaN(target.PeriapsisRadius) || !(target.ApoapsisRadius < body.Soi))
                return $"The target is not on a closed orbit of {body.Name}.";
            double clear = body.Radius + Math.Max(body.AtmosphereDepth, body.MaxTerrainHeight);
            if (!(target.PeriapsisRadius > clear))
                return Inv($"The target's periapsis ({(target.PeriapsisRadius - body.Radius) / 1000:F0} km) is inside the atmosphere or under the terrain of {body.Name}.");
            return null;
        }

        // The transfer asked of the search: a circular orbit at the parking radius, in the target's plane.
        public static TransferRequest TransferRequestFor(FarRendezvousRequest q, double parkingRadius) => new TransferRequest
        {
            FromBody = q.FromBody,
            ToBody = q.TargetBody,
            R0 = q.R0,
            V0 = q.V0,
            UT0 = q.UT0,
            Arrival = ArrivalKind.Orbit,
            ArrivalPeriapsisRadius = parkingRadius,
            ArrivalApoapsisRadius = parkingRadius,
            ArrivalInclinationDeg = double.NaN,
            ArrivalPlaneNormal = Vector3d.Cross(q.TargetR, q.TargetV).normalized,
            MaxTripSeconds = q.MaxTripSeconds,
        };

        // ------------------------------------------------------------ arrival geometry

        // The incoming asymptote at the target body (its own frame), predicted by flying the plan's
        // burns with a vector (the corrections, solved in flight, are not in it). From the last leg's
        // request on a staged arrival. Without an SOI entry: the direction of the relative velocity at
        // the closest approach. False when none can be predicted.
        public static bool ArrivalAsymptote(BodySystem s, TransferPlan plan, out Vector3d sHatLocal, out string source)
        {
            sHatLocal = Vector3d.zero;
            source = "";
            if (s == null || plan == null)
                return false;
            bool lastLeg = plan.FinalLegRequest != null;
            var start = plan.FinalLegRequest ?? plan.Request;
            if (start == null || start.ToBody < 0 || start.ToBody >= s.Count || start.FromBody < 0 || start.FromBody >= s.Count)
                return false;
            int to = start.ToBody;
            var target = s[to];
            var impulses = plan.Steps
                .Where(st => st.UT >= start.UT0 && st.DeltaV.magnitude > 0)
                .Select(st => new Impulse(st.UT, st.DeltaV))
                .ToList();
            double arrival = double.IsNaN(plan.ArrivalUT) ? start.UT0 : plan.ArrivalUT;
            double horizon = arrival + 0.25 * Math.Max(0, arrival - start.UT0) + 3600;
            if (!(horizon > start.UT0))
                return false;
            var patches = PatchedPropagator.Propagate(s, start.FromBody, start.R0, start.V0, start.UT0, horizon, impulses, 12);

            foreach (var p in patches)
                if (p.Body == to)
                {
                    var r = CelestialFrames.ToLocal(target.FrameToCommon, p.StartR);
                    var v = CelestialFrames.ToLocal(target.FrameToCommon, p.StartV);
                    var sHat = ArrivalTargeting.IncomingAsymptote(r, v, target.Mu);
                    if (!(sHat.magnitude > 0))
                        sHat = v.normalized; // a closed arrival: its start velocity
                    if (!(sHat.magnitude > 0.5))
                        return false;
                    sHatLocal = sHat;
                    source = lastLeg ? "last leg" : "propagated";
                    return true;
                }

            // No SOI entry: the relative velocity at the closest approach, around the target's parent.
            double bestD = double.PositiveInfinity, bestT = double.NaN;
            Patch bestP = null;
            foreach (var p in patches)
            {
                if (p.Body != target.ParentIndex)
                    continue;
                double t = ClosestUT(s, p, to, out double d);
                if (d < bestD)
                {
                    bestD = d;
                    bestT = t;
                    bestP = p;
                }
            }
            if (bestP == null || double.IsNaN(bestT))
                return false;
            Vector3d vv = bestP.StartV;
            if (bestT != bestP.StartUT)
                KeplerPropagator.Propagate(bestP.StartR, bestP.StartV, s[bestP.Body].Mu, bestT - bestP.StartUT, out _, out vv);
            s.StateRelativeToParent(to, bestT, out _, out var vt);
            var rel = CelestialFrames.ToLocal(target.FrameToCommon, vv - vt);
            if (!(rel.magnitude > 0))
                return false;
            sHatLocal = rel.normalized;
            source = lastLeg ? "last leg, closest approach" : "closest approach";
            return true;
        }

        // The time of the closest approach to 'body' over a patch around its parent, and the distance.
        static double ClosestUT(BodySystem s, Patch p, int body, out double distance)
        {
            double Dist(double ut)
            {
                Vector3d r = p.StartR;
                if (ut != p.StartUT)
                    KeplerPropagator.Propagate(p.StartR, p.StartV, s[p.Body].Mu, ut - p.StartUT, out r, out _);
                s.StateRelativeToParent(body, ut, out var tr, out _);
                return (r - tr).magnitude;
            }
            double span = p.EndUT - p.StartUT;
            if (!(span > 0))
            {
                distance = Dist(p.StartUT);
                return p.StartUT;
            }
            double dt = span / ClosestSamples, best = double.PositiveInfinity, bestT = p.StartUT;
            for (int i = 0; i <= ClosestSamples; i++)
            {
                double t = p.StartUT + i * dt, d = Dist(t);
                if (d < best)
                {
                    best = d;
                    bestT = t;
                }
            }
            double lo = Math.Max(p.StartUT, bestT - dt), hi = Math.Min(p.EndUT, bestT + dt);
            const double g = 0.6180339887498949;
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo);
            double f1 = Dist(x1), f2 = Dist(x2);
            for (int i = 0; i < 60 && hi - lo > 1; i++)
            {
                if (f1 < f2)
                {
                    hi = x2; x2 = x1; f2 = f1;
                    x1 = hi - g * (hi - lo); f1 = Dist(x1);
                }
                else
                {
                    lo = x1; x1 = x2; f1 = f2;
                    x2 = lo + g * (hi - lo); f2 = Dist(x2);
                }
            }
            if (f1 < best) { best = f1; bestT = x1; }
            if (f2 < best) { best = f2; bestT = x2; }
            distance = best;
            return bestT;
        }

        // ------------------------------------------------------------ rendezvous estimate

        // The rendezvous from a circular orbit of radius parkingRadius in the plane closest to the
        // target's through sHat (its periapsis on the B side), at arrivalUT, for PhaseSamples phases of
        // the target (its state at targetUT, moved on by k/12 of its period). All vectors in the target
        // body's own frame. Returns the Ok plan of highest Δv (its plane match is inside), best the
        // lowest, planned the Ok count; null when none.
        public static RvPlan EstimateRendezvous(Vector3d sHat, Vector3d targetNormal, double parkingRadius, double mu, double arrivalUT,
            Vector3d targetR, Vector3d targetV, double targetUT, double lowOrbitRadius, CancellationToken ct, out double best, out int planned) =>
            Estimate(sHat, targetNormal, parkingRadius, mu, arrivalUT, targetR, targetV, targetUT, lowOrbitRadius, ct, null, out best, out planned, out _);

        static RvPlan Estimate(Vector3d sHat, Vector3d targetNormal, double parkingRadius, double mu, double arrivalUT,
            Vector3d targetR, Vector3d targetV, double targetUT, double lowOrbitRadius, CancellationToken ct, IProgress<float> progress,
            out double best, out int planned, out string firstRefusal)
        {
            best = double.NaN;
            planned = 0;
            firstRefusal = null;
            var n = targetNormal.normalized;
            // As the arrival correction aims it (ArrivalTargeting.PlaneAimMinDeg): the closest plane
            // through the asymptote, or, with the normal along it, any plane through it (about 90°
            // left, budgeted as such).
            var h = ArrivalTargeting.ClosestPlaneNormal(sHat, n, Math.Sin(ArrivalTargeting.PlaneAimMinDeg * Deg));
            if (!(h.magnitude > 0))
                h = Perpendicular(sHat);
            var pHat = Vector3d.Cross(sHat, h).normalized;
            var r = parkingRadius * pHat;
            var v = Math.Sqrt(mu / parkingRadius) * Vector3d.Cross(h, pHat).normalized;

            var elT = OrbitMath.Elements(targetR, targetV, mu);
            double period = elT.Period;
            RvPlan worst = null;
            for (int k = 0; k < PhaseSamples; k++)
            {
                ct.ThrowIfCancellationRequested();
                double t = arrivalUT + k * period / PhaseSamples;
                KeplerPropagator.Propagate(targetR, targetV, mu, t - targetUT, out var rT, out var vT);
                var plan = RendezvousPlanner.Plan(r, v, rT, vT, mu, arrivalUT, lowOrbitRadius, ct: ct);
                progress?.Report((k + 1f) / PhaseSamples);
                if (plan == null || !plan.Ok)
                {
                    if (firstRefusal == null)
                        firstRefusal = plan?.Refusal ?? "no plan";
                    continue;
                }
                planned++;
                if (double.IsNaN(best) || plan.TotalDeltaV < best)
                    best = plan.TotalDeltaV;
                if (worst == null || plan.TotalDeltaV > worst.TotalDeltaV)
                    worst = plan;
            }
            return worst;
        }

        static Vector3d Perpendicular(Vector3d h)
        {
            var a = Math.Abs(h.x) < 0.9 ? new Vector3d(1, 0, 0) : new Vector3d(0, 1, 0);
            return Vector3d.Cross(h, a).normalized;
        }

        static double PlaneMatchDeltaV(RvPlan p) =>
            p?.Steps?.Where(st => st.Kind == RvStepKind.PlaneMatch).Sum(st => st.DeltaVMagnitude) ?? 0;

        // ------------------------------------------------------------ plan

        public static FarRendezvousPlan Plan(BodySystem s, FarRendezvousRequest q, TransferFinder find, CancellationToken ct,
            IProgress<float> progress = null)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (q == null) throw new ArgumentNullException(nameof(q));
            if (find == null) throw new ArgumentNullException(nameof(find));
            ct.ThrowIfCancellationRequested();
            progress?.Report(0);
            var result = new FarRendezvousPlan { TargetName = q.TargetName ?? "" };
            if (q.TargetBody < 0 || q.TargetBody >= s.Count)
                return Refuse(result, "Choose a target vessel.");
            var body = s[q.TargetBody];
            var elT = OrbitMath.Elements(q.TargetR, q.TargetV, body.Mu);
            string refusal = TargetRefusal(s, q.FromBody, q.TargetBody, true, false, elT);
            if (refusal != null)
                return Refuse(result, refusal);

            result.ParkingRadius = RendezvousLaunch.ParkingRadius(elT.PeriapsisRadius, body.LowOrbitRadius, body.Radius, out result.ParkingReason);
            result.TargetPlaneNormal = elT.AngularMomentum.normalized;
            var request = TransferRequestFor(q, result.ParkingRadius);
            var transfer = find(s, request, ct, progress == null ? null : new Scaled(progress, 0, 0.8f));
            ct.ThrowIfCancellationRequested();
            result.Transfer = transfer;
            if (transfer == null || !transfer.Ok)
                return Refuse(result, $"No transfer to {body.Name}: {(transfer?.Refusal ?? "no plan").Trim().TrimEnd('.')}.");
            if (transfer.Request == null)
                transfer.Request = request;

            if (!ArrivalAsymptote(s, transfer, out var sHat, out string source))
                return Refuse(result, $"Cannot predict the arrival geometry at {body.Name}: plan the transfer in the Transfer tab, then the rendezvous there.");
            result.ArrivalAsymptote = sHat;
            result.AsymptoteSource = source;
            result.PlaneResidualDeg = ArrivalTargeting.PlaneResidualDeg(sHat, result.TargetPlaneNormal);
            progress?.Report(0.8f);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var rv = Estimate(sHat, result.TargetPlaneNormal, result.ParkingRadius, body.Mu, transfer.ArrivalUT,
                q.TargetR, q.TargetV, q.UT0, body.LowOrbitRadius, ct, progress == null ? null : new Scaled(progress, 0.8f, 1),
                out double best, out int planned, out string firstRefusal);
            result.PlanSeconds = watch.Elapsed.TotalSeconds;
            if (rv == null)
                return Refuse(result, Inv($"No rendezvous with {q.TargetName} from the {(result.ParkingRadius - body.Radius) / 1000:F0} km orbit at {body.Name}: ")
                    + (firstRefusal ?? "no phase could be planned").Trim().TrimEnd('.') + ".");
            result.Rendezvous = rv;
            result.RendezvousBest = best;
            result.RendezvousWorst = rv.TotalDeltaV;
            result.PhasesPlanned = planned;
            result.DockReserve = q.Dock ? RendezvousFlight.DockReserve : 0;

            double inc = elT.InclinationRad / Deg;
            result.Explanation =
                Inv($"Transfer to {body.Name} into a {(result.ParkingRadius - body.Radius) / 1000:F0} km orbit ({result.ParkingReason}), aimed at {q.TargetName}'s plane ({inc:F2}°): ")
                + Inv($"{result.PlaneResidualDeg:F2}° left after the arrival correction ({PlaneMatchDeltaV(rv):F0} m/s plane match). ")
                + Inv($"Rendezvous planned at the arrival from the actual phase: {best:F0} to {result.RendezvousWorst:F0} m/s over {planned} phases, the worst budgeted.")
                + (q.Dock ? Inv($" Docking reserve {RendezvousFlight.DockReserve:F0} m/s.") : "");
            result.Ok = true;
            progress?.Report(1);
            return result;
        }

        static FarRendezvousPlan Refuse(FarRendezvousPlan p, string why)
        {
            p.Ok = false;
            p.Refusal = why;
            return p;
        }

        // Reports a sub-task's progress into [from, to] of the whole.
        sealed class Scaled : IProgress<float>
        {
            readonly IProgress<float> _outer;
            readonly float _from, _to;
            public Scaled(IProgress<float> outer, float from, float to) { _outer = outer; _from = from; _to = to; }
            public void Report(float value) => _outer.Report(_from + (_to - _from) * Math.Max(0, Math.Min(1, value)));
        }

        // ------------------------------------------------------------ budget

        // The per-stage check of the transfer steps from 'from' on (captures checked as 4.1 does),
        // then the rendezvous as one step; the reserve is the transfer's plus the docking reserve.
        public static BudgetResult Budget(TransferPlan transfer, int from, double rendezvousDeltaV, double dockReserve, IReadOnlyList<StageDv> stages)
        {
            var dvs = new List<double>();
            var captures = new List<int>();
            if (transfer?.Steps != null)
                for (int i = Math.Max(0, from); i < transfer.Steps.Count; i++)
                {
                    if (transfer.Steps[i].Kind == StepKind.Capture)
                        captures.Add(dvs.Count);
                    dvs.Add(transfer.Steps[i].DeltaVMagnitude);
                }
            dvs.Add(Math.Max(0, double.IsNaN(rendezvousDeltaV) ? 0 : rendezvousDeltaV));
            double reserve = (transfer?.ReserveDeltaV ?? 0) + Math.Max(0, dockReserve);
            return StageBudget.Check(stages ?? new List<StageDv>(), dvs, captures, reserve);
        }

        // ------------------------------------------------------------ tab

        // The plan table: the transfer's steps, then the plane match left, the rendezvous and the docking.
        public static List<RendezvousReadout.StepRow> Rows(FarRendezvousPlan p, BodySystem s, double now, int transferDoneBefore)
        {
            var rows = new List<RendezvousReadout.StepRow>();
            if (p?.Transfer == null)
                return rows;
            foreach (var t in TransferReadout.Steps(p.Transfer, s, now, transferDoneBefore))
                rows.Add(new RendezvousReadout.StepRow { Kind = t.Kind, When = t.When, DeltaV = t.DeltaV, Explanation = t.Explanation, Done = t.Done });
            int to = p.Transfer.Request?.ToBody ?? -1;
            string body = s != null && to >= 0 && to < s.Count ? s[to].Name : "the target's body";
            string target = TargetName(p);
            if (p.PlaneResidualDeg > RendezvousPlanner.PlaneMatchThresholdDeg)
                rows.Add(new RendezvousReadout.StepRow
                {
                    Kind = $"Plane match left ({body})",
                    When = "after the arrival",
                    DeltaV = Inv($"{PlaneMatchDeltaV(p.Rendezvous):F1} m/s"),
                    Explanation = Inv($"{p.PlaneResidualDeg:F2}° between the arrival orbit and {target}'s, which the arrival cannot remove: the Rendezvous pilot's first burn (in the rendezvous below)"),
                });
            rows.Add(new RendezvousReadout.StepRow
            {
                Kind = $"Rendezvous with {target} (planned at the arrival)",
                When = "after the arrival",
                DeltaV = Inv($"up to {p.RendezvousWorst:F0} m/s"),
                Explanation = Inv($"The target's phase at the arrival is not known yet: {p.RendezvousBest:F0} to {p.RendezvousWorst:F0} m/s over {p.PhasesPlanned} phases."),
            });
            if (p.DockReserve > 0)
                rows.Add(new RendezvousReadout.StepRow
                {
                    Kind = "Docking",
                    When = "after the rendezvous",
                    DeltaV = Inv($"{p.DockReserve:F0} m/s reserve"),
                    Explanation = "The approach to the target's free docking port, flown by the Rendezvous pilot",
                });
            return rows;
        }

        static string TargetName(FarRendezvousPlan p) => string.IsNullOrEmpty(p.TargetName) ? "the target" : p.TargetName;

        public static string Totals(FarRendezvousPlan p, BudgetResult budget)
        {
            if (p == null) return "";
            string text = Inv($"Total {p.TotalDeltaV:F0} m/s (transfer {p.Transfer?.TotalDeltaV ?? 0:F0}, rendezvous up to {p.RendezvousWorst:F0}")
                + (p.DockReserve > 0 ? Inv($", docking reserve {p.DockReserve:F0})") : ")");
            if (budget != null)
            {
                double margin = budget.Available - budget.Needed;
                text += Inv($"; the vessel has {budget.Available:F0} m/s, ") +
                    (margin >= 0 ? Inv($"{margin:F0} m/s left") : Inv($"short by {-margin:F0} m/s"));
            }
            return text;
        }

        // ------------------------------------------------------------ chain rules and texts

        public enum ResumeRoute { ChainSearch, PlainWithTargetPlane, PlainNoPlane }

        // How a chain whose plan must be searched again resumes. The target gone (handoffWhy set): the
        // plain search, aimed at the periapsis only (no rendezvous follows). Already in the target's
        // SOI (a failed capture): the plain search, which plans the orbit there, in the target's plane
        // (the chain's search refuses a target around our own body). Else the chain's search.
        public static ResumeRoute RouteForResume(int ourBody, int targetBody, string handoffWhy)
        {
            if (handoffWhy != null)
                return ResumeRoute.PlainNoPlane;
            if (ourBody == targetBody)
                return ResumeRoute.PlainWithTargetPlane;
            return ResumeRoute.ChainSearch;
        }

        // Whether the rendezvous counts in the go/no-go of the stages from step 'from': only while the
        // departure is still ahead (the first burn left is a plane change, an orbit change or the
        // injection, outside the target's SOI), when stopping leaves the vessel in a safe orbit. In
        // cruise or at the capture the transfer goes on to the parking orbit, and the Rendezvous pilot
        // checks its own budget at the hand-over.
        public static bool GatesOnChain(TransferPlan plan, int from)
        {
            if (plan?.Steps == null || plan.CaseName == TransferSearch.InTarget)
                return false;
            for (int i = Math.Max(0, from); i < plan.Steps.Count; i++)
            {
                switch (plan.Steps[i].Kind)
                {
                    case StepKind.Escape:
                        continue;
                    case StepKind.PlaneChange:
                    case StepKind.OrbitChange:
                    case StepKind.Injection:
                        return true;
                    default:
                        return false;
                }
            }
            return false;
        }

        // On a resume, a chain plan refused for its rendezvous only (its transfer is fine): the transfer
        // alone is flown to the parking orbit, and the hand-over decides there.
        public static bool TransferAloneOnResume(FarRendezvousPlan p) =>
            p != null && !p.Ok && p.Transfer != null && p.Transfer.Ok;

        // The target orbits another body than ours: the far button, not 4.3's.
        public static bool IsFar(string ourBody, string targetBody) =>
            !string.IsNullOrEmpty(ourBody) && !string.IsNullOrEmpty(targetBody) && !string.Equals(ourBody, targetBody, StringComparison.Ordinal);

        public static string FarHint(string target, string targetBody) =>
            $"{target} orbits {targetBody}: Transfer + rendezvous + dock plans the transfer, the rendezvous and the docking.";

        // The target at the hand-over, in the parking orbit: null when it can still be met.
        public static string HandoffRefusal(bool exists, bool landed, bool aroundArrivalBody, string arrivalBody)
        {
            if (!exists)
                return "the target vessel no longer exists (recovered, destroyed, or docked elsewhere)";
            if (landed)
                return "the target is landed";
            if (!aroundArrivalBody)
                return $"the target no longer orbits {arrivalBody}";
            return null;
        }

        // What a stop of the chain says (6b): the step, why, and what is left to do.
        public static string ChainStopped(string stepName, string target, string why, bool afterArrival)
        {
            string reason = (why ?? "").Trim().TrimEnd('.');
            string left = afterArrival
                ? "the vessel is in its parking orbit: choose a target in the Rendezvous tab and start the rendezvous there"
                : "press Resume (Rendezvous or Transfer tab): the transfer is planned again from here, then the rendezvous follows";
            return $"Transfer + rendezvous with {target} stopped during {stepName}: {reason}. Left to do: {left}";
        }

        public static string HandoffStarted(string target, bool dock, bool prepareOnly) =>
            $"In the parking orbit: the Rendezvous pilot takes over ({(dock ? "rendezvous + docking" : "rendezvous only")} with {target}{(prepareOnly ? ", Prepare only" : "")}), see the Rendezvous tab";

        public static string ResumeOffer(string transferOffer, string target) =>
            transferOffer + " Then the rendezvous with " + target + ".";

        // Whether the target's plane moved by more than PlaneChangeLogDeg; deg is the angle.
        public static bool PlaneChanged(Vector3d before, Vector3d now, out double deg)
        {
            deg = double.NaN;
            if (!(before.magnitude > 0) || !(now.magnitude > 0))
                return false;
            var a = before.normalized;
            var b = now.normalized;
            deg = Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) / Deg;
            return deg > PlaneChangeLogDeg;
        }

        static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);
    }
}
