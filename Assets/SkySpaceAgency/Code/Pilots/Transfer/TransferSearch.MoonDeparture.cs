using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        public static string NoMoon(string body) => $"No moon to fly by around {body}.";

        // A plan whose departure flies by 'moon', a moon of the vessel's body (4.6): the direct
        // porkchop's two best candidates, each built with the departure through the moon; the cheaper.
        public static TransferPlan FindMoonDeparture(BodySystem s, TransferRequest q, int moon, CancellationToken ct, IProgress<float> progress = null)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (q == null)
                throw new ArgumentNullException(nameof(q));
            ct.ThrowIfCancellationRequested();
            if (q.FromBody < 0 || q.FromBody >= s.Count)
                throw new ArgumentException("Unknown body index.");
            bool hasMoon = false;
            for (int b = 0; b < s.Count; b++)
                if (s[b].ParentIndex == q.FromBody)
                    hasMoon = true;
            var search = new Search(s, q, ct, progress);
            TransferPlan plan;
            if (!hasMoon)
                plan = search.RefuseWith(NoMoon(s[q.FromBody].Name));
            else
                plan = search.AssistSetup(moon, AssistKind.Departure) ?? search.MoonDeparture(moon);
            plan.Request = q;
            return plan;
        }

        sealed partial class Search
        {
            const int MoonFlybySamples = 48, MoonTofSamples = 16, MoonDepartureCandidates = 2;

            public TransferPlan RefuseWith(string reason) => Refuse(reason);

            public TransferPlan MoonDeparture(int moon)
            {
                if (Up.Count != 1)
                    return Refuse(NoAssist($"a departure flyby needs the vessel around a child of {S[Ancestor].Name}"));
                var candidates = PorkchopCandidates(out var refusal);
                if (candidates == null)
                    return refusal;
                Built best = null;
                foreach (var c in candidates.Take(MoonDepartureCandidates))
                {
                    var b = BuildPorkchop(c.t, c.tof, c.prograde, moon);
                    Trace?.Invoke(Inv($"[Transfer] departure via {S[moon].Name}, T+{c.t - Q.UT0:F0} s: ") +
                        (b.Plan.Ok ? Inv($"{b.Plan.TotalDeltaV:F1} m/s") : $"refused ({b.Plan.Refusal})"));
                    if (best == null || b.Cost < best.Cost)
                        best = b;
                }
                return best.Plan;
            }

            // The moon's grid point: the flyby date, the injection's flight, its direction, the
            // branch of the outgoing velocity.
            struct MoonPoint
            {
                public double TF, Tof, Cost;
                public bool Dir;
                public int Branch;
            }

            // The cost of injecting from the vessel's orbit at tF - tof toward the moon at tF, then the
            // flyby that leaves the moon with the outgoing velocity of 'branch' (0, 1: the two whose
            // asymptote around the vessel's body is w).
            double MoonCost(int moon, Vector3d w, double tF, double tof, bool dir, int branch, out FlybySolution fly,
                out Vector3d inject, out Vector3d vOutBody)
            {
                fly = default;
                inject = Vector3d.zero;
                vOutBody = Vector3d.zero;
                var F = S[From];
                var M = S[moon];
                double t0 = tF - tof;
                if (!(tof > 0) || t0 < Q.UT0)
                    return double.PositiveInfinity;
                S.StateRelativeToParent(moon, tF, out var rM, out var vM);
                if (!Hyperbola.VelocityForAsymptote(rM, w, F.Mu, out var vA, out var vB))
                    return double.PositiveInfinity;
                vOutBody = branch == 0 ? vA : vB;
                if (!PeriapsisClear(rM, vOutBody, F.Mu, Floor(From)))
                    return double.PositiveInfinity;
                VesselAt(t0, out var rV, out var vV);
                if (!Lambert.Solve(rV, rM, tof, F.Mu, dir, 0, false, out var v1, out var v2))
                    return double.PositiveInfinity;
                if (!PeriapsisClear(rV, v1, F.Mu, Floor(From)))
                    return double.PositiveInfinity;
                fly = FlybyModel.Solve(v2 - vM, vOutBody - vM, M.Mu, M.LowOrbitRadius, FlybyModel.MaxPeriapsisShare * M.Soi);
                if (!fly.Ok)
                    return double.PositiveInfinity;
                inject = v1 - vV;
                return inject.magnitude + fly.DeltaV;
            }

            // Leave the vessel's body with the excess w at tExit through a flyby of 'moon' (4.6), as the
            // replacement for Leave(0, w, tExit): an injection toward the moon (leg 0, with the three
            // corrections aimed at its periapsis and plane), the flyby (leg 1: a burn at its
            // periapsis when it needs one, the moon's SOI exit), then a re-aim burn toward w (leg 2) and
            // the SOI exit. Not Ok when no point of one moon period clears the floors.
            Leg LeaveViaMoon(int moon, Vector3d w, double tExit)
            {
                Ct.ThrowIfCancellationRequested();
                var F = S[From];
                var M = S[moon];
                double mu = F.Mu, aM = Sma[moon], pM = S.OrbitalPeriod(moon);
                double rV0 = VesselEl.SemiMajorAxis;
                double hohmann = Math.PI * Math.Sqrt(Cube((rV0 + aM) / 2) / mu);
                double centre = tExit - HyperbolicTime(aM, Math.Max(1, w.magnitude), mu, F.Soi);
                var dirs = Directions(VesselEl.AngularMomentum);

                var best = new MoonPoint { Cost = double.PositiveInfinity };
                for (int i = 0; i < MoonFlybySamples; i++)
                {
                    double tF = centre - pM / 2 + pM * i / MoonFlybySamples;
                    for (int j = 0; j < MoonTofSamples; j++)
                    {
                        double tof = hohmann * (0.5 + 1.0 * j / (MoonTofSamples - 1));
                        foreach (bool d in dirs)
                            for (int b = 0; b < 2; b++)
                            {
                                double c = MoonCost(moon, w, tF, tof, d, b, out _, out _, out _);
                                if (c < best.Cost)
                                    best = new MoonPoint { TF = tF, Tof = tof, Cost = c, Dir = d, Branch = b };
                            }
                    }
                }
                if (double.IsInfinity(best.Cost))
                    return new Leg { Ok = false, Refusal = $"No flyby of {M.Name} on the way out of {F.Name} clears the atmospheres or terrains." };
                {
                    double x = best.TF, y = best.Tof;
                    var p = best;
                    double f = GridRefiner.NelderMead((a, b) => MoonCost(moon, w, a, b, p.Dir, p.Branch, out _, out _, out _),
                        ref x, ref y, 0.5 * pM / MoonFlybySamples, 0.5 * hohmann / (MoonTofSamples - 1), Ct);
                    if (f < best.Cost)
                    {
                        best.TF = x;
                        best.Tof = y;
                        best.Cost = f;
                    }
                }
                MoonCost(moon, w, best.TF, best.Tof, best.Dir, best.Branch, out var fly, out var inject, out _);
                double t0 = best.TF - best.Tof;

                // The injection aimed at the moon's periapsis and plane (its own frame).
                var target = new Target(moon, Math.Max(fly.PeriapsisRadius, M.LowOrbitRadius), double.NaN, double.NaN,
                    CelestialFrames.ToLocal(M.FrameToCommon, fly.Normal));
                VesselAt(t0, out var r0, out var v0);
                double horizon = best.TF + 0.5 * best.Tof;
                for (int iter = 0; iter < 3; iter++)
                {
                    if (!ArrivalTargeting.Solve(S, From, r0, v0 + inject, t0, moon, target.Pe, double.NaN, target.Plane, horizon,
                            best.TF, double.NaN, ArrivalTargeting.MaxDeltaV, out var dvAim, out _, out _, out _, out _, out _))
                        break;
                    inject += dvAim;
                    if (dvAim.magnitude < 1e-3)
                        break;
                }

                // The flyby as the injection meets it.
                var path = PatchedPropagator.Propagate(S, From, r0, v0 + inject, t0, best.TF + best.Tof, null, 3);
                var inside = path.FirstOrDefault(pa => pa.Body == moon);
                if (inside == null)
                    return new Leg { Ok = false, Refusal = $"The injection toward {M.Name} does not reach it." };
                double tPe = inside.PeriapsisUT >= inside.StartUT ? inside.PeriapsisUT : inside.StartUT;
                KeplerPropagator.Propagate(inside.StartR, inside.StartV, M.Mu, tPe - inside.StartUT, out var rPe, out var vPe);
                if (rPe.magnitude < Floor(moon))
                    return new Leg { Ok = false, Refusal = $"The flyby of {M.Name} would pass below its terrain ({Km(moon, rPe.magnitude)} km)." };
                // The excess wanted out of the moon at the periapsis: the velocity around the body whose
                // asymptote is w, from the moon's position then (the branch of the grid).
                S.StateRelativeToParent(moon, tPe, out var rMp, out var vMp);
                if (!Hyperbola.VelocityForAsymptote(rMp, w, mu, out var oA, out var oB))
                    return new Leg { Ok = false, Refusal = $"No path out of {F.Name} through {M.Name}." };
                Vector3d wantedMoon = (best.Branch == 0 ? oA : oB) - vMp;
                if (!FlyFlyby(moon, rPe, vPe, tPe, wantedMoon, out var dvFly, out var moonExit, out _, out string why))
                    return new Leg { Ok = false, Refusal = $"The flyby of {M.Name} cannot be flown: {why}." };

                // The re-aim burn at the moon's SOI exit toward w, never exactly zero (a planned vector).
                double tRe = moonExit.StartUT;
                if (!AsymptoteBurn(moonExit.StartR, moonExit.StartV, w, mu, Floor(From), out var dvRe, out var vH))
                    return new Leg { Ok = false, Refusal = $"No burn after the {M.Name} flyby leaves {F.Name} at the excess wanted." };
                if (dvRe.sqrMagnitude == 0)
                    dvRe = 1e-6 * vH.normalized;
                double exitUT = tRe + Finite(TimeToRadius(moonExit.StartR, vH, mu, F.Soi), HyperbolicTime(aM, Math.Max(1, w.magnitude), mu, F.Soi));

                var leg = new Leg();
                var directLeg = DepartFromVessel(w, tExit);
                double direct = directLeg.Ok ? directLeg.Cost : double.NaN;
                double cost = inject.magnitude + dvFly.magnitude + dvRe.magnitude;
                leg.Steps.Add(Burn(StepKind.Injection, From, t0, inject,
                    Inv($"Injection toward {M.Name} ({best.Tof / 3600:F1} h of flight), for a flyby on the way out of {F.Name}"), 0));
                AddCorrections(leg.Steps, From, 0, target, t0, t0, tPe, inside.StartUT, null);
                foreach (var st in leg.Steps.Where(st => st.Kind == StepKind.Correction))
                    st.Explanation = st.Explanation.Replace($"Correction after leaving {F.Name}", "Correction after the injection");
                string saving = double.IsNaN(direct) ? "no direct departure to compare with"
                    : direct >= cost ? Inv($"{direct - cost:F0} m/s less than a direct departure")
                    : Inv($"{cost - direct:F0} m/s more than a direct departure");
                string power = dvFly.sqrMagnitude > 0 ? Inv($", powered: {dvFly.magnitude:F0} m/s") : ", no burn";
                var flyStep = Burn(StepKind.Flyby, moon, tPe, dvFly,
                    Inv($"Flyby of {M.Name} at {Km(moon, rPe.magnitude)} km on the way out of {F.Name}: {saving}{power}"), 1);
                flyStep.TargetExcess = wantedMoon;
                leg.Steps.Add(flyStep);
                leg.Steps.Add(Step(StepKind.Escape, moon, moonExit.StartUT, 0, $"Leave the {M.Name} SOI", 1));
                leg.Steps.Add(Burn(StepKind.Correction, From, tRe, dvRe,
                    Inv($"Re-aim after the {M.Name} flyby: leave {F.Name} at {w.magnitude:F0} m/s excess"), 2));
                leg.Steps.Add(Step(StepKind.Escape, From, exitUT, 0, $"Leave the {F.Name} SOI", 2));
                leg.Impulses.Add(new Impulse(t0, inject));
                if (dvFly.sqrMagnitude > 0)
                    leg.Impulses.Add(new Impulse(tPe, dvFly));
                leg.Impulses.Add(new Impulse(tRe, dvRe));
                leg.Cost = cost;
                leg.FirstBurnUT = t0;
                leg.ExitUT = exitUT;
                leg.TopW = w;
                Trace?.Invoke(Inv($"[Transfer] departure via {M.Name}: injection {inject.magnitude:F1}, flyby {dvFly.magnitude:F1} at {Km(moon, rPe.magnitude)} km, re-aim {dvRe.magnitude:F1} m/s; direct {direct:F1} m/s"));
                return leg;
            }
        }
    }
}
