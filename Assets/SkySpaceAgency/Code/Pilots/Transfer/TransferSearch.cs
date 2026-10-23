using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    // The cheapest transfer from the vessel's orbit to any body (patched conics). The case comes from
    // the body hierarchy only, through the common ancestor A of the two bodies:
    //  - target = A (child → parent or ancestor): an escape whose orbit around A has its periapsis at
    //    the arrival radius;
    //  - vessel's body = A (parent → child or descendant): a Lambert search inside A's SOI, from the
    //    vessel's orbit to the child's ephemeris;
    //  - otherwise (siblings, different branches): a porkchop of Lambert transfers at A between the two
    //    children of A, then the climb out of the vessel's branch and the descent into the target's.
    // Climbing out of a moon, and descending into one, each level is either straight through or
    // through a low periapsis of that level (Oberth), whichever is cheaper; the plan shows each stage.
    // The grids use circular, coplanar estimates; only the refined candidates are costed from the actual
    // state (DeparturePlanner) and flown through the PatchedPropagator.
    // Pure code: no game API, safe on a worker thread. Honours the token (throws
    // OperationCanceledException) and reports progress in [0, 1].
    public static class TransferSearch
    {
        public const string SameBody = "The target is the body you are orbiting.";
        public const string NoWindow = "No window within the maximum trip duration.";
        public const string NotClosed = "Not in a closed orbit.";
        public static string LowCapture(string body) => $"Capture orbit below the atmosphere or terrain of {body}.";

        public const string ChildToParent = "Child to parent";
        public const string ChildToAncestor = "Child to ancestor";
        public const string ParentToChild = "Parent to child";
        public const string ParentToDescendant = "Parent to descendant";
        public const string Siblings = "Siblings";
        public const string DifferentBranches = "Different branches";

        public static TransferPlan Find(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress = null)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (q == null)
                throw new ArgumentNullException(nameof(q));
            if (q.FromBody < 0 || q.FromBody >= s.Count || q.ToBody < 0 || q.ToBody >= s.Count)
                throw new ArgumentException("Unknown body index.");
            ct.ThrowIfCancellationRequested();
            progress?.Report(0);
            var plan = new Search(s, q, ct, progress).Run();
            plan.Request = q;
            progress?.Report(1);
            return plan;
        }

        sealed class Leg
        {
            public bool Ok = true;
            public string Refusal;
            public readonly List<PlanStep> Steps = new List<PlanStep>();
            public readonly List<Impulse> Impulses = new List<Impulse>();
            public double Cost;
            public double FirstBurnUT = double.NaN;
            public double ExitUT = double.NaN; // SOI exit of the last level climbed
        }

        sealed class Arrival
        {
            public readonly List<PlanStep> Steps = new List<PlanStep>();
            public readonly List<string> Notes = new List<string>();
            public double Cost;
            public double ArrivalUT;
        }

        sealed class Built
        {
            public TransferPlan Plan;
            public int Unplanned = -1; // a body entered on the departure that the plan does not pass through
            public double UnplannedUT;
            public double Cost => Plan.Ok ? Plan.TotalDeltaV : double.PositiveInfinity;
        }

        sealed class Search
        {
            const int SiblingDepartureSamples = 120, SiblingTofSamples = 50;
            const int ChildTofSamples = 16, MaxChildDepartureSamples = 1200, ChildSamplesPerOrbit = 24;
            const int RefinedCandidates = 4;
            const int PhaseSamples = 72;
            const double SafetyMargin = 10_000; // LowOrbitRadius minus this: the top of the atmosphere or terrain
            const int MaxShifts = 12;
            const double ShiftTolerance = 1.03; // a shifted departure may cost 3 % more
            const double PlaneTurnExplainedDeg = 10;

            readonly BodySystem S;
            readonly TransferRequest Q;
            readonly CancellationToken Ct;
            readonly IProgress<float> Progress;
            readonly int From, To;
            readonly double[] Sma;
            int Ancestor;
            readonly List<int> Up = new List<int>();   // [From, …, child of A]; empty when From = A
            readonly List<int> Down = new List<int>(); // [child of A, …, To]; empty when To = A
            readonly HashSet<int> Planned = new HashSet<int>();
            double ArrRp, ArrRa, MaxTrip;
            OrbitElements VesselEl;
            double VesselPeriod;
            string CaseName = "";
            float _progress;

            public Search(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress)
            {
                S = s;
                Q = q;
                Ct = ct;
                Progress = progress;
                From = q.FromBody;
                To = q.ToBody;
                Sma = new double[s.Count];
                for (int i = 0; i < s.Count; i++)
                {
                    var n = s[i];
                    if (n.ParentIndex >= 0)
                        Sma[i] = OrbitMath.Elements(n.EpochPosition, n.EpochVelocity, s[n.ParentIndex].Mu).SemiMajorAxis;
                }
            }

            public TransferPlan Run()
            {
                if (From == To)
                    return Refuse(SameBody);
                var target = S[To];
                ArrRp = double.IsNaN(Q.ArrivalPeriapsisRadius) ? target.LowOrbitRadius : Q.ArrivalPeriapsisRadius;
                ArrRa = double.IsNaN(Q.ArrivalApoapsisRadius) ? ArrRp : Q.ArrivalApoapsisRadius;
                if (ArrRa < ArrRp)
                    (ArrRp, ArrRa) = (ArrRa, ArrRp);
                if (!(ArrRp >= target.LowOrbitRadius - SafetyMargin))
                    return Refuse(LowCapture(target.Name));
                MaxTrip = double.IsNaN(Q.MaxTripSeconds) ? double.PositiveInfinity : Q.MaxTripSeconds;

                Ancestor = S.CommonAncestor(From, To);
                for (int b = From; b != Ancestor; b = S[b].ParentIndex)
                    Up.Add(b);
                for (int b = To; b != Ancestor; b = S[b].ParentIndex)
                    Down.Add(b);
                Down.Reverse();
                Planned.Add(Ancestor);
                Planned.UnionWith(Up);
                Planned.UnionWith(Down);

                if (To == Ancestor)
                    CaseName = S[From].ParentIndex == To ? ChildToParent : ChildToAncestor;
                else if (From == Ancestor)
                    CaseName = S[To].ParentIndex == From ? ParentToChild : ParentToDescendant;
                else
                    CaseName = S[From].ParentIndex == S[To].ParentIndex ? Siblings : DifferentBranches;

                if (!Hyperbola.IsFinite(Q.R0) || !Hyperbola.IsFinite(Q.V0) || !(Q.R0.magnitude > 0))
                    return Refuse("Invalid vessel state.");
                VesselEl = OrbitMath.Elements(Q.R0, Q.V0, S[From].Mu);
                bool closed = VesselEl.IsClosed && VesselEl.ApoapsisRadius < S[From].Soi;
                if (!closed)
                    return To == Ancestor ? AlreadyEscaping() : Refuse(NotClosed);
                VesselPeriod = VesselEl.Period;

                if (To == Ancestor)
                    return ToAncestor();
                if (From == Ancestor)
                    return ToDescendant();
                return Porkchop();
            }

            // ---------------------------------------------------------------- child → parent / ancestor

            // The excess velocity leaving cF (a child of the target) at t, so that the orbit around the
            // target is tangent at cF's position and has its other apsis at rpAim.
            Vector3d ExcessToward(double t, double rpAim)
            {
                int cF = Up[Up.Count - 1];
                double mu = S[To].Mu;
                S.StateRelativeToParent(cF, t, out var rC, out var vC);
                Vector3d along = Vector3d.Cross(Vector3d.Cross(rC, vC), rC).normalized;
                double rc = rC.magnitude;
                double speed = Math.Sqrt(mu * (2 / rc - 2 / (rc + rpAim)));
                return speed * along - vC;
            }

            TransferPlan ToAncestor()
            {
                int k = Up.Count - 1;
                double w0 = ExcessToward(Q.UT0, ArrRp).magnitude;
                double tExit = Q.UT0 + 1.2 * UpLead(k, Math.Max(1, w0));
                Report(0.2f);
                var built = BuildToAncestor(tExit);
                Report(0.6f);
                built = ResolveCrossing(built, shift => BuildToAncestor(tExit + shift * ShiftUnit()));
                return built.Plan;
            }

            Built BuildToAncestor(double tExit)
            {
                Ct.ThrowIfCancellationRequested();
                int k = Up.Count - 1;
                var target = S[To];
                double mu = target.Mu;
                double rc = Sma[Up[k]];
                double transfer = Math.PI * Math.Sqrt(Cube((rc + ArrRp) / 2) / mu);

                double rpAim = ArrRp, prevAim = double.NaN, prevAct = double.NaN;
                Leg leg = null;
                Patch arrival = null;
                var built = new Built();
                int iterations = k == 0 ? 8 : 1; // only a direct departure is flown to refine the periapsis
                for (int iter = 0; iter < iterations; iter++)
                {
                    leg = Leave(k, ExcessToward(tExit, rpAim), tExit);
                    if (!leg.Ok)
                        return Failed(leg.Refusal);
                    var patches = FlyDeparture(leg, leg.ExitUT + 1.5 * transfer + 1);
                    NoteCrossing(built, patches);
                    arrival = patches.FirstOrDefault(p => p.Body == To);
                    if (k > 0 || arrival == null)
                        break;
                    double act = arrival.PeriapsisRadius;
                    double err = ArrRp - act;
                    if (Math.Abs(err) < Math.Max(200, 1e-4 * ArrRp))
                        break;
                    double next = double.IsNaN(prevAim) || act == prevAct
                        ? rpAim + err
                        : rpAim + err * (rpAim - prevAim) / (act - prevAct);
                    prevAim = rpAim;
                    prevAct = act;
                    rpAim = Math.Max(1000, Math.Min(10 * rc, next));
                }

                double arrUT = arrival != null && arrival.PeriapsisUT >= arrival.StartUT
                    ? arrival.PeriapsisUT
                    : leg.ExitUT + transfer;
                var plan = NewPlan();
                plan.Steps.AddRange(leg.Steps);
                if (Q.Arrival == ArrivalKind.Orbit)
                {
                    double vAt;
                    if (arrival != null)
                    {
                        double energy = 0.5 * arrival.StartV.sqrMagnitude - mu / arrival.StartR.magnitude;
                        vAt = Math.Sqrt(Math.Max(0, 2 * (energy + mu / ArrRp)));
                    }
                    else
                        vAt = Math.Sqrt(mu * (2 / ArrRp - 2 / (ArrRp + rc)));
                    double vOrbit = Math.Sqrt(mu * (2 / ArrRp - 2 / (ArrRp + ArrRa)));
                    plan.Steps.Add(Step(StepKind.Capture, To, arrUT, Math.Abs(vAt - vOrbit),
                        Inv($"Capture at the {target.Name} periapsis ({Km(To, ArrRp)} km) into a {Km(To, ArrRp)} × {Km(To, ArrRa)} km orbit")));
                }
                AddCorrections(plan.Steps, To, leg.ExitUT, leg.FirstBurnUT, arrUT, arrUT, null);
                return Finish(built, plan, leg.FirstBurnUT, arrUT);
            }

            // A vessel already on an escape: no burn, the escape is followed to the target.
            TransferPlan AlreadyEscaping()
            {
                double horizon = Q.UT0 + S.OrbitalPeriod(Up[Up.Count - 1]);
                var patches = PatchedPropagator.Propagate(S, From, Q.R0, Q.V0, Q.UT0, horizon, null, Up.Count + 1);
                var arrival = patches.FirstOrDefault(p => p.Body == To);
                if (arrival == null)
                    return Refuse(NotClosed);
                var plan = NewPlan();
                double exit = Q.UT0;
                foreach (var p in patches)
                    if (p.End == PatchEnd.Escape)
                    {
                        plan.Steps.Add(Step(StepKind.Escape, p.Body, p.EndUT, 0, $"Leave the {S[p.Body].Name} SOI"));
                        exit = p.EndUT;
                    }
                double arrUT = arrival.PeriapsisUT >= arrival.StartUT ? arrival.PeriapsisUT : arrival.StartUT;
                var target = S[To];
                if (Q.Arrival == ArrivalKind.Orbit)
                {
                    double mu = target.Mu;
                    double energy = 0.5 * arrival.StartV.sqrMagnitude - mu / arrival.StartR.magnitude;
                    double vAt = Math.Sqrt(Math.Max(0, 2 * (energy + mu / ArrRp)));
                    double vOrbit = Math.Sqrt(mu * (2 / ArrRp - 2 / (ArrRp + ArrRa)));
                    plan.Steps.Add(Step(StepKind.Capture, To, arrUT, Math.Abs(vAt - vOrbit),
                        Inv($"Capture at the {target.Name} periapsis ({Km(To, ArrRp)} km) into a {Km(To, ArrRp)} × {Km(To, ArrRa)} km orbit; the corrections first move the periapsis there from {Km(To, arrival.PeriapsisRadius)} km")));
                }
                AddCorrections(plan.Steps, To, exit, Q.UT0, arrUT, arrUT, null);
                return Finish(new Built(), plan, Q.UT0, arrUT).Plan;
            }

            // ---------------------------------------------------------------- parent → child / descendant

            TransferPlan ToDescendant()
            {
                int cT = Down[0];
                double mu = S[From].Mu;
                double aC = Sma[cT];
                double hohmann = Math.PI * Math.Sqrt(Cube((VesselEl.SemiMajorAxis + aC) / 2) / mu);
                // Departure UT: the vessel's phase (one orbit) over half the child's period, so the child
                // crosses the vessel's orbit plane at least once (an inclined or polar parking orbit).
                double window = S.OrbitalPeriod(cT) / 2 + VesselPeriod;
                int nT = (int)Math.Min(MaxChildDepartureSamples, Math.Max(48, Math.Ceiling(window / (VesselPeriod / ChildSamplesPerOrbit))));
                double tofLo = 0.5 * hohmann, tofHi = 1.5 * hohmann;
                var dirs = Directions(VesselEl.AngularMomentum);

                double Cost(double t, double tof, bool prograde)
                {
                    if (t < Q.UT0 || !(tof > 0) || tof > MaxTrip)
                        return double.PositiveInfinity;
                    VesselAt(t, out var rV, out var vV);
                    return ChildCost(rV, vV, t, tof, prograde);
                }

                var grid = new double[nT, ChildTofSamples];
                var gridDir = new bool[nT, ChildTofSamples];
                for (int i = 0; i < nT; i++)
                {
                    Ct.ThrowIfCancellationRequested();
                    double t = Q.UT0 + window * i / (nT - 1);
                    VesselAt(t, out var rV, out var vV);
                    for (int j = 0; j < ChildTofSamples; j++)
                    {
                        double tof = tofLo + (tofHi - tofLo) * j / (ChildTofSamples - 1);
                        grid[i, j] = double.PositiveInfinity;
                        if (tof > MaxTrip)
                            continue;
                        foreach (bool d in dirs)
                        {
                            double c = ChildCost(rV, vV, t, tof, d);
                            if (c < grid[i, j])
                            {
                                grid[i, j] = c;
                                gridDir[i, j] = d;
                            }
                        }
                    }
                    Report(0.7f * (i + 1) / nT);
                }

                var candidates = Refine(grid, gridDir, Q.UT0, window / (nT - 1), tofLo, (tofHi - tofLo) / (ChildTofSamples - 1), Cost);
                if (candidates.Count == 0)
                    return Refuse(double.IsInfinity(MaxTrip) ? $"No transfer found to {S[To].Name}." : NoWindow);
                Report(0.8f);
                Built best = null;
                (double t, double tof, bool prograde) bestCand = default;
                foreach (var c in candidates)
                {
                    var b = BuildToDescendant(c.t, c.tof, c.prograde);
                    if (best == null || b.Cost < best.Cost)
                    {
                        best = b;
                        bestCand = c;
                    }
                }
                Report(0.9f);
                if (best.Plan.Ok)
                    best = ResolveCrossing(best, shift =>
                        BuildToDescendant(bestCand.t + shift * VesselPeriod, bestCand.tof, bestCand.prograde));
                return best.Plan;
            }

            // Burn cost from (rV, vV) at t to the child at t + tof, plus the estimated arrival.
            double ChildCost(Vector3d rV, Vector3d vV, double t, double tof, bool prograde)
            {
                var F = S[From];
                int cT = Down[0];
                S.StateRelativeToParent(cT, t + tof, out var rC, out var vC);
                if (!Lambert.Solve(rV, rC, tof, F.Mu, prograde, 0, false, out var v1, out var v2))
                    return double.PositiveInfinity;
                if (!PeriapsisClear(rV, v1, F.Mu, F.LowOrbitRadius - SafetyMargin))
                    return double.PositiveInfinity;
                return (v1 - vV).magnitude + EstimateArrive(0, (v2 - vC).magnitude);
            }

            Built BuildToDescendant(double t, double tof, bool prograde)
            {
                Ct.ThrowIfCancellationRequested();
                var F = S[From];
                int cT = Down[0];
                VesselAt(t, out var rV, out var vV);
                S.StateRelativeToParent(cT, t + tof, out var rC, out var vC);
                if (!Lambert.Solve(rV, rC, tof, F.Mu, prograde, 0, false, out var v1, out var v2))
                    return Failed($"No transfer found to {S[To].Name}.");
                Vector3d dv = v1 - vV;
                double turn = AngleDeg(Vector3d.Cross(rV, vV), Vector3d.Cross(rV, v1));
                string text = Inv($"Transfer burn to {S[cT].Name}: {tof / 3600:F1} h of flight");
                if (turn > PlaneTurnExplainedDeg)
                    text += Inv($". Departure burn also turns the plane by {turn:F0}° (cheaper than a separate plane change)");

                var plan = NewPlan();
                plan.Steps.Add(new PlanStep { Kind = StepKind.Injection, Body = From, UT = t, DeltaV = dv, DeltaVMagnitude = dv.magnitude, Explanation = text });
                var arr = BuildArrival(0, (v2 - vC).magnitude, t + tof);
                plan.Steps.AddRange(arr.Steps);

                var built = new Built();
                var patches = PatchedPropagator.Propagate(S, From, rV, vV, t, t + tof, new[] { new Impulse(t, dv) }, 3);
                NoteCrossing(built, patches);
                var entry = patches.FirstOrDefault(p => p.End == PatchEnd.Encounter && p.NextBody == cT);
                double latest = entry != null ? entry.EndUT : t + tof;
                AddCorrections(plan.Steps, From, t, t, arr.ArrivalUT, latest, arr.Notes);
                return Finish(built, plan, t, arr.ArrivalUT);
            }

            // ---------------------------------------------------------------- siblings / different branches

            TransferPlan Porkchop()
            {
                int k = Up.Count - 1;
                int cF = Up[k], cT = Down[0];
                double mu = S[Ancestor].Mu;
                double a1 = Sma[cF], a2 = Sma[cT];
                double hohmann = Math.PI * Math.Sqrt(Cube((a1 + a2) / 2) / mu);
                double synodic = S.SynodicPeriod(cF, cT);
                if (double.IsInfinity(synodic))
                    synodic = Math.Max(S.OrbitalPeriod(cF), S.OrbitalPeriod(cT));
                double vNominal = Math.Abs(Math.Sqrt(mu / a1) - Math.Sqrt(mu * (2 / a1 - 2 / (a1 + a2))));
                // The first SOI exit leaves time to climb out of the vessel's branch.
                double t0 = Q.UT0 + UpLead(k, Math.Max(1, 0.5 * vNominal));
                double t1 = Math.Max(Q.UT0 + 2 * synodic, t0 + synodic);
                double tofLo = 0.5 * hohmann, tofHi = 1.5 * hohmann;
                var dirs = Directions(Vector3d.Cross(S[cF].EpochPosition, S[cF].EpochVelocity));

                // The search horizon is the window: the refinement stays inside it.
                double Cost(double t, double tof, bool prograde)
                {
                    if (t < t0 || t > t1 || tof < 0.3 * hohmann || tof > 2 * hohmann || tof > MaxTrip)
                        return double.PositiveInfinity;
                    S.StateRelativeToParent(cF, t, out var rF, out var vF);
                    return PorkchopCost(rF, vF, t, tof, prograde);
                }

                var grid = new double[SiblingDepartureSamples, SiblingTofSamples];
                var gridDir = new bool[SiblingDepartureSamples, SiblingTofSamples];
                double dt = (t1 - t0) / (SiblingDepartureSamples - 1);
                double dTof = (tofHi - tofLo) / (SiblingTofSamples - 1);
                for (int i = 0; i < SiblingDepartureSamples; i++)
                {
                    Ct.ThrowIfCancellationRequested();
                    double t = t0 + dt * i;
                    S.StateRelativeToParent(cF, t, out var rF, out var vF);
                    for (int j = 0; j < SiblingTofSamples; j++)
                    {
                        double tof = tofLo + dTof * j;
                        grid[i, j] = double.PositiveInfinity;
                        if (tof > MaxTrip)
                            continue;
                        foreach (bool d in dirs)
                        {
                            double c = PorkchopCost(rF, vF, t, tof, d);
                            if (c < grid[i, j])
                            {
                                grid[i, j] = c;
                                gridDir[i, j] = d;
                            }
                        }
                    }
                    Report(0.7f * (i + 1) / SiblingDepartureSamples);
                }

                var candidates = Refine(grid, gridDir, t0, dt, tofLo, dTof, Cost);
                if (candidates.Count == 0)
                    return Refuse(double.IsInfinity(MaxTrip) ? $"No transfer found to {S[To].Name}." : NoWindow);
                Report(0.8f);
                Built best = null;
                (double t, double tof, bool prograde) bestCand = default;
                foreach (var c in candidates)
                {
                    var b = BuildPorkchop(c.t, c.tof, c.prograde);
                    if (best == null || b.Cost < best.Cost)
                    {
                        best = b;
                        bestCand = c;
                    }
                }
                Report(0.9f);
                if (best.Plan.Ok)
                    best = ResolveCrossing(best, shift =>
                        BuildPorkchop(bestCand.t + shift * ShiftUnit(), bestCand.tof - shift * ShiftUnit(), bestCand.prograde));
                return best.Plan;
            }

            double PorkchopCost(Vector3d rF, Vector3d vF, double t, double tof, bool prograde)
            {
                int cT = Down[0];
                S.StateRelativeToParent(cT, t + tof, out var rT, out var vT);
                if (!Lambert.Solve(rF, rT, tof, S[Ancestor].Mu, prograde, 0, false, out var v1, out var v2))
                    return double.PositiveInfinity;
                double vinf = (v1 - vF).magnitude;
                // The trip runs from the first burn: the climb out of the vessel's branch counts.
                if (!double.IsInfinity(MaxTrip) && tof + UpLead(Up.Count - 1, vinf, false) > MaxTrip)
                    return double.PositiveInfinity;
                return EstimateLeave(Up.Count - 1, vinf) + EstimateArrive(0, (v2 - vT).magnitude);
            }

            Built BuildPorkchop(double t, double tof, bool prograde)
            {
                Ct.ThrowIfCancellationRequested();
                int k = Up.Count - 1;
                int cF = Up[k], cT = Down[0];
                S.StateRelativeToParent(cF, t, out var rF, out var vF);
                S.StateRelativeToParent(cT, t + tof, out var rT, out var vT);
                if (!Lambert.Solve(rF, rT, tof, S[Ancestor].Mu, prograde, 0, false, out var v1, out var v2))
                    return Failed($"No transfer found to {S[To].Name}.");
                Vector3d wd = v1 - vF, wa = v2 - vT;

                var leg = Leave(k, wd, t);
                if (!leg.Ok)
                    return Failed(leg.Refusal);
                var arr = BuildArrival(0, wa.magnitude, t + tof);
                var plan = NewPlan();
                plan.Steps.AddRange(leg.Steps);
                plan.Steps.AddRange(arr.Steps);

                var built = new Built();
                NoteCrossing(built, FlyDeparture(leg, leg.ExitUT + 1));
                var target = S[cT];
                double entry = t + tof - HyperbolicTime(target.LowOrbitRadius, Math.Max(1, wa.magnitude), target.Mu, target.Soi);
                AddCorrections(plan.Steps, Ancestor, leg.ExitUT, leg.FirstBurnUT, arr.ArrivalUT, entry, arr.Notes);
                return Finish(built, plan, leg.FirstBurnUT, arr.ArrivalUT);
            }

            // ---------------------------------------------------------------- climbing out

            // Leave Up[j] with the excess w (relative to Up[j], in its parent's frame) at tExit, starting
            // from the vessel's orbit in Up[0].
            Leg Leave(int j, Vector3d w, double tExit)
            {
                Ct.ThrowIfCancellationRequested();
                if (j == 0)
                    return DepartFromVessel(w, tExit);

                int x = Up[j], y = Up[j - 1];
                var X = S[x];
                var Y = S[y];
                double mu = X.Mu, vinf = w.magnitude;
                double aY = Sma[y], pY = S.OrbitalPeriod(y);
                double floor = X.LowOrbitRadius - SafetyMargin;

                // Straight through: escape Y on the X-hyperbola whose asymptote is w.
                Leg direct = null;
                {
                    double tf = HyperbolicTime(aY, Math.Max(1, vinf), mu, X.Soi);
                    double best = double.PositiveInfinity, bestT = 0;
                    Vector3d bestW = Vector3d.zero, bestR = Vector3d.zero, bestV = Vector3d.zero;
                    for (int i = 0; i < PhaseSamples; i++)
                    {
                        double tY = tExit - tf - pY * i / PhaseSamples;
                        S.StateRelativeToParent(y, tY, out var rY, out var vY);
                        if (!Hyperbola.VelocityForAsymptote(rY, w, mu, out var vA, out var vB))
                            continue;
                        foreach (var v in new[] { vA, vB })
                        {
                            if (!PeriapsisClear(rY, v, mu, floor))
                                continue;
                            Vector3d wY = v - vY;
                            double c = EstimateLeave(j - 1, wY.magnitude);
                            if (c < best)
                            {
                                best = c;
                                bestT = tY;
                                bestW = wY;
                                bestR = rY;
                                bestV = v;
                            }
                        }
                    }
                    if (!double.IsInfinity(best))
                    {
                        var inner = Leave(j - 1, bestW, bestT);
                        if (inner.Ok)
                        {
                            direct = inner;
                            direct.ExitUT = bestT + Finite(TimeToRadius(bestR, bestV, mu, X.Soi), tExit - bestT);
                            direct.Steps.Add(Step(StepKind.Escape, x, direct.ExitUT, 0, $"Leave the {X.Name} SOI"));
                        }
                    }
                }

                // Through a low periapsis of X: escape Y retrograde into an X orbit whose periapsis is
                // X's low orbit, then make the hyperbolic burn there (Oberth).
                Leg staged = null;
                double stagedBurn = 0;
                double rp = X.LowOrbitRadius;
                if (rp < aY)
                {
                    double aE = (rp + aY) / 2;
                    double half = Math.PI * Math.Sqrt(Cube(aE) / mu);
                    double va = Math.Sqrt(mu * (2 / aY - 1 / aE)), vp = Math.Sqrt(mu * (2 / rp - 1 / aE));
                    double tf = HyperbolicTime(rp, Math.Max(1, vinf), mu, X.Soi);
                    double best = double.PositiveInfinity, bestT = 0;
                    Vector3d bestW = Vector3d.zero, bestDv = Vector3d.zero, bestR = Vector3d.zero, bestV = Vector3d.zero;
                    for (int i = 0; i < PhaseSamples; i++)
                    {
                        double tY = tExit - tf - half - pY * i / PhaseSamples;
                        S.StateRelativeToParent(y, tY, out var rY, out var vY);
                        Vector3d along = Vector3d.Cross(Vector3d.Cross(rY, vY), rY).normalized;
                        Vector3d wY = va * along - vY;
                        Vector3d rPer = (-rp / rY.magnitude) * rY;
                        Vector3d vPer = (-vp) * along;
                        if (!Hyperbola.VelocityForAsymptote(rPer, w, mu, out var vA, out var vB))
                            continue;
                        foreach (var v in new[] { vA, vB })
                        {
                            if (!PeriapsisClear(rPer, v, mu, floor))
                                continue;
                            double burn = (v - vPer).magnitude;
                            double c = burn + EstimateLeave(j - 1, wY.magnitude);
                            if (c < best)
                            {
                                best = c;
                                bestT = tY;
                                bestW = wY;
                                bestDv = v - vPer;
                                bestR = rPer;
                                bestV = v;
                            }
                        }
                    }
                    if (!double.IsInfinity(best))
                    {
                        var inner = Leave(j - 1, bestW, bestT);
                        if (inner.Ok)
                        {
                            staged = inner;
                            stagedBurn = bestDv.magnitude;
                            double burnUT = bestT + half;
                            staged.Steps.Add(new PlanStep { Kind = StepKind.Injection, Body = x, UT = burnUT, DeltaV = bestDv, DeltaVMagnitude = stagedBurn });
                            staged.Impulses.Add(new Impulse(burnUT, bestDv));
                            staged.Cost += stagedBurn;
                            staged.ExitUT = burnUT + Finite(TimeToRadius(bestR, bestV, mu, X.Soi), tf);
                            staged.Steps.Add(Step(StepKind.Escape, x, staged.ExitUT, 0, $"Leave the {X.Name} SOI"));
                        }
                    }
                }

                double directCost = direct?.Cost ?? double.PositiveInfinity;
                double stagedCost = staged?.Cost ?? double.PositiveInfinity;
                if (direct == null && staged == null)
                    return new Leg { Ok = false, Refusal = $"No way found out of {X.Name} from {Y.Name}." };
                if (stagedCost < directCost)
                {
                    var burnStep = staged.Steps.Last(st => st.Kind == StepKind.Injection);
                    string saving = double.IsInfinity(directCost)
                        ? $"no direct escape from {Y.Name} reaches it"
                        : Inv($"saves {directCost - stagedCost:F0} m/s against escaping {Y.Name} directly");
                    burnStep.Explanation = Inv($"Burn at the {X.Name} periapsis ({Km(x, rp)} km), reached by escaping {Y.Name} into a {Km(x, rp)} × {Km(x, aY)} km orbit, to leave {X.Name} at {vinf:F0} m/s excess (Oberth): {saving}");
                    return staged;
                }
                var exitStep = direct.Steps.Last(st => st.Kind == StepKind.Escape);
                exitStep.Explanation = double.IsInfinity(stagedCost)
                    ? Inv($"Leave the {X.Name} SOI at {vinf:F0} m/s excess, straight from the {Y.Name} escape")
                    : Inv($"Leave the {X.Name} SOI at {vinf:F0} m/s excess, straight from the {Y.Name} escape: saves {stagedCost - directCost:F0} m/s against a burn at a low {X.Name} periapsis");
                return direct;
            }

            // The departure burn from the vessel's own orbit, planned from the actual state.
            Leg DepartFromVessel(Vector3d w, double tExit)
            {
                var F = S[From];
                double mu = F.Mu, vinf = w.magnitude;
                var leg = new Leg();
                double tf = HyperbolicTime(VesselEl.PeriapsisRadius, Math.Max(1, vinf), mu, F.Soi);
                // The planner searches one orbit from ut0: centre it on the burn the exit time asks for.
                double ut0 = Math.Max(Q.UT0, tExit - tf - VesselPeriod / 2);
                VesselAt(ut0, out var r, out var v);
                var dp = DeparturePlanner.Plan(r, v, ut0, mu, F.Soi, w);
                if (!dp.Ok)
                {
                    leg.Ok = false;
                    leg.Refusal = dp.Refusal;
                    return leg;
                }

                double ut = ut0;
                if (dp.PlaneChangeFirst)
                {
                    Coast(ref r, ref v, mu, ref ut, dp.PlaneChangeUT);
                    v += dp.PlaneChangeDeltaV;
                    leg.Steps.Add(new PlanStep
                    {
                        Kind = StepKind.PlaneChange, Body = From, UT = dp.PlaneChangeUT, DeltaV = dp.PlaneChangeDeltaV,
                        DeltaVMagnitude = dp.PlaneChangeDeltaV.magnitude, Explanation = dp.Explanation,
                    });
                    leg.Impulses.Add(new Impulse(dp.PlaneChangeUT, dp.PlaneChangeDeltaV));
                }
                Coast(ref r, ref v, mu, ref ut, dp.BurnUT);
                v += dp.DeltaV;
                if (!PeriapsisClear(r, v, mu, F.LowOrbitRadius - SafetyMargin))
                {
                    leg.Ok = false;
                    leg.Refusal = $"The departure would pass below the atmosphere or terrain of {F.Name}.";
                    return leg;
                }

                string next = S[F.ParentIndex].Name;
                string text = Inv($"Departure burn: leave {F.Name} at {vinf:F0} m/s excess, into the {next} SOI");
                if (!dp.PlaneChangeFirst && dp.OutOfPlaneDeg > PlaneTurnExplainedDeg)
                    text += Inv($". Departure burn also turns the plane by {dp.OutOfPlaneDeg:F0}° (cheaper than a separate plane change)");
                leg.Steps.Add(new PlanStep
                {
                    Kind = StepKind.Injection, Body = From, UT = dp.BurnUT, DeltaV = dp.DeltaV,
                    DeltaVMagnitude = dp.DeltaVMagnitude, Explanation = text,
                });
                leg.Impulses.Add(new Impulse(dp.BurnUT, dp.DeltaV));
                leg.Cost = dp.TotalDeltaV;
                leg.FirstBurnUT = dp.PlaneChangeFirst ? dp.PlaneChangeUT : dp.BurnUT;
                leg.ExitUT = dp.BurnUT + Finite(TimeToRadius(r, v, mu, F.Soi), tf);
                leg.Steps.Add(Step(StepKind.Escape, From, leg.ExitUT, 0, $"Leave the {F.Name} SOI"));
                return leg;
            }

            // Circular, coplanar estimate of leaving Up[j] at vinf from the vessel's orbit (the grids).
            double EstimateLeave(int j, double vinf)
            {
                if (j == 0)
                {
                    double mu0 = S[From].Mu, rp0 = VesselEl.PeriapsisRadius;
                    double vp0 = Math.Sqrt(mu0 * (2 / rp0 - 1 / VesselEl.SemiMajorAxis));
                    return Math.Sqrt(vinf * vinf + 2 * mu0 / rp0) - vp0;
                }
                var X = S[Up[j]];
                double mu = X.Mu, aY = Sma[Up[j - 1]], vY = Math.Sqrt(mu / aY);
                double direct = EstimateLeave(j - 1, Math.Sqrt(vinf * vinf + 2 * mu / aY) - vY);
                double rp = X.LowOrbitRadius;
                if (rp >= aY)
                    return direct;
                double aE = (rp + aY) / 2;
                double burn = Math.Sqrt(vinf * vinf + 2 * mu / rp) - Math.Sqrt(mu * (2 / rp - 1 / aE));
                double staged = burn + EstimateLeave(j - 1, vY - Math.Sqrt(mu * (2 / aY - 1 / aE)));
                return Math.Min(direct, staged);
            }

            // A generous bound on the time from now to the SOI exit of Up[j]: waiting for the right
            // phase at each level (one orbit), then the flights. Without the waits: from the first burn.
            double UpLead(int j, double vinf, bool waits = true)
            {
                vinf = Math.Max(1, vinf);
                if (j == 0)
                    return HyperbolicTime(VesselEl.PeriapsisRadius, vinf, S[From].Mu, S[From].Soi) + (waits ? VesselPeriod : 0);
                var X = S[Up[j]];
                double mu = X.Mu, aY = Sma[Up[j - 1]], vY = Math.Sqrt(mu / aY);
                double wDirect = Math.Sqrt(vinf * vinf + 2 * mu / aY) - vY, wStaged = wDirect;
                double flight = HyperbolicTime(aY, vinf, mu, X.Soi);
                double rp = X.LowOrbitRadius;
                if (rp < aY)
                {
                    double aE = (rp + aY) / 2;
                    flight = Math.Max(flight, Math.PI * Math.Sqrt(Cube(aE) / mu) + HyperbolicTime(rp, vinf, mu, X.Soi));
                    wStaged = vY - Math.Sqrt(mu * (2 / aY - 1 / aE));
                }
                return flight + (waits ? S.OrbitalPeriod(Up[j - 1]) : 0) + UpLead(j - 1, Math.Min(wDirect, wStaged), waits);
            }

            // ---------------------------------------------------------------- descending

            // Circular, coplanar estimate of arriving at Down[k] at vinf and ending at the target.
            double EstimateArrive(int k, double vinf)
            {
                if (k == Down.Count - 1)
                    return CaptureCost(vinf);
                var X = S[Down[k]];
                double mu = X.Mu, aZ = Sma[Down[k + 1]], vZ = Math.Sqrt(mu / aZ);
                double direct = EstimateArrive(k + 1, Math.Sqrt(vinf * vinf + 2 * mu / aZ) - vZ);
                double rp = X.LowOrbitRadius;
                if (rp >= aZ)
                    return direct;
                double aE = (rp + aZ) / 2;
                double burn = Math.Sqrt(vinf * vinf + 2 * mu / rp) - Math.Sqrt(mu * (2 / rp - 1 / aE));
                return Math.Min(direct, burn + EstimateArrive(k + 1, vZ - Math.Sqrt(mu * (2 / aZ - 1 / aE))));
            }

            double CaptureCost(double vinf) =>
                Q.Arrival == ArrivalKind.Orbit ? Hyperbola.CaptureDeltaV(vinf, ArrRp, ArrRa, S[To].Mu) : 0;

            // The arrival steps from Down[k] at vinf, arriving (periapsis, or centre in the zero-SOI
            // picture) at ut. Below the first level the geometry is estimated: the corrections aim it.
            Arrival BuildArrival(int k, double vinf, double ut)
            {
                int x = Down[k];
                var X = S[x];
                if (k == Down.Count - 1)
                {
                    var last = new Arrival { ArrivalUT = ut };
                    if (Q.Arrival == ArrivalKind.Orbit)
                    {
                        last.Cost = CaptureCost(vinf);
                        last.Steps.Add(Step(StepKind.Capture, x, ut, last.Cost,
                            Inv($"Capture at the {X.Name} periapsis ({Km(x, ArrRp)} km), arriving at {vinf:F0} m/s excess, into a {Km(x, ArrRp)} × {Km(x, ArrRa)} km orbit")));
                    }
                    return last;
                }

                int z = Down[k + 1];
                var Z = S[z];
                double mu = X.Mu, aZ = Sma[z], vZ = Math.Sqrt(mu / aZ);
                double wDirect = Math.Sqrt(vinf * vinf + 2 * mu / aZ) - vZ;
                double direct = EstimateArrive(k + 1, wDirect);
                double rp = X.LowOrbitRadius;
                double staged = double.PositiveInfinity, burn = 0, wStaged = 0, half = 0;
                if (rp < aZ)
                {
                    double aE = (rp + aZ) / 2;
                    burn = Math.Sqrt(vinf * vinf + 2 * mu / rp) - Math.Sqrt(mu * (2 / rp - 1 / aE));
                    wStaged = vZ - Math.Sqrt(mu * (2 / aZ - 1 / aE));
                    half = Math.PI * Math.Sqrt(Cube(aE) / mu);
                    staged = burn + EstimateArrive(k + 1, wStaged);
                }

                Arrival result;
                if (staged < direct)
                {
                    result = BuildArrival(k + 1, wStaged, ut + half);
                    result.Steps.Insert(0, Step(StepKind.Capture, x, ut, burn,
                        Inv($"Capture at the {X.Name} periapsis ({Km(x, rp)} km) into a {Km(x, rp)} × {Km(x, aZ)} km orbit that meets {Z.Name} at its apoapsis (Oberth): saves {direct - staged:F0} m/s against arriving at {Z.Name} directly")));
                    result.Cost += burn;
                }
                else
                {
                    result = BuildArrival(k + 1, wDirect, ut);
                    result.Notes.Add(double.IsInfinity(staged)
                        ? $"The {X.Name} arrival is aimed straight at {Z.Name}"
                        : Inv($"The {X.Name} arrival is aimed straight at {Z.Name}: saves {staged - direct:F0} m/s against capturing at {X.Name} first"));
                }
                return result;
            }

            // ---------------------------------------------------------------- shared

            // Three placeholders, solved in flight: after the SOI exit, at mid-course, on the approach.
            void AddCorrections(List<PlanStep> steps, int body, double exitUT, double departureUT, double arrivalUT,
                double latestUT, List<string> notes)
            {
                double trip = arrivalUT - departureUT;
                double end = Math.Min(arrivalUT, latestUT);
                if (!(trip > 0) || !(end > exitUT))
                    return;
                double c1 = exitUT + 0.01 * trip, c2 = departureUT + 0.5 * trip, c3 = Math.Min(departureUT + 0.9 * trip, end - 0.01 * trip);
                if (!(exitUT < c1 && c1 < c2 && c2 < c3 && c3 < end))
                {
                    double span = end - exitUT;
                    c1 = exitUT + 0.05 * span;
                    c2 = exitUT + 0.5 * span;
                    c3 = exitUT + 0.9 * span;
                }
                string aim = Inv($"the {S[To].Name} periapsis at {Km(To, ArrRp)} km");
                if (!double.IsNaN(Q.ArrivalInclinationDeg))
                    aim += Inv($" and an inclination of {Q.ArrivalInclinationDeg:F1}°");
                const string solved = "solved in flight, skipped under 1 m/s";
                steps.Add(Step(StepKind.Correction, body, c1, 0, $"Correction after leaving {S[From].Name}: aims {aim} ({solved})"));
                steps.Add(Step(StepKind.Correction, body, c2, 0, $"Mid-course correction: aims {aim} ({solved})"));
                string approach = $"Approach correction: sets {aim}, the arrival plane included ({solved})";
                if (notes != null && notes.Count > 0)
                    approach += ". " + string.Join(". ", notes);
                steps.Add(Step(StepKind.Correction, body, c3, 0, approach));
            }

            // The vessel flown from its first burn through the leg's impulses (the departure check).
            List<Patch> FlyDeparture(Leg leg, double untilUT)
            {
                double start = leg.Impulses.Min(i => i.UT);
                VesselAt(start, out var r, out var v);
                return PatchedPropagator.Propagate(S, From, r, v, start, untilUT, leg.Impulses, Up.Count + 3);
            }

            void NoteCrossing(Built built, List<Patch> patches)
            {
                built.Unplanned = -1;
                foreach (var p in patches)
                    if (p.End == PatchEnd.Encounter && !Planned.Contains(p.NextBody))
                    {
                        built.Unplanned = p.NextBody;
                        built.UnplannedUT = p.EndUT;
                        return;
                    }
            }

            // An unplanned SOI on the departure: depart a few orbits later if that costs at most 3 %
            // more, otherwise keep the plan and warn.
            Built ResolveCrossing(Built built, Func<int, Built> shifted)
            {
                if (!built.Plan.Ok || built.Unplanned < 0)
                    return built;
                for (int k = 1; k <= MaxShifts; k++)
                {
                    var b = shifted(k);
                    if (b.Plan.Ok && b.Unplanned < 0 && b.Cost <= ShiftTolerance * built.Cost)
                    {
                        var first = b.Plan.Steps.First(st => st.DeltaVMagnitude > 0);
                        first.Explanation += Inv($". Departure moved {k} orbit(s) later to avoid the {S[built.Unplanned].Name} SOI (+{b.Cost - built.Cost:F0} m/s)");
                        return b;
                    }
                }
                string warning = $"The departure crosses the {S[built.Unplanned].Name} SOI, which the plan does not include: expect a large correction, or plan again later.";
                built.Plan.Warnings.Add(warning);
                var burn = built.Plan.Steps.First(st => st.DeltaVMagnitude > 0);
                burn.Explanation += ". Warning: " + warning;
                return built;
            }

            double ShiftUnit() => Up.Count > 1 ? S.OrbitalPeriod(Up[0]) : VesselPeriod;

            Built Finish(Built built, TransferPlan plan, double departureUT, double arrivalUT)
            {
                plan.Steps = plan.Steps.OrderBy(st => st.UT).ToList(); // stable
                double sum = plan.Steps.Sum(st => st.DeltaVMagnitude);
                plan.ReserveDeltaV = Math.Max(10, 0.02 * sum);
                plan.TotalDeltaV = sum + plan.ReserveDeltaV;
                plan.DepartureUT = departureUT;
                plan.ArrivalUT = arrivalUT;
                plan.Ok = true;
                if (plan.TripSeconds > MaxTrip)
                {
                    plan.Ok = false;
                    plan.Refusal = NoWindow;
                }
                built.Plan = plan;
                return built;
            }

            // Local minima of the grid, refined by Nelder–Mead on the same estimate; the best few.
            List<(double t, double tof, bool prograde)> Refine(double[,] grid, bool[,] gridDir, double t0, double dt,
                double tof0, double dTof, Func<double, double, bool, double> cost)
            {
                int n = grid.GetLength(0), m = grid.GetLength(1);
                var minima = new List<(int i, int j)>();
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < m; j++)
                    {
                        double c = grid[i, j];
                        if (double.IsInfinity(c) || double.IsNaN(c))
                            continue;
                        bool isMin = true;
                        for (int di = -1; di <= 1 && isMin; di++)
                            for (int dj = -1; dj <= 1; dj++)
                            {
                                int a = i + di, b = j + dj;
                                if ((di != 0 || dj != 0) && a >= 0 && a < n && b >= 0 && b < m && grid[a, b] < c)
                                {
                                    isMin = false;
                                    break;
                                }
                            }
                        if (isMin)
                            minima.Add((i, j));
                    }
                minima.Sort((p, q) => grid[p.i, p.j].CompareTo(grid[q.i, q.j]));

                var result = new List<(double t, double tof, bool prograde, double cost)>();
                foreach (var (i, j) in minima.Take(RefinedCandidates))
                {
                    bool d = gridDir[i, j];
                    double x = t0 + dt * i, y = tof0 + dTof * j;
                    double f = NelderMead((a, b) => cost(a, b, d), ref x, ref y, 0.5 * dt, 0.5 * dTof);
                    if (f > grid[i, j])
                    {
                        x = t0 + dt * i;
                        y = tof0 + dTof * j;
                        f = grid[i, j];
                    }
                    result.Add((x, y, d, f));
                }
                return result.OrderBy(r => r.cost).Select(r => (r.t, r.tof, r.prograde)).ToList();
            }

            double NelderMead(Func<double, double, double> f, ref double x, ref double y, double dx, double dy)
            {
                var px = new[] { x, x + dx, x };
                var py = new[] { y, y, y + dy };
                var fv = new[] { f(px[0], py[0]), f(px[1], py[1]), f(px[2], py[2]) };
                for (int iter = 0; iter < 100; iter++)
                {
                    Ct.ThrowIfCancellationRequested();
                    // Order: 0 best, 2 worst.
                    for (int a = 0; a < 2; a++)
                        for (int b = a + 1; b < 3; b++)
                            if (fv[b] < fv[a])
                            {
                                (px[a], px[b]) = (px[b], px[a]);
                                (py[a], py[b]) = (py[b], py[a]);
                                (fv[a], fv[b]) = (fv[b], fv[a]);
                            }
                    double size = Math.Max(Math.Abs(px[2] - px[0]) / dx, Math.Abs(py[2] - py[0]) / dy);
                    if (size < 1e-3 || (!double.IsInfinity(fv[2]) && fv[2] - fv[0] < 1e-4))
                        break;
                    double cx = 0.5 * (px[0] + px[1]), cy = 0.5 * (py[0] + py[1]);
                    double rx = 2 * cx - px[2], ry = 2 * cy - py[2], fr = f(rx, ry);
                    if (fr < fv[0])
                    {
                        double ex = 3 * cx - 2 * px[2], ey = 3 * cy - 2 * py[2], fe = f(ex, ey);
                        if (fe < fr) { px[2] = ex; py[2] = ey; fv[2] = fe; }
                        else { px[2] = rx; py[2] = ry; fv[2] = fr; }
                    }
                    else if (fr < fv[1])
                    {
                        px[2] = rx; py[2] = ry; fv[2] = fr;
                    }
                    else
                    {
                        double kx = 0.5 * (cx + px[2]), ky = 0.5 * (cy + py[2]), fk = f(kx, ky);
                        if (fk < fv[2]) { px[2] = kx; py[2] = ky; fv[2] = fk; }
                        else
                            for (int a = 1; a < 3; a++)
                            {
                                px[a] = 0.5 * (px[0] + px[a]);
                                py[a] = 0.5 * (py[0] + py[a]);
                                fv[a] = f(px[a], py[a]);
                            }
                    }
                }
                int bestIndex = 0;
                for (int a = 1; a < 3; a++)
                    if (fv[a] < fv[bestIndex])
                        bestIndex = a;
                x = px[bestIndex];
                y = py[bestIndex];
                return fv[bestIndex];
            }

            void VesselAt(double ut, out Vector3d r, out Vector3d v)
            {
                if (ut == Q.UT0)
                {
                    r = Q.R0;
                    v = Q.V0;
                    return;
                }
                KeplerPropagator.Propagate(Q.R0, Q.V0, S[From].Mu, ut - Q.UT0, out r, out v);
            }

            static void Coast(ref Vector3d r, ref Vector3d v, double mu, ref double ut, double toUT)
            {
                if (toUT != ut)
                    KeplerPropagator.Propagate(r, v, mu, toUT - ut, out r, out v);
                ut = toUT;
            }

            // Lambert's direction about +z, from the orbit's angular momentum; both when near-polar.
            static bool[] Directions(Vector3d h)
            {
                var hHat = h.normalized;
                return Math.Abs(hHat.z) >= 0.5 ? new[] { hHat.z >= 0 } : new[] { true, false };
            }

            TransferPlan NewPlan() => new TransferPlan { CaseName = CaseName, Request = Q };

            TransferPlan Refuse(string reason) => new TransferPlan { Ok = false, Refusal = reason, CaseName = CaseName, Request = Q };

            Built Failed(string reason) => new Built { Plan = Refuse(reason) };

            void Report(float value)
            {
                if (value <= _progress)
                    return;
                _progress = value;
                Progress?.Report(value);
            }

            string Km(int body, double radius) => ((radius - S[body].Radius) / 1000).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

            static PlanStep Step(StepKind kind, int body, double ut, double dv, string text) =>
                new PlanStep { Kind = kind, Body = body, UT = ut, DeltaVMagnitude = dv, Explanation = text };
        }

        // ---------------------------------------------------------------- orbit helpers

        static string Inv(FormattableString text) => FormattableString.Invariant(text);

        static double Cube(double x) => x * x * x;

        static double Finite(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;

        static double AngleDeg(Vector3d a, Vector3d b) =>
            Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) * 180 / Math.PI;

        // False when the trajectory still has its periapsis ahead and it lies below 'floor'.
        static bool PeriapsisClear(Vector3d r, Vector3d v, double mu, double floor)
        {
            var el = OrbitMath.Elements(r, v, mu);
            if (!el.IsClosed && Vector3d.Dot(r, v) >= 0)
                return true; // outgoing hyperbola: the periapsis is behind
            return el.PeriapsisRadius >= floor;
        }

        // Time from the periapsis rp of a hyperbola of excess vinf to the radius R.
        static double HyperbolicTime(double rp, double vinf, double mu, double R)
        {
            if (!(R > rp))
                return 0;
            double a = mu / (vinf * vinf);
            double e = 1 + rp / a;
            double coshH = (1 + R / a) / e;
            double h = Math.Log(coshH + Math.Sqrt(coshH * coshH - 1));
            return Math.Sqrt(a * a * a / mu) * (e * Math.Sinh(h) - h);
        }

        // Time for a state to climb to radius R (0 when already there, ∞ when it never does).
        static double TimeToRadius(Vector3d r, Vector3d v, double mu, double R)
        {
            double rMag = r.magnitude;
            if (rMag >= R)
                return 0;
            var el = OrbitMath.Elements(r, v, mu);
            double e = el.Eccentricity;
            double sign = Vector3d.Dot(r, v) >= 0 ? 1 : -1;
            if (!el.IsClosed)
            {
                double a = -el.SemiMajorAxis;
                double h0 = sign * Acosh((1 + rMag / a) / e), h1 = Acosh((1 + R / a) / e);
                return Math.Sqrt(a * a * a / mu) * ((e * Math.Sinh(h1) - h1) - (e * Math.Sinh(h0) - h0));
            }
            if (el.ApoapsisRadius < R || e < 1e-9)
                return double.PositiveInfinity;
            double sma = el.SemiMajorAxis;
            double e0 = sign * Math.Acos(Clamp((1 - rMag / sma) / e)), e1 = Math.Acos(Clamp((1 - R / sma) / e));
            double dM = (e1 - e * Math.Sin(e1)) - (e0 - e * Math.Sin(e0));
            if (dM < 0)
                dM += 2 * Math.PI;
            return dM / Math.Sqrt(mu / (sma * sma * sma));
        }

        static double Acosh(double x) => x <= 1 ? 0 : Math.Log(x + Math.Sqrt(x * x - 1));

        static double Clamp(double x) => Math.Max(-1, Math.Min(1, x));
    }
}
