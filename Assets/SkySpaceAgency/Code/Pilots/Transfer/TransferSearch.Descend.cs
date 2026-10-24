using System;
using System.Linq;
using KSP.Sim;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        // Descending into the target's branch: Down[0] (a child of A) … Down[last] (the target).
        sealed partial class Search
        {
            enum Descent { Auto, Direct, Staged }

            // Circular, coplanar estimate of arriving at Down[k] at vinf and ending at the target.
            double EstimateArrive(int k, double vinf)
            {
                if (k == Down.Count - 1)
                    return CaptureCost(vinf);
                return Math.Min(EstimateDirect(k, vinf), EstimateStaged(k, vinf));
            }

            // Straight at the moon: the arrival at the moon's orbit radius, tangent to it. The moon's
            // phasing is not in this estimate: BudgetDirect adds it on the refined candidates.
            double EstimateDirect(int k, double vinf)
            {
                var X = S[Down[k]];
                double aZ = Sma[Down[k + 1]];
                return EstimateArrive(k + 1, Oberth(X.Mu, aZ, Math.Sqrt(X.Mu / aZ), vinf));
            }

            // A capture into the low circular orbit, then the Hohmann burn from there to the moon's orbit,
            // then the moon: the plan flies exactly this (a parent → child leg from the low orbit).
            double EstimateStaged(int k, double vinf)
            {
                var X = S[Down[k]];
                double mu = X.Mu, aZ = Sma[Down[k + 1]], rp = X.LowOrbitRadius;
                if (rp >= aZ)
                    return double.PositiveInfinity;
                double vCircular = Math.Sqrt(mu / rp), aE = (rp + aZ) / 2;
                double capture = Oberth(mu, rp, vCircular, vinf);
                double injection = VisViva(mu, rp, aE) - vCircular;
                return capture + injection + EstimateArrive(k + 1, Math.Sqrt(mu / aZ) - VisViva(mu, aZ, aE));
            }

            double CaptureCost(double vinf) =>
                Q.Arrival == ArrivalKind.Orbit ? Oberth(S[To].Mu, ArrRp, VisViva(S[To].Mu, ArrRp, (ArrRp + ArrRa) / 2), vinf) : 0;

            // The arrival from Down[k] at vinf, arriving (periapsis, or centre in the zero-SOI picture) at
            // ut, on leg 'leg'. Through an intermediate body, either straight at the moon (the leg aims the
            // body's periapsis at the moon's orbit, and a new leg starts at its SOI entry, aimed at the
            // moon) or a capture into its low orbit, then a parent → child leg costed by the same search
            // from that orbit. Every leg after this one is re-planned in flight. 'mode' forces the choice
            // at this level (null when it is impossible).
            Arrival BuildArrival(int k, double vinf, double ut, int leg, Descent mode = Descent.Auto, Vector3d? excess = null)
            {
                Ct.ThrowIfCancellationRequested();
                int x = Down[k];
                var X = S[x];
                if (k == Down.Count - 1)
                {
                    var last = new Arrival { ArrivalUT = ut, FirstTarget = FinalTarget() };
                    if (Q.Arrival == ArrivalKind.Orbit)
                    {
                        last.Cost = CaptureCost(vinf);
                        last.FinalCapture = Step(StepKind.Capture, x, ut, last.Cost,
                            Inv($"Capture at the {X.Name} periapsis ({Km(x, ArrRp)} km), arriving at {vinf:F0} m/s excess, into a {Km(x, ArrRp)} × {Km(x, ArrRa)} km orbit"),
                            leg, FinalTarget());
                        last.Steps.Add(last.FinalCapture);
                    }
                    return last;
                }

                int z = Down[k + 1];
                var Z = S[z];
                double mu = X.Mu, aZ = Sma[z], vZ = Math.Sqrt(mu / aZ);
                double direct = EstimateDirect(k, vinf), staged = EstimateStaged(k, vinf);
                bool useStaged = mode == Descent.Staged || (mode == Descent.Auto && staged < direct);
                if (useStaged)
                {
                    var arr = double.IsInfinity(staged) ? null : StagedArrival(k, vinf, ut, leg, direct - staged, excess);
                    if (arr != null || mode == Descent.Staged)
                        return arr;
                }

                // Straight at the moon: this leg aims the periapsis at the moon's orbit radius.
                double entry = ut - HyperbolicTime(aZ, Math.Max(1, vinf), mu, X.Soi);
                var result = BuildArrival(k + 1, Oberth(mu, aZ, vZ, vinf), ut, leg + 1);
                string why = double.IsInfinity(staged)
                    ? $"the {X.Name} arrival is aimed straight at {Z.Name}"
                    : Inv($"the {X.Name} arrival is aimed straight at {Z.Name}: an estimated {staged - direct:F0} m/s less than a capture into a low {X.Name} orbit first");
                result.EntryCorrection = Step(StepKind.Correction, x, entry, 0,
                    $"Correction on entering the {X.Name} SOI: aims {Describe(result.FirstTarget)}; {why} ({Solved})",
                    leg + 1, result.FirstTarget);
                result.Steps.Insert(0, result.EntryCorrection);
                result.Notes.Add($"It aims the {X.Name} periapsis at the {Z.Name} orbit ({Km(x, aZ)} km): {why}");
                result.FirstTarget = new Target(x, aZ, double.NaN, double.NaN);
                result.DirectDescent = true;
                return result;
            }

            // Capture into the low circular orbit of Down[k], then the parent → child leg to the target,
            // planned from that orbit (in the moon's orbit plane) by a search of its own. Null when that
            // leg cannot be planned.
            Arrival StagedArrival(int k, double vinf, double ut, int leg, double saving, Vector3d? excess)
            {
                int x = Down[k];
                var X = S[x];
                double mu = X.Mu, rp = X.LowOrbitRadius;
                int z = Down[k + 1];
                var moon = S[z];
                // The low orbit captured into contains the arrival asymptote (a capture cannot turn the
                // plane for free): of those planes, the closest to the moon's. The leg from there pays the
                // rest of the plane change.
                Vector3d h = Vector3d.Cross(moon.EpochPosition, moon.EpochVelocity).normalized;
                if (excess.HasValue && excess.Value.magnitude > 0)
                {
                    Vector3d s = excess.Value.normalized;
                    Vector3d inPlane = h - Vector3d.Dot(h, s) * s;
                    h = inPlane.magnitude > 1e-6 ? inPlane.normalized : Vector3d.Cross(s, moon.EpochPosition).normalized;
                }
                Vector3d along = (moon.EpochPosition - Vector3d.Dot(moon.EpochPosition, h) * h).normalized;
                var sub = new TransferRequest
                {
                    FromBody = x,
                    ToBody = To,
                    R0 = rp * along,
                    V0 = Math.Sqrt(mu / rp) * Vector3d.Cross(h, along).normalized,
                    UT0 = ut,
                    Arrival = Q.Arrival,
                    ArrivalPeriapsisRadius = Q.ArrivalPeriapsisRadius,
                    ArrivalApoapsisRadius = Q.ArrivalApoapsisRadius,
                    ArrivalInclinationDeg = Q.ArrivalInclinationDeg,
                };
                var plan = new Search(S, sub, Ct, null).Run();
                if (!plan.Ok)
                    return null;

                var arr = new Arrival { ArrivalUT = plan.ArrivalUT, FirstTarget = new Target(x, rp, rp, double.NaN) };
                double capture = Oberth(mu, rp, Math.Sqrt(mu / rp), vinf);
                string why = double.IsInfinity(saving) || double.IsNaN(saving)
                    ? "no direct arrival works"
                    : saving >= 0
                        ? Inv($"an estimated {saving:F0} m/s less than arriving at {moon.Name} directly")
                        : Inv($"arriving at {moon.Name} directly would cost more once the {moon.Name} phasing is counted");
                arr.Steps.Add(Step(StepKind.Capture, x, ut, capture,
                    Inv($"Capture into a low {X.Name} orbit ({Km(x, rp)} km), arriving at {vinf:F0} m/s excess, then a leg from there to {moon.Name}: {why}"),
                    leg, arr.FirstTarget));
                arr.Cost = capture;
                foreach (var st in plan.Steps)
                {
                    st.LegIndex += leg + 1;
                    st.Explanation += " (estimated: re-planned from the orbit reached)";
                    arr.Steps.Add(st);
                    arr.Cost += st.DeltaVMagnitude;
                }
                arr.Warnings.AddRange(plan.Warnings);
                return arr;
            }

            // A direct descent into a moon, budgeted from the planned cruise: the cruise state (r, v at
            // ut, in 'frame') is flown to the intermediate SOI entry, and the correction there toward the
            // moon (its phasing) is solved, with the capture at the pass it picks. The correction step and
            // the moon's capture then carry these estimates. False when the moon cannot be met that way.
            bool BudgetDirect(Arrival arr, int frame, Vector3d r, Vector3d v, double ut, double arriveUT)
            {
                if (arr == null || !arr.DirectDescent || arr.EntryCorrection == null)
                    return false;
                int x = Down[0], z = Down[1];
                var patches = PatchedPropagator.Propagate(S, frame, r, v, ut, arriveUT + 0.1 * (arriveUT - ut) + 3600, null, 4);
                var inside = patches.FirstOrDefault(p => p.Body == x);
                if (inside == null)
                    return false;
                var step = arr.EntryCorrection;
                double captureAp = z == To && Q.Arrival == ArrivalKind.Orbit ? ArrRa : double.NaN;
                double horizon = inside.StartUT + Math.Max(3 * (arriveUT - inside.StartUT), S.OrbitalPeriod(z));
                if (!ArrivalTargeting.Solve(S, x, inside.StartR, inside.StartV, inside.StartUT, z, step.TargetPeriapsisRadius,
                        step.TargetInclinationDeg, horizon, double.NaN, captureAp,
                        out var dv, out _, out _, out double arrival, out double capture, out _))
                    return false;
                step.UT = inside.StartUT;
                step.DeltaVMagnitude = dv.magnitude;
                step.Explanation += Inv($". Estimated from the planned path: {dv.magnitude:F0} m/s for the {S[z].Name} phasing");
                if (z == To && arr.FinalCapture != null && !double.IsNaN(capture))
                {
                    arr.FinalCapture.DeltaVMagnitude = capture;
                    if (!double.IsNaN(arrival))
                    {
                        arr.FinalCapture.UT = arrival;
                        arr.ArrivalUT = arrival;
                    }
                }
                arr.Cost = arr.Steps.Sum(st => st.DeltaVMagnitude);
                return true;
            }

            // The descent of a refined candidate. With an intermediate body, both ways are built: the
            // direct one budgeted from the planned cruise, the staged one by its own search. The cheaper
            // in Δv is kept.
            Arrival ChooseArrival(Vector3d excess, double ut, int leg, bool canBudget, int frame, Vector3d r, Vector3d v, double cruiseUT)
            {
                double vinf = excess.magnitude;
                if (Down.Count < 2)
                    return BuildArrival(0, vinf, ut, leg);
                double estDirect = EstimateDirect(0, vinf), estStaged = EstimateStaged(0, vinf);
                var direct = BuildArrival(0, vinf, ut, leg, Descent.Direct);
                bool budgeted = canBudget && BudgetDirect(direct, frame, r, v, cruiseUT, ut);
                double directCost = budgeted ? direct.Cost : canBudget ? double.PositiveInfinity : direct.Cost;
                // The staged leg runs a search of its own: only when its estimate can beat the direct one.
                var staged = estStaged < directCost ? BuildArrival(0, vinf, ut, leg, Descent.Staged, excess) : null;
                double stagedCost = staged?.Cost ?? double.PositiveInfinity;
                Trace?.Invoke(Inv($"[Transfer] descent into {S[Down[1]].Name} through {S[Down[0]].Name}: estimate prefers {(estStaged < estDirect ? "staged" : "direct")} (direct {estDirect:F0}, staged {estStaged:F0} m/s); direct {(budgeted ? "budgeted" : "not budgeted")} {directCost:F0} m/s (correction {direct.EntryCorrection?.DeltaVMagnitude ?? double.NaN:F0}), staged {stagedCost:F0} m/s"));
                if (staged != null && stagedCost < directCost)
                    return staged;
                return direct;
            }
        }
    }
}
