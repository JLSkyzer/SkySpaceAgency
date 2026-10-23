using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.OrbitPlanning;
using KSP.Sim;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        // The three shapes of transfer, each: a search on estimates, then the refined candidates built
        // from the actual state and checked in flight.
        sealed partial class Search
        {
            // Leg targets of the climb: leg i (in Up[i]) arrives at Up[i+1], at its low periapsis when
            // that level is left through it.
            Dictionary<int, Target> ClimbTargets(Leg leg)
            {
                var targets = new Dictionary<int, Target>();
                foreach (var lv in leg.Levels)
                {
                    int body = Up[lv.Level];
                    targets[lv.Level - 1] = lv.Staged
                        ? new Target(body, S[body].LowOrbitRadius, Sma[Up[lv.Level - 1]], double.NaN)
                        : new Target(body, double.NaN, double.NaN, double.NaN);
                }
                return targets;
            }

            string FlightRefusal(Flight flight) => $"The departure from {S[From].Name} cannot be flown as planned: {flight.Reason}.";

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
                return VisViva(mu, rc, (rc + rpAim) / 2) * along - vC;
            }

            TransferPlan ToAncestor()
            {
                int k = Up.Count - 1;
                double w0 = ExcessToward(Q.UT0, ArrRp).magnitude;
                double tExit = Q.UT0 + 1.2 * UpLead(k, Math.Max(1, w0));
                Report(0.2f);
                var built = BuildToAncestor(tExit);
                Report(0.6f);
                built = ResolveDeparture(built, shift => BuildToAncestor(tExit + shift * ShiftUnit()));
                return built.Plan;
            }

            // The periapsis around the target is refined by a secant on the aimed radius, each step
            // checked in flight.
            Built BuildToAncestor(double tExit)
            {
                Ct.ThrowIfCancellationRequested();
                int k = Up.Count - 1;
                var target = S[To];
                double mu = target.Mu;
                double rc = Sma[Up[k]];
                double transfer = Math.PI * Math.Sqrt(Cube((rc + ArrRp) / 2) / mu);

                double rpAim = ArrRp, prevAim = double.NaN, prevAct = double.NaN, act = double.NaN;
                Leg leg = null;
                Flight flight = null;
                for (int iter = 0; iter < 8; iter++)
                {
                    leg = Leave(k, ExcessToward(tExit, rpAim), tExit);
                    if (!leg.Ok)
                        return Failed(leg.Refusal);
                    flight = FlyClimb(leg);
                    if (!flight.Ok)
                        return FlightFailure(FlightRefusal(flight));
                    act = OrbitMath.Elements(flight.AfterR, flight.AfterV, mu).PeriapsisRadius;
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
                // Even when the secant has not converged, the flown periapsis must clear the target.
                if (!(act >= Floor(To)))
                    return FlightFailure($"The escape from {S[From].Name} would bring the {target.Name} periapsis below its atmosphere or terrain ({Km(To, act)} km).");

                double arrUT = flight.AfterUT + Finite(TimeToPeriapsis(flight.AfterR, flight.AfterV, mu), transfer);
                var plan = NewPlan();
                plan.Steps.AddRange(leg.Steps);
                plan.Steps.AddRange(flight.Steps);
                if (Q.Arrival == ArrivalKind.Orbit)
                {
                    double energy = 0.5 * flight.AfterV.sqrMagnitude - mu / flight.AfterR.magnitude;
                    double vAt = Math.Sqrt(Math.Max(0, 2 * (energy + mu / ArrRp)));
                    double vOrbit = VisViva(mu, ArrRp, (ArrRp + ArrRa) / 2);
                    plan.Steps.Add(Step(StepKind.Capture, To, arrUT, Math.Abs(vAt - vOrbit),
                        Inv($"Capture at the {target.Name} periapsis ({Km(To, ArrRp)} km) into a {Km(To, ArrRp)} × {Km(To, ArrRa)} km orbit"), k, FinalTarget()));
                }
                AddCorrections(plan.Steps, To, k, FinalTarget(), flight.TopExitUT, leg.FirstBurnUT, arrUT, arrUT, null);
                var targets = ClimbTargets(leg);
                targets[k] = FinalTarget();
                plan.ExcessBody = Up[k];
                plan.RequiredExcess = leg.TopW;
                var built = new Built { Unplanned = flight.Unplanned, UnplannedUT = flight.UnplannedUT };
                return Finish(built, plan, leg.FirstBurnUT, arrUT, targets);
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
                        plan.Steps.Add(Step(StepKind.Escape, p.Body, p.EndUT, 0, $"Leave the {S[p.Body].Name} SOI", 0));
                        exit = p.EndUT;
                    }
                double arrUT = arrival.PeriapsisUT >= arrival.StartUT ? arrival.PeriapsisUT : arrival.StartUT;
                var target = S[To];
                if (Q.Arrival == ArrivalKind.Orbit)
                {
                    double mu = target.Mu;
                    double energy = 0.5 * arrival.StartV.sqrMagnitude - mu / arrival.StartR.magnitude;
                    double vAt = Math.Sqrt(Math.Max(0, 2 * (energy + mu / ArrRp)));
                    double vOrbit = VisViva(mu, ArrRp, (ArrRp + ArrRa) / 2);
                    plan.Steps.Add(Step(StepKind.Capture, To, arrUT, Math.Abs(vAt - vOrbit),
                        Inv($"Capture at the {target.Name} periapsis ({Km(To, ArrRp)} km) into a {Km(To, ArrRp)} × {Km(To, ArrRa)} km orbit; the corrections first move the periapsis there from {Km(To, arrival.PeriapsisRadius)} km"), 0));
                }
                AddCorrections(plan.Steps, To, 0, FinalTarget(), exit, Q.UT0, arrUT, arrUT, null);
                return Finish(new Built(), plan, Q.UT0, arrUT, new Dictionary<int, Target> { [0] = FinalTarget() }).Plan;
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
                    if (t < Q.UT0 || t > Q.UT0 + window || !(tof > 0) || tof > MaxTrip)
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

                var candidates = GridRefiner.Refine(grid, gridDir, Q.UT0, window / (nT - 1), tofLo,
                    (tofHi - tofLo) / (ChildTofSamples - 1), Cost, RefinedCandidates, Ct);
                if (candidates.Count == 0)
                    return Refuse(double.IsInfinity(MaxTrip) ? $"No transfer found to {S[To].Name}." : NoWindow);
                Report(0.8f);
                var (best, cand) = Cheapest(candidates, c => BuildToDescendant(c.t, c.tof, c.prograde));
                Report(0.9f);
                best = ResolveDeparture(best, shift => BuildToDescendant(cand.t + shift * VesselPeriod, cand.tof, cand.prograde));
                return best.Plan;
            }

            // Burn cost from (rV, vV) at t to the child at t + tof, plus the estimated arrival.
            double ChildCost(Vector3d rV, Vector3d vV, double t, double tof, bool prograde)
            {
                var F = S[From];
                S.StateRelativeToParent(Down[0], t + tof, out var rC, out var vC);
                if (!Lambert.Solve(rV, rC, tof, F.Mu, prograde, 0, false, out var v1, out var v2))
                    return double.PositiveInfinity;
                if (!PeriapsisClear(rV, v1, F.Mu, Floor(From)))
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

                // Check flight: the transfer must reach the child, above the atmosphere or terrain.
                var built = new Built();
                var patches = PatchedPropagator.Propagate(S, From, rV, vV, t, t + tof, new[] { new Impulse(t, dv) }, 3);
                Patch entry = null;
                foreach (var p in patches)
                {
                    if (p.Body != From)
                        break;
                    if (p.End == PatchEnd.Impact)
                        return FlightFailure($"The transfer to {S[cT].Name} would hit {F.Name}.");
                    if (p.PeriapsisUT >= p.StartUT && p.PeriapsisUT <= p.EndUT && p.PeriapsisRadius < Floor(From))
                        return FlightFailure($"The transfer to {S[cT].Name} would pass below the atmosphere or terrain of {F.Name}.");
                    if (p.End == PatchEnd.Encounter)
                    {
                        if (p.NextBody == cT)
                            entry = p;
                        else if (!Planned.Contains(p.NextBody))
                        {
                            built.Unplanned = p.NextBody;
                            built.UnplannedUT = p.EndUT;
                        }
                        break;
                    }
                }
                if (entry == null && built.Unplanned < 0)
                    return FlightFailure($"The transfer burn does not reach {S[cT].Name}.");

                var plan = NewPlan();
                plan.Steps.Add(Burn(StepKind.Injection, From, t, dv, text, 0));
                var arr = BuildArrival(0, (v2 - vC).magnitude, t + tof, 0);
                plan.Steps.AddRange(arr.Steps);
                plan.Warnings.AddRange(arr.Warnings);
                double latest = entry != null ? entry.EndUT : t + tof;
                AddCorrections(plan.Steps, From, 0, arr.FirstTarget, t, t, Math.Min(arr.ArrivalUT, t + tof), latest, arr.Notes);
                return Finish(built, plan, t, arr.ArrivalUT, new Dictionary<int, Target> { [0] = arr.FirstTarget });
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
                double vNominal = Math.Abs(Math.Sqrt(mu / a1) - VisViva(mu, a1, (a1 + a2) / 2));
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

                var candidates = GridRefiner.Refine(grid, gridDir, t0, dt, tofLo, dTof, Cost, RefinedCandidates, Ct);
                if (candidates.Count == 0)
                    return Refuse(double.IsInfinity(MaxTrip) ? $"No transfer found to {S[To].Name}." : NoWindow);
                Report(0.8f);
                var (best, cand) = Cheapest(candidates, c => BuildPorkchop(c.t, c.tof, c.prograde));
                Report(0.9f);
                best = ResolveDeparture(best, shift =>
                    BuildPorkchop(cand.t + shift * ShiftUnit(), cand.tof - shift * ShiftUnit(), cand.prograde));
                return best.Plan;
            }

            // The cheapest built candidate; when none could be built, the first one (for its refusal).
            (Built, (double t, double tof, bool prograde)) Cheapest(List<(double t, double tof, bool prograde)> candidates,
                Func<(double t, double tof, bool prograde), Built> build)
            {
                Built best = null;
                var bestCand = candidates[0];
                foreach (var c in candidates)
                {
                    var b = build(c);
                    Trace?.Invoke(Inv($"[Transfer] candidate T+{c.t - Q.UT0:F0} s, flight {c.tof:F0} s: ") +
                        (b.Plan.Ok ? Inv($"{b.Plan.TotalDeltaV:F1} m/s") : $"refused ({b.Plan.Refusal})"));
                    if (best == null || b.Cost < best.Cost)
                    {
                        best = b;
                        bestCand = c;
                    }
                }
                return (best, bestCand);
            }

            double PorkchopCost(Vector3d rF, Vector3d vF, double t, double tof, bool prograde)
            {
                S.StateRelativeToParent(Down[0], t + tof, out var rT, out var vT);
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
                var flight = FlyClimb(leg);
                var built = new Built { Unplanned = flight.Unplanned, UnplannedUT = flight.UnplannedUT };
                // From the vessel's own body, an unplanned SOI is warned about (or avoided by a shift);
                // higher up, the climb could not be re-planned past it.
                bool crossingOnly = !flight.Ok && flight.Unplanned >= 0 && k == 0;
                if (!flight.Ok && !crossingOnly)
                    return FlightFailure(FlightRefusal(flight));
                double exit = crossingOnly ? leg.ExitUT : flight.TopExitUT;

                var arr = BuildArrival(0, wa.magnitude, t + tof, k);
                var plan = NewPlan();
                plan.Steps.AddRange(leg.Steps);
                plan.Steps.AddRange(flight.Steps);
                plan.Steps.AddRange(arr.Steps);
                plan.Warnings.AddRange(arr.Warnings);
                var arrival = S[cT];
                double entry = t + tof - HyperbolicTime(arrival.LowOrbitRadius, Math.Max(1, wa.magnitude), arrival.Mu, arrival.Soi);
                AddCorrections(plan.Steps, Ancestor, k, arr.FirstTarget, exit, leg.FirstBurnUT, t + tof, entry, arr.Notes);
                var targets = ClimbTargets(leg);
                targets[k] = arr.FirstTarget;
                plan.ExcessBody = cF;
                plan.RequiredExcess = wd;
                return Finish(built, plan, leg.FirstBurnUT, arr.ArrivalUT, targets);
            }
        }
    }
}
