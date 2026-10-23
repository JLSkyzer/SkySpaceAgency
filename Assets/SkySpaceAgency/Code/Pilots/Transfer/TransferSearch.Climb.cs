using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        // Climbing out of the vessel's branch: Up[0] (the vessel's body) … Up[k] (a child of A).
        sealed partial class Search
        {
            // Leave Up[j] with the excess w (relative to Up[j], in its parent's frame) at tExit, starting
            // from the vessel's orbit in Up[0]. Level 0 is planned from the actual state; each level above
            // only records how it is left (LevelPlan): the check flight re-plans its burn from the state
            // actually reached there, as the pilot will.
            Leg Leave(int j, Vector3d w, double tExit)
            {
                Ct.ThrowIfCancellationRequested();
                if (j == 0)
                {
                    var leg0 = DepartFromVessel(w, tExit);
                    leg0.TopW = w;
                    return leg0;
                }

                int x = Up[j], y = Up[j - 1];
                var X = S[x];
                var Y = S[y];
                double mu = X.Mu, vinf = w.magnitude;
                double aY = Sma[y], pY = S.OrbitalPeriod(y);
                double floor = Floor(x);

                // Straight through: escape Y on the X-hyperbola whose asymptote is w.
                Leg direct = null;
                {
                    double tf = HyperbolicTime(aY, Math.Max(1, vinf), mu, X.Soi);
                    double best = double.PositiveInfinity, bestT = 0;
                    Vector3d bestW = Vector3d.zero;
                    for (int i = 0; i < PhaseSamples; i++)
                    {
                        double tY = tExit - tf - pY * i / PhaseSamples;
                        S.StateRelativeToParent(y, tY, out var rY, out var vY);
                        if (!AsymptoteBurn(rY, vY, w, mu, floor, out var dv, out _))
                            continue;
                        double c = EstimateLeave(j - 1, dv.magnitude); // dv here is the excess from Y
                        if (c < best)
                        {
                            best = c;
                            bestT = tY;
                            bestW = dv;
                        }
                    }
                    if (!double.IsInfinity(best))
                    {
                        var inner = Leave(j - 1, bestW, bestT);
                        if (inner.Ok)
                            direct = inner;
                    }
                }

                // Through a low periapsis of X: escape Y retrograde into an X orbit whose periapsis is
                // X's low orbit, then make the hyperbolic burn there (Oberth).
                Leg staged = null;
                double rp = X.LowOrbitRadius;
                if (rp < aY)
                {
                    double aE = (rp + aY) / 2;
                    double half = Math.PI * Math.Sqrt(Cube(aE) / mu);
                    double va = VisViva(mu, aY, aE), vp = VisViva(mu, rp, aE);
                    double tf = HyperbolicTime(rp, Math.Max(1, vinf), mu, X.Soi);
                    double best = double.PositiveInfinity, bestT = 0, bestBurn = 0;
                    Vector3d bestW = Vector3d.zero;
                    for (int i = 0; i < PhaseSamples; i++)
                    {
                        double tY = tExit - tf - half - pY * i / PhaseSamples;
                        S.StateRelativeToParent(y, tY, out var rY, out var vY);
                        Vector3d along = Vector3d.Cross(Vector3d.Cross(rY, vY), rY).normalized;
                        Vector3d wY = va * along - vY;
                        Vector3d rPer = (-rp / rY.magnitude) * rY;
                        if (!AsymptoteBurn(rPer, (-vp) * along, w, mu, floor, out var dv, out _))
                            continue;
                        double c = dv.magnitude + EstimateLeave(j - 1, wY.magnitude);
                        if (c < best)
                        {
                            best = c;
                            bestT = tY;
                            bestW = wY;
                            bestBurn = dv.magnitude;
                        }
                    }
                    if (!double.IsInfinity(best))
                    {
                        var inner = Leave(j - 1, bestW, bestT);
                        if (inner.Ok)
                        {
                            staged = inner;
                            staged.Cost += bestBurn;
                        }
                    }
                }

                if (direct == null && staged == null)
                    return new Leg { Ok = false, Refusal = $"No way found out of {X.Name} from {Y.Name}." };
                double directCost = direct?.Cost ?? double.PositiveInfinity;
                double stagedCost = staged?.Cost ?? double.PositiveInfinity;
                bool useStaged = stagedCost < directCost;
                var chosen = useStaged ? staged : direct;
                string text;
                if (useStaged)
                    text = double.IsInfinity(directCost)
                        ? Inv($"Burn at the {X.Name} periapsis ({Km(x, rp)} km), reached by escaping {Y.Name} into a {Km(x, rp)} × {Km(x, aY)} km orbit, to leave {X.Name} at {vinf:F0} m/s excess (Oberth): no direct escape from {Y.Name} reaches it")
                        : Inv($"Burn at the {X.Name} periapsis ({Km(x, rp)} km), reached by escaping {Y.Name} into a {Km(x, rp)} × {Km(x, aY)} km orbit, to leave {X.Name} at {vinf:F0} m/s excess (Oberth): saves {directCost - stagedCost:F0} m/s against escaping {Y.Name} directly");
                else
                    text = double.IsInfinity(stagedCost)
                        ? Inv($"Re-aim the {X.Name} exit at {vinf:F0} m/s excess, straight from the {Y.Name} escape")
                        : Inv($"Re-aim the {X.Name} exit at {vinf:F0} m/s excess, straight from the {Y.Name} escape: saves {stagedCost - directCost:F0} m/s against a burn at a low {X.Name} periapsis");
                chosen.Levels.Add(new LevelPlan { Level = j, W = w, Staged = useStaged, Explanation = text });
                chosen.TopW = w;
                return chosen;
            }

            // The departure burn from the vessel's own orbit, from the actual state. From a high orbit it
            // also tries lowering the periapsis first, then burning there (Oberth), and keeps the cheaper.
            Leg DepartFromVessel(Vector3d w, double tExit)
            {
                var F = S[From];
                double mu = F.Mu, vinf = w.magnitude;
                double tf = HyperbolicTime(VesselEl.PeriapsisRadius, Math.Max(1, vinf), mu, F.Soi);
                // The planner searches one orbit from ut0: centre it on the burn the exit time asks for.
                double ut0 = Math.Max(Q.UT0, tExit - tf - VesselPeriod / 2);
                VesselAt(ut0, out var r, out var v);
                var direct = PlanFrom(r, v, ut0, w);

                if (VesselEl.PeriapsisRadius > 2 * F.LowOrbitRadius)
                {
                    var lowered = Lowered(w, tExit, out var lowering, out double loweringUT);
                    if (lowered != null && lowered.Ok && (!direct.Ok || lowered.Cost + 1 <= direct.Cost))
                    {
                        double low = F.LowOrbitRadius;
                        string text = direct.Ok
                            ? Inv($"Orbit change first: lower the periapsis to {Km(From, low)} km ({lowering.magnitude:F0} m/s), then make the departure burn there (Oberth): {lowered.Cost:F0} m/s in all against {direct.Cost:F0} m/s from your orbit as it is, which saves {direct.Cost - lowered.Cost:F0} m/s")
                            : Inv($"Orbit change first: lower the periapsis to {Km(From, low)} km ({lowering.magnitude:F0} m/s), then make the departure burn there (Oberth): no departure from your orbit as it is works");
                        lowered.Steps.Insert(0, Burn(StepKind.OrbitChange, From, loweringUT, lowering, text, 0));
                        return lowered;
                    }
                }
                return direct;
            }

            // Lower the periapsis to the low orbit at the best point of one orbit, then plan the departure
            // from the lowered orbit. Null when nothing works.
            Leg Lowered(Vector3d w, double tExit, out Vector3d lowering, out double loweringUT)
            {
                lowering = Vector3d.zero;
                loweringUT = double.NaN;
                var F = S[From];
                double mu = F.Mu, vinf = w.magnitude, low = F.LowOrbitRadius;
                double aE = (VesselEl.SemiMajorAxis + low) / 2;
                double lead = HyperbolicTime(low, Math.Max(1, vinf), mu, F.Soi) + Math.PI * Math.Sqrt(Cube(aE) / mu);
                double latest = Math.Max(Q.UT0 + VesselPeriod, tExit - lead);

                double best = double.PositiveInfinity;
                for (int i = 0; i < PhaseSamples; i++)
                {
                    double tb = latest - VesselPeriod * i / PhaseSamples;
                    VesselAt(tb, out var rb, out var vb);
                    double rbm = rb.magnitude;
                    Vector3d along = Vector3d.Cross(Vector3d.Cross(rb, vb), rb).normalized;
                    double sma = (rbm + low) / 2;
                    Vector3d dvL = VisViva(mu, rbm, sma) * along - vb;
                    Vector3d rP = (-low / rbm) * rb;
                    if (!AsymptoteBurn(rP, (-VisViva(mu, low, sma)) * along, w, mu, Floor(From), out var dvP, out _))
                        continue;
                    double c = dvL.magnitude + dvP.magnitude;
                    if (c < best)
                    {
                        best = c;
                        lowering = dvL;
                        loweringUT = tb;
                    }
                }
                if (double.IsInfinity(best))
                    return null;
                VesselAt(loweringUT, out var r, out var v);
                var leg = PlanFrom(r, v + lowering, loweringUT, w);
                if (!leg.Ok)
                    return null;
                leg.Impulses.Insert(0, new Impulse(loweringUT, lowering));
                leg.Cost += lowering.magnitude;
                leg.FirstBurnUT = loweringUT;
                return leg;
            }

            // DeparturePlanner from (r, v) at ut0, as leg-0 steps.
            Leg PlanFrom(Vector3d r, Vector3d v, double ut0, Vector3d w)
            {
                var F = S[From];
                double mu = F.Mu, vinf = w.magnitude;
                var leg = new Leg();
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
                    leg.Steps.Add(Burn(StepKind.PlaneChange, From, dp.PlaneChangeUT, dp.PlaneChangeDeltaV, dp.Explanation, 0));
                    leg.Impulses.Add(new Impulse(dp.PlaneChangeUT, dp.PlaneChangeDeltaV));
                }
                Coast(ref r, ref v, mu, ref ut, dp.BurnUT);
                v += dp.DeltaV;
                if (!PeriapsisClear(r, v, mu, Floor(From)))
                {
                    leg.Ok = false;
                    leg.Refusal = $"The departure would pass below the atmosphere or terrain of {F.Name}.";
                    return leg;
                }

                string next = S[F.ParentIndex].Name;
                string text = Inv($"Departure burn: leave {F.Name} at {vinf:F0} m/s excess, into the {next} SOI");
                if (!dp.PlaneChangeFirst && dp.OutOfPlaneDeg > PlaneTurnExplainedDeg)
                    text += Inv($". Departure burn also turns the plane by {dp.OutOfPlaneDeg:F0}° (cheaper than a separate plane change)");
                leg.Steps.Add(Burn(StepKind.Injection, From, dp.BurnUT, dp.DeltaV, text, 0));
                leg.Impulses.Add(new Impulse(dp.BurnUT, dp.DeltaV));
                leg.Cost = dp.TotalDeltaV;
                leg.FirstBurnUT = dp.PlaneChangeFirst ? dp.PlaneChangeUT : dp.BurnUT;
                leg.ExitUT = dp.BurnUT + Finite(TimeToRadius(r, v, mu, F.Soi), HyperbolicTime(VesselEl.PeriapsisRadius, Math.Max(1, vinf), mu, F.Soi));
                leg.Steps.Add(Step(StepKind.Escape, From, leg.ExitUT, 0, $"Leave the {F.Name} SOI", 0));
                return leg;
            }

            // The check flight: leg 0 as planned, then each level above re-planned from the state reached
            // on entering it (a periapsis fix and the burn at the flown periapsis when staged, a re-aim
            // burn otherwise). Each level must stay above its atmosphere or terrain and leave its SOI; the
            // top level must leave with the required excess, within 5 %.
            Flight FlyClimb(Leg leg)
            {
                var f = new Flight();
                int k = Up.Count - 1;
                double start = leg.Impulses.Min(i => i.UT);
                VesselAt(start, out var r0, out var v0);
                double margin = 60 + 0.1 * (leg.ExitUT - start);
                var patches = PatchedPropagator.Propagate(S, From, r0, v0, start, leg.ExitUT + margin, leg.Impulses, 8);
                if (!CheckLevel(f, patches, From, k == 0 ? leg.TopW : (Vector3d?)null, out var next, out double exit))
                    return f;
                f.TopExitUT = exit;
                leg.ExitUT = exit; // the flown SOI exit
                var escape0 = leg.Steps.LastOrDefault(st => st.Kind == StepKind.Escape);
                if (escape0 != null)
                    escape0.UT = exit;

                for (int j = 1; j <= k; j++)
                {
                    Ct.ThrowIfCancellationRequested();
                    var lv = leg.Levels[j - 1];
                    int x = Up[j];
                    var X = S[x];
                    double mu = X.Mu, floor = Floor(x);
                    Vector3d r = next.StartR, v = next.StartV;
                    double ut = next.StartUT;
                    var impulses = new List<Impulse>();
                    double exitEstimate;
                    if (lv.Staged)
                    {
                        // The periapsis fix, then the departure burn planned on the flown orbit, as from
                        // a parking orbit: the flown periapsis rarely points where the estimate assumed.
                        Vector3d fix = PeriapsisFix(r, v, mu, X.LowOrbitRadius);
                        Vector3d v1 = v + fix;
                        var el = OrbitMath.Elements(r, v1, mu);
                        if (!el.IsClosed || !(el.ApoapsisRadius < X.Soi))
                            return Fail(f, $"the {Y(j)} escape does not leave the vessel in orbit of {X.Name}");
                        var dp = DeparturePlanner.Plan(r, v1, ut, mu, X.Soi, lv.W);
                        if (!dp.Ok)
                            return Fail(f, $"no burn in orbit of {X.Name} reaches the required excess ({dp.Refusal})");
                        if (fix.magnitude > 0.01)
                        {
                            f.Steps.Add(Burn(StepKind.Correction, x, ut, fix,
                                $"Set the {X.Name} periapsis to {Km(x, X.LowOrbitRadius)} km for the burn there (re-planned on entering the {X.Name} SOI)", j));
                            impulses.Add(new Impulse(ut, fix));
                        }
                        if (dp.PlaneChangeFirst)
                        {
                            f.Steps.Add(Burn(StepKind.PlaneChange, x, dp.PlaneChangeUT, dp.PlaneChangeDeltaV, dp.Explanation, j));
                            impulses.Add(new Impulse(dp.PlaneChangeUT, dp.PlaneChangeDeltaV));
                        }
                        f.Steps.Add(Burn(StepKind.Injection, x, dp.BurnUT, dp.DeltaV, lv.Explanation, j));
                        impulses.Add(new Impulse(dp.BurnUT, dp.DeltaV));
                        double tb = ut;
                        Vector3d rb = r, vb = v1;
                        if (dp.PlaneChangeFirst)
                        {
                            Coast(ref rb, ref vb, mu, ref tb, dp.PlaneChangeUT);
                            vb += dp.PlaneChangeDeltaV;
                        }
                        Coast(ref rb, ref vb, mu, ref tb, dp.BurnUT);
                        vb += dp.DeltaV;
                        exitEstimate = dp.BurnUT + Finite(TimeToRadius(rb, vb, mu, X.Soi), 0);
                    }
                    else
                    {
                        if (!AsymptoteBurn(r, v, lv.W, mu, floor, out var dv, out var vH))
                            return Fail(f, $"no burn on entering {X.Name} reaches the required excess");
                        f.Steps.Add(Burn(StepKind.Correction, x, ut, dv, lv.Explanation + $" (re-planned on entering the {X.Name} SOI)", j));
                        impulses.Add(new Impulse(ut, dv));
                        exitEstimate = ut + Finite(TimeToRadius(r, vH, mu, X.Soi), 0);
                    }
                    f.Cost += impulses.Sum(i => i.DeltaV.magnitude);
                    patches = PatchedPropagator.Propagate(S, x, r, v, ut, exitEstimate + 60 + 0.1 * (exitEstimate - ut), impulses, 8);
                    if (!CheckLevel(f, patches, x, j == k ? lv.W : (Vector3d?)null, out next, out exit))
                        return f;
                    f.Steps.Add(Step(StepKind.Escape, x, exit, 0, $"Leave the {X.Name} SOI", j));
                    f.TopExitUT = exit;
                }
                f.AfterR = next.StartR;
                f.AfterV = next.StartV;
                f.AfterUT = next.StartUT;
                return f;
            }

            string Y(int j) => S[Up[j - 1]].Name;

            // The patches flown in 'body': no impact, no periapsis below the floor, an escape, and, when
            // 'w' is given, the excess it leaves with within 5 % of w. 'next' is the patch after the escape.
            bool CheckLevel(Flight f, List<Patch> patches, int body, Vector3d? w, out Patch next, out double exitUT)
            {
                next = null;
                exitUT = double.NaN;
                string name = S[body].Name;
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    if (p.Body != body)
                        continue;
                    if (p.End == PatchEnd.Impact)
                        return FailCheck(f, $"the path hits {name}");
                    if (p.PeriapsisUT >= p.StartUT && p.PeriapsisUT <= p.EndUT && p.PeriapsisRadius < Floor(body))
                        return FailCheck(f, $"the path passes below the atmosphere or terrain of {name}");
                    if (p.End == PatchEnd.Encounter && !Planned.Contains(p.NextBody))
                    {
                        f.Unplanned = p.NextBody;
                        f.UnplannedUT = p.EndUT;
                        return FailCheck(f, $"the path enters the {S[p.NextBody].Name} SOI");
                    }
                    if (p.End != PatchEnd.Escape)
                        continue;
                    if (w.HasValue)
                    {
                        Vector3d a = Hyperbola.Asymptote(p.EndR, p.EndV, S[body].Mu);
                        Vector3d want = w.Value;
                        if ((a - want).magnitude > ExcessTolerance * want.magnitude)
                            return FailCheck(f, Inv($"it leaves {name} at {a.magnitude:F0} m/s, {AngleDeg(a, want):F1}° off, instead of {want.magnitude:F0} m/s"));
                    }
                    if (i + 1 >= patches.Count)
                        return FailCheck(f, $"the path does not leave {name} in time");
                    next = patches[i + 1];
                    exitUT = p.EndUT;
                    return true;
                }
                return FailCheck(f, $"the path does not leave {name}");
            }

            static bool FailCheck(Flight f, string reason)
            {
                f.Ok = false;
                f.Reason = reason;
                return false;
            }

            static Flight Fail(Flight f, string reason)
            {
                FailCheck(f, reason);
                return f;
            }

            // The burn along the local horizontal that puts the periapsis at 'target' (zero when none).
            static Vector3d PeriapsisFix(Vector3d r, Vector3d v, double mu, double target)
            {
                Vector3d rHat = r.normalized;
                double vr = Vector3d.Dot(v, rHat);
                Vector3d horizontal = v - vr * rHat;
                double vt = horizontal.magnitude;
                if (vt < 1e-6)
                    return Vector3d.zero;
                Vector3d tHat = horizontal / vt;
                double Periapsis(double speed) => OrbitMath.Elements(r, vr * rHat + speed * tHat, mu).PeriapsisRadius;
                double lo = 0, hi = vt;
                for (int i = 0; i < 40 && Periapsis(hi) < target; i++)
                    hi *= 2;
                if (Periapsis(hi) < target)
                    return Vector3d.zero;
                for (int i = 0; i < 80; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (Periapsis(mid) < target) lo = mid; else hi = mid;
                }
                return (0.5 * (lo + hi) - vt) * tHat;
            }

            // Circular, coplanar estimate of leaving Up[j] at vinf from the vessel's orbit (the grids).
            double EstimateLeave(int j, double vinf)
            {
                if (j == 0)
                {
                    double rp0 = VesselEl.PeriapsisRadius;
                    return Oberth(S[From].Mu, rp0, VisViva(S[From].Mu, rp0, VesselEl.SemiMajorAxis), vinf);
                }
                var X = S[Up[j]];
                double mu = X.Mu, aY = Sma[Up[j - 1]], vY = Math.Sqrt(mu / aY);
                double direct = EstimateLeave(j - 1, Oberth(mu, aY, vY, vinf));
                double rp = X.LowOrbitRadius;
                if (rp >= aY)
                    return direct;
                double aE = (rp + aY) / 2;
                double staged = Oberth(mu, rp, VisViva(mu, rp, aE), vinf) + EstimateLeave(j - 1, vY - VisViva(mu, aY, aE));
                return Math.Min(direct, staged);
            }

            // A generous bound on the time from now to the SOI exit of Up[j]: waiting for the right
            // phase at each level (one orbit), then the flights. Without the waits: from the first burn.
            double UpLead(int j, double vinf, bool waits = true)
            {
                vinf = Math.Max(1, vinf);
                if (j == 0)
                {
                    double lead = HyperbolicTime(VesselEl.PeriapsisRadius, vinf, S[From].Mu, S[From].Soi) + (waits ? VesselPeriod : 0);
                    if (VesselEl.PeriapsisRadius > 2 * S[From].LowOrbitRadius) // room to lower the periapsis first
                        lead += VesselPeriod;
                    return lead;
                }
                var X = S[Up[j]];
                double mu = X.Mu, aY = Sma[Up[j - 1]], vY = Math.Sqrt(mu / aY);
                double wDirect = Oberth(mu, aY, vY, vinf), wStaged = wDirect;
                double flight = HyperbolicTime(aY, vinf, mu, X.Soi);
                double rp = X.LowOrbitRadius;
                if (rp < aY)
                {
                    double aE = (rp + aY) / 2;
                    flight = Math.Max(flight, Math.PI * Math.Sqrt(Cube(aE) / mu) + HyperbolicTime(rp, vinf, mu, X.Soi));
                    wStaged = vY - VisViva(mu, aY, aE);
                }
                return flight + (waits ? S.OrbitalPeriod(Up[j - 1]) : 0) + UpLead(j - 1, Math.Min(wDirect, wStaged), waits);
            }
        }
    }
}
