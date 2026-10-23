using System;
using KSP.Sim;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        // Descending into the target's branch: Down[0] (a child of A) … Down[last] (the target).
        sealed partial class Search
        {
            // Circular, coplanar estimate of arriving at Down[k] at vinf and ending at the target. A
            // staged level is a capture into the low circular orbit, then a Hohmann-like leg to the moon:
            // in this ideal picture that costs the same as one capture into the low × moon ellipse.
            double EstimateArrive(int k, double vinf)
            {
                if (k == Down.Count - 1)
                    return CaptureCost(vinf);
                var X = S[Down[k]];
                double mu = X.Mu, aZ = Sma[Down[k + 1]], vZ = Math.Sqrt(mu / aZ);
                double direct = EstimateArrive(k + 1, Oberth(mu, aZ, vZ, vinf));
                double rp = X.LowOrbitRadius;
                if (rp >= aZ)
                    return direct;
                double aE = (rp + aZ) / 2;
                double staged = Oberth(mu, rp, VisViva(mu, rp, aE), vinf) + EstimateArrive(k + 1, vZ - VisViva(mu, aZ, aE));
                return Math.Min(direct, staged);
            }

            double CaptureCost(double vinf) =>
                Q.Arrival == ArrivalKind.Orbit ? Oberth(S[To].Mu, ArrRp, VisViva(S[To].Mu, ArrRp, (ArrRp + ArrRa) / 2), vinf) : 0;

            // The arrival from Down[k] at vinf, arriving (periapsis, or centre in the zero-SOI picture) at
            // ut, on leg 'leg'. Through an intermediate body, either straight at the moon (the leg aims the
            // body's periapsis at the moon's orbit, and a new leg starts at its SOI entry, aimed at the
            // moon) or a capture into its low orbit, then a parent → child leg costed by the same search
            // from that orbit. Every leg after this one is re-planned in flight.
            Arrival BuildArrival(int k, double vinf, double ut, int leg)
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
                        last.Steps.Add(Step(StepKind.Capture, x, ut, last.Cost,
                            Inv($"Capture at the {X.Name} periapsis ({Km(x, ArrRp)} km), arriving at {vinf:F0} m/s excess, into a {Km(x, ArrRp)} × {Km(x, ArrRa)} km orbit"),
                            leg, FinalTarget()));
                    }
                    return last;
                }

                int z = Down[k + 1];
                var Z = S[z];
                double mu = X.Mu, aZ = Sma[z], vZ = Math.Sqrt(mu / aZ);
                double wDirect = Oberth(mu, aZ, vZ, vinf);
                double direct = EstimateArrive(k + 1, wDirect);
                double rp = X.LowOrbitRadius;
                double staged = double.PositiveInfinity;
                if (rp < aZ)
                {
                    double aE = (rp + aZ) / 2;
                    staged = Oberth(mu, rp, VisViva(mu, rp, aE), vinf) + EstimateArrive(k + 1, vZ - VisViva(mu, aZ, aE));
                }

                if (staged < direct)
                {
                    var arr = StagedArrival(k, vinf, ut, leg, direct - staged);
                    if (arr != null)
                        return arr;
                }

                // Straight at the moon: this leg aims the periapsis at the moon's orbit radius.
                double entry = ut - HyperbolicTime(aZ, Math.Max(1, vinf), mu, X.Soi);
                var result = BuildArrival(k + 1, wDirect, ut, leg + 1);
                string why = double.IsInfinity(staged)
                    ? $"the {X.Name} arrival is aimed straight at {Z.Name}"
                    : Inv($"the {X.Name} arrival is aimed straight at {Z.Name}: saves {staged - direct:F0} m/s against a capture into a low {X.Name} orbit first");
                result.Steps.Insert(0, Step(StepKind.Correction, x, entry, 0,
                    $"Correction on entering the {X.Name} SOI: aims {Describe(result.FirstTarget)}; {why} ({Solved})",
                    leg + 1, result.FirstTarget));
                result.Notes.Add($"It aims the {X.Name} periapsis at the {Z.Name} orbit ({Km(x, aZ)} km): {why}");
                result.FirstTarget = new Target(x, aZ, double.NaN, double.NaN);
                return result;
            }

            // Capture into the low circular orbit of Down[k], then the parent → child leg to the target,
            // planned from that orbit (in the moon's orbit plane) by a search of its own. Null when that
            // leg cannot be planned.
            Arrival StagedArrival(int k, double vinf, double ut, int leg, double saving)
            {
                int x = Down[k];
                var X = S[x];
                double mu = X.Mu, rp = X.LowOrbitRadius;
                int z = Down[k + 1];
                var moon = S[z];
                Vector3d h = Vector3d.Cross(moon.EpochPosition, moon.EpochVelocity).normalized;
                Vector3d along = moon.EpochPosition.normalized;
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
                arr.Steps.Add(Step(StepKind.Capture, x, ut, capture,
                    Inv($"Capture into a low {X.Name} orbit ({Km(x, rp)} km), arriving at {vinf:F0} m/s excess, then a leg from there to {moon.Name} (Oberth at {X.Name}): saves {saving:F0} m/s against arriving at {moon.Name} directly"),
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
        }
    }
}
