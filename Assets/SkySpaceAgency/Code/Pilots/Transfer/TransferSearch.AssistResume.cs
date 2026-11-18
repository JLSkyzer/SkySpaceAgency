using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        // A gravity assist resumed after its departure (4.6), from the vessel's state in q:
        //  - BeforeFlyby: cruising around the flyby body's parent; a correction now, the flyby, the
        //    target (flyby dates within 15 % of the time left to flybyUT, arrivals within 20 % of
        //    the planned leg);
        //  - InFlybySoi: in the flyby body's SOI, periapsis ahead; a new excess out and a new burn.
        // The other routes are the glue's (the plain search). A refusal says why: the glue then
        // plans again without the assist.
        public static TransferPlan ResumeAssist(BodySystem s, TransferRequest q, AssistResumeRoute route, int flyby, double flybyUT,
            double arrivalUT, CancellationToken ct)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (q == null)
                throw new ArgumentNullException(nameof(q));
            ct.ThrowIfCancellationRequested();
            // Still in the departure body on the escape (after the injection): the cruise starts at the
            // SOI exit, planned from there.
            if (route == AssistResumeRoute.BeforeFlyby && flyby >= 0 && flyby < s.Count && q.FromBody != s[flyby].ParentIndex
                && s[q.FromBody].ParentIndex == s[flyby].ParentIndex)
                q = ExitRequest(s, q, out _) ?? q;
            var search = new Search(s, q, ct, null);
            TransferPlan plan;
            if (flyby < 0 || flyby >= s.Count)
                plan = search.RefuseWith(NoAssist("unknown flyby body"));
            else if (route == AssistResumeRoute.BeforeFlyby)
                plan = search.ResumeBeforeFlyby(flyby, flybyUT, arrivalUT);
            else if (route == AssistResumeRoute.InFlybySoi)
                plan = search.ResumeInFlybySoi(flyby, arrivalUT);
            else
                plan = search.RefuseWith(NoAssist($"the route {route} is the plain search"));
            plan.Request = q;
            AssistSearch.Log?.Invoke($"[Transfer] assist: resume {route}: " +
                (plan.Ok ? plan.TotalDeltaV.ToString("F0", CultureInfo.InvariantCulture) + " m/s" : "refused: " + plan.Refusal));
            return plan;
        }

        // The plain search, except on an open path that leaves the vessel's body (a resume after a
        // departure burn or a flyby periapsis, 4.6): the path is followed to the SOI exit as it is, and
        // the plan made from there, with a warning. The plain search refuses such a path.
        public static TransferPlan FindFromExit(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress = null)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (q == null)
                throw new ArgumentNullException(nameof(q));
            var exit = ExitRequest(s, q, out double exitUT);
            if (exit == null)
                return Find(s, q, ct, progress);
            var plan = Find(s, exit, ct, progress);
            if (plan.Ok)
                plan.Warnings.Add(Inv($"Planned from the {s[q.FromBody].Name} SOI exit, in {KerbinCalendar.Duration(exitUT - q.UT0)}: the path until then is flown as it is."));
            return plan;
        }

        // Whether q's path is open and leaves its body (FindFromExit plans from the SOI exit).
        public static bool LeavesItsBody(BodySystem s, TransferRequest q) => s != null && q != null && ExitRequest(s, q, out _) != null;

        // The request from the SOI exit of an open path that leaves q's body for its parent; null
        // when the path is closed, already at the target, or does not leave.
        static TransferRequest ExitRequest(BodySystem s, TransferRequest q, out double exitUT)
        {
            exitUT = double.NaN;
            var B = s[q.FromBody];
            if (q.FromBody == q.ToBody || B.ParentIndex < 0 || !Hyperbola.IsFinite(q.R0) || !Hyperbola.IsFinite(q.V0))
                return null;
            var el = OrbitMath.Elements(q.R0, q.V0, B.Mu);
            if (el.IsClosed && el.ApoapsisRadius < B.Soi)
                return null;
            double horizon = q.UT0 + Finite(TimeToRadius(q.R0, q.V0, B.Mu, B.Soi), 0) * 1.2 + 3600;
            var patches = PatchedPropagator.Propagate(s, q.FromBody, q.R0, q.V0, q.UT0, horizon, null, 3);
            var after = patches.FirstOrDefault(p => p.Body == B.ParentIndex);
            if (after == null || patches.Any(p => p.End == PatchEnd.Impact))
                return null;
            exitUT = after.StartUT;
            return new TransferRequest
            {
                FromBody = after.Body, ToBody = q.ToBody, R0 = after.StartR, V0 = after.StartV, UT0 = after.StartUT, Arrival = q.Arrival,
                ArrivalPeriapsisRadius = q.ArrivalPeriapsisRadius, ArrivalApoapsisRadius = q.ArrivalApoapsisRadius,
                ArrivalInclinationDeg = q.ArrivalInclinationDeg, MaxTripSeconds = q.MaxTripSeconds, ArrivalPlaneNormal = q.ArrivalPlaneNormal,
            };
        }

        sealed partial class Search
        {
            const double ResumeLead = ExistingCorrectionLead;

            public TransferPlan ResumeBeforeFlyby(int flyby, double flybyUT, double arrivalUT)
            {
                var early = Setup();
                if (early != null)
                    return early.Ok ? Refuse(NoAssist("the vessel is not cruising")) : early;
                var B = S[flyby];
                if (B.ParentIndex != Ancestor || flyby == Down[0])
                    return Refuse(NoAssist($"{B.Name} is not a body on the way around {S[Ancestor].Name}"));
                if (From != Ancestor)
                    return Refuse(NoAssist($"the vessel does not cruise around {S[B.ParentIndex].Name} toward {B.Name}"));
                double left = flybyUT - Q.UT0, leg2 = arrivalUT - flybyUT;
                if (!(left > ResumeLead) || !(leg2 > 0))
                    return Refuse(NoAssist($"the {B.Name} flyby is not ahead"));
                Planned.Add(flyby);
                int cT = Down[0];
                double depLo = Q.UT0 + ResumeLead, depHi = depLo + 0.05 * left;
                double fLo = flybyUT - 0.15 * left, fHi = flybyUT + 0.15 * left;
                var dirs = Directions(VesselEl.AngularMomentum);
                var legs = new AssistLegs
                {
                    DepartureState = t =>
                    {
                        VesselAt(t, out var r, out var v);
                        return (r, v);
                    },
                    FlybyState = t => StateOf(flyby, t),
                    ArrivalState = t => StateOf(cT, t),
                    LeaveCost = v => v,
                    ArriveCost = v => EstimateArrive(0, v),
                    Mu = S[From].Mu, FlybyMu = B.Mu, FlybyFloor = B.LowOrbitRadius, FlybyCeiling = FlybyModel.MaxPeriapsisShare * B.Soi,
                    Tof1Lo = Math.Max(1, fLo - depHi), Tof1Hi = fHi - depLo, Tof2Lo = 0.8 * leg2, Tof2Hi = 1.2 * leg2,
                    FlybyLo = Math.Max(fLo, depLo + 1), FlybyHi = fHi,
                    DepartureLo = depLo, DepartureHi = depHi,
                    MaxTrip = double.PositiveInfinity,
                    Directions1 = dirs, Directions2 = dirs,
                    FixedDeparture = true,
                };
                var list = AssistPorkchop.Search(legs, Ct, null, 0, 1);
                if (list.Count == 0)
                    return Refuse(NoAssist($"no flyby of {B.Name} is reachable from here"));
                Built best = null;
                foreach (var c in list.Take(2))
                {
                    var b = BuildResumeBeforeFlyby(flyby, c);
                    if (best == null || b.Cost < best.Cost)
                        best = b;
                }
                return best.Plan;
            }

            Built BuildResumeBeforeFlyby(int flyby, AssistCandidate c)
            {
                Ct.ThrowIfCancellationRequested();
                var B = S[flyby];
                string name = B.Name;
                double mu = S[From].Mu, dep = c.DepartureUT, tF = c.FlybyUT;
                VesselAt(dep, out var r, out var v);
                S.StateRelativeToParent(flyby, tF, out var rB, out _);
                if (!Lambert.Solve(r, rB, tF - dep, mu, c.Prograde1, c.Revolutions1, c.LongPeriod1, out var v1, out _))
                    return Failed(NoAssist($"no path from here to {name}"));
                var burn = v1 - v;
                var flyTarget = new Target(flyby, Math.Max(c.FlybyPeriapsis, B.LowOrbitRadius), double.NaN, double.NaN,
                    CelestialFrames.ToLocal(B.FrameToCommon, c.Normal));
                double horizon = tF + 0.25 * (tF - dep);
                for (int iter = 0; iter < 3; iter++)
                {
                    if (!ArrivalTargeting.Solve(S, From, r, v + burn, dep, flyby, flyTarget.Pe, double.NaN, flyTarget.Plane, horizon, tF,
                            double.NaN, ArrivalTargeting.MaxDeltaV, out var dvAim, out _, out _, out _, out _, out _))
                        break;
                    burn += dvAim;
                    if (dvAim.magnitude < 1e-3)
                        break;
                }
                var cruise = PatchedPropagator.Propagate(S, From, r, v + burn, dep, horizon, null, 4);
                var inside = cruise.FirstOrDefault(p => p.Body == flyby);
                if (inside == null)
                    return FlightFailure(NoAssist($"the corrected cruise does not reach {name}"));
                var built = new Built();
                NoteCruise(built, cruise.TakeWhile(p => p != inside), flyby);
                var plan = NewPlan();
                var now = Burn(StepKind.Correction, From, dep, burn,
                    Inv($"Correction now: re-aims the cruise at the {name} flyby ({burn.magnitude:F1} m/s, re-planned from the actual path)"), 0);
                flyTarget.ApplyTo(now);
                plan.Steps.Add(now);
                var failed = AppendFlyby(built, plan, flyby, inside.StartR, inside.StartV, inside.StartUT, c.ArrivalUT, c.Prograde2, 1,
                    out var arr, out double tPe);
                if (failed != null)
                    return failed;
                AddCorrections(plan.Steps, From, 0, flyTarget, dep, dep, tPe, inside.StartUT, null);
                foreach (var st in plan.Steps.Where(st => st.LegIndex == 0 && st.Kind == StepKind.Correction && st != now))
                    st.Explanation = st.Explanation.Replace($"Correction after leaving {S[From].Name}", "Correction after the re-aim");
                var targets = new Dictionary<int, Target> { [0] = flyTarget, [1] = arr.FirstTarget, [2] = arr.FirstTarget };
                return Finish(built, plan, dep, arr.ArrivalUT, targets);
            }

            public TransferPlan ResumeInFlybySoi(int flyby, double arrivalUT)
            {
                if (From != flyby)
                    return Refuse(NoAssist($"the vessel is not in the {S[flyby].Name} SOI"));
                var early = Setup(openOrbit: true);
                if (early != null)
                    return early.Ok ? Refuse(NoAssist("the vessel is already at the target")) : early;
                if (CaseName != Siblings && CaseName != DifferentBranches)
                    return Refuse(NoAssist($"the case is {CaseName}"));
                var B = S[flyby];
                // A departure flyby (a moon of the departure body) has no sibling leg to re-plan here.
                if (B.ParentIndex != Ancestor || flyby == Down[0])
                    return Refuse(NoAssist($"{B.Name} is not a body on the way around {S[Ancestor].Name}"));
                if (!(Vector3d.Dot(Q.R0, Q.V0) < 0) || VesselEl.IsClosed && VesselEl.ApoapsisRadius < B.Soi)
                    return Refuse(NoAssist($"no {B.Name} flyby periapsis ahead"));
                int cT = Down[0];
                double mu = B.Mu, muA = S[Ancestor].Mu;
                double tPe = Q.UT0 + Finite(TimeToPeriapsis(Q.R0, Q.V0, mu), 0);
                KeplerPropagator.Propagate(Q.R0, Q.V0, mu, tPe - Q.UT0, out var rPe, out var vPe);
                if (rPe.magnitude < Floor(flyby))
                    return Refuse($"The {B.Name} flyby periapsis is below its atmosphere or terrain ({Km(flyby, rPe.magnitude)} km): correct it first.");
                double leg2 = arrivalUT - tPe;
                if (!(leg2 > 0))
                    return Refuse(NoAssist("the planned arrival is behind"));
                S.StateRelativeToParent(flyby, tPe, out var rB, out var vB);
                var dirs = Directions(Vector3d.Cross(S[flyby].EpochPosition, S[flyby].EpochVelocity));
                double best = double.PositiveInfinity, bestUT = double.NaN;
                bool bestDir = true;
                const int samples = 25; // odd: the planned arrival is one of them
                for (int i = 0; i < samples; i++)
                {
                    Ct.ThrowIfCancellationRequested();
                    double tA = tPe + leg2 * (0.8 + 0.4 * i / (samples - 1));
                    S.StateRelativeToParent(cT, tA, out var rT, out var vT);
                    foreach (bool d in dirs)
                    {
                        if (!Lambert.Solve(rB, rT, tA - tPe, muA, d, 0, false, out var w1, out var w2))
                            continue;
                        if (!AsymptoteBurn(rPe, vPe, w1 - vB, mu, Floor(flyby), out var dv, out _))
                            continue;
                        double cost = dv.magnitude + EstimateArrive(0, (w2 - vT).magnitude);
                        if (cost < best)
                        {
                            best = cost;
                            bestUT = tA;
                            bestDir = d;
                        }
                    }
                }
                if (double.IsInfinity(best))
                    return Refuse(NoAssist($"no burn at the {B.Name} periapsis reaches {S[cT].Name}"));
                var built = new Built();
                var plan = NewPlan();
                var failed = AppendFlyby(built, plan, flyby, Q.R0, Q.V0, Q.UT0, bestUT, bestDir, 0, out var arr, out _);
                if (failed != null)
                    return failed.Plan;
                var targets = new Dictionary<int, Target> { [0] = arr.FirstTarget, [1] = arr.FirstTarget };
                return Finish(built, plan, tPe, arr.ArrivalUT, targets).Plan;
            }
        }
    }
}
