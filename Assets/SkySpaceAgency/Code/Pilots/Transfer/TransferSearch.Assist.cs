using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SkySpaceAgency.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace SkySpaceAgency.Transfer
{
    public static partial class TransferSearch
    {
        public static string NoAssist(string why) => $"No gravity assist: {why}.";

        // The assist grid's legs for a flyby of 'flyby', a child of the common ancestor (4.6). Null,
        // with the refusal, when the request has no sibling flyby.
        public static AssistLegs SiblingAssistLegs(BodySystem s, TransferRequest q, int flyby, out TransferPlan refusal)
        {
            var search = new Search(s, q, CancellationToken.None, null);
            refusal = search.AssistSetup(flyby, AssistKind.Sibling);
            return refusal == null ? search.SiblingLegs(flyby) : null;
        }

        // A sibling flyby plan built from the actual state around a grid candidate (4.6).
        public static TransferPlan FindSiblingAssist(BodySystem s, TransferRequest q, int flyby, AssistCandidate c, CancellationToken ct)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (q == null)
                throw new ArgumentNullException(nameof(q));
            ct.ThrowIfCancellationRequested();
            var search = new Search(s, q, ct, null);
            var plan = search.AssistSetup(flyby, AssistKind.Sibling) ?? search.SiblingAssist(flyby, c);
            plan.Request = q;
            return plan;
        }

        sealed partial class Search
        {
            // The request's set-up, then the assist's own rules. Null when the flyby is possible.
            public TransferPlan AssistSetup(int flyby, AssistKind kind)
            {
                if (flyby < 0 || flyby >= S.Count)
                    return Refuse(NoAssist("unknown flyby body"));
                var early = Setup();
                if (early != null)
                    return early.Ok ? Refuse(NoAssist("the vessel is not in a closed orbit of another branch")) : early;
                if (CaseName != Siblings && CaseName != DifferentBranches)
                    return Refuse(NoAssist($"the case is {CaseName}"));
                if (kind == AssistKind.Sibling)
                {
                    if (S[flyby].ParentIndex != Ancestor || flyby == Down[0])
                        return Refuse(NoAssist($"{S[flyby].Name} is not a body on the way around {S[Ancestor].Name}"));
                }
                else if (S[flyby].ParentIndex != From)
                    return Refuse(NoAssist($"{S[flyby].Name} is not a moon of {S[From].Name}"));
                Planned.Add(flyby);
                return null;
            }

            static double Hohmann(double a1, double a2, double mu) => Math.PI * Math.Sqrt(Cube((a1 + a2) / 2) / mu);

            (Vector3d r, Vector3d v) StateOf(int body, double ut)
            {
                S.StateRelativeToParent(body, ut, out var r, out var v);
                return (r, v);
            }

            // The departure window and the trip cap of the direct porkchop, the flights from 0.3 to 1.5
            // Hohmann of each leg (around the departure body: 0.6 to 2.2 of its period, with the
            // 1-revolution solutions), the estimates of the climb and the descent as end costs.
            public AssistLegs SiblingLegs(int flyby)
            {
                int k = Up.Count - 1, cF = Up[k], cT = Down[0];
                double mu = S[Ancestor].Mu;
                var B = S[flyby];
                double aF = Sma[cF], aB = Sma[flyby], aT = Sma[cT];
                double tof1Lo, tof1Hi;
                int[] revs;
                if (flyby == cF)
                {
                    double p = S.OrbitalPeriod(cF);
                    tof1Lo = 0.6 * p;
                    tof1Hi = 2.2 * p;
                    revs = new[] { 0, 1 };
                }
                else
                {
                    double h1 = Hohmann(aF, aB, mu);
                    tof1Lo = 0.3 * h1;
                    tof1Hi = 1.5 * h1;
                    revs = new[] { 0 };
                }
                double h2 = Hohmann(aB, aT, mu);
                double synodic = S.SynodicPeriod(cF, cT);
                if (double.IsInfinity(synodic))
                    synodic = Math.Max(S.OrbitalPeriod(cF), S.OrbitalPeriod(cT));
                double vNominal = Math.Abs(Math.Sqrt(mu / aF) - VisViva(mu, aF, (aF + aB) / 2));
                double depLo = Q.UT0 + UpLead(k, Math.Max(1, 0.5 * vNominal));
                double depHi = Math.Max(Q.UT0 + 2 * synodic, depLo + synodic);
                double climb = UpLead(k, Math.Max(1, vNominal), false);
                var dirs = Directions(Vector3d.Cross(S[cF].EpochPosition, S[cF].EpochVelocity));
                return new AssistLegs
                {
                    DepartureState = t => StateOf(cF, t),
                    FlybyState = t => StateOf(flyby, t),
                    ArrivalState = t => StateOf(cT, t),
                    LeaveCost = v => EstimateLeave(k, v),
                    ArriveCost = v => EstimateArrive(0, v),
                    Mu = mu,
                    FlybyMu = B.Mu,
                    FlybyFloor = B.LowOrbitRadius,
                    FlybyCeiling = FlybyModel.MaxPeriapsisShare * B.Soi,
                    Tof1Lo = tof1Lo, Tof1Hi = tof1Hi, Tof2Lo = 0.3 * h2, Tof2Hi = 1.5 * h2,
                    DepartureLo = depLo, DepartureHi = depHi,
                    FlybyLo = depLo + tof1Lo, FlybyHi = depHi + tof1Hi,
                    MaxTrip = double.IsInfinity(MaxTrip) ? double.PositiveInfinity : MaxTrip - climb,
                    Directions1 = dirs, Directions2 = dirs, Revolutions1 = revs,
                };
            }

            public TransferPlan SiblingAssist(int flyby, AssistCandidate c)
            {
                var built = BuildSiblingAssist(flyby, c);
                built = ResolveDeparture(built, shift =>
                {
                    var moved = c;
                    moved.DepartureUT += shift * ShiftUnit();
                    return BuildSiblingAssist(flyby, moved);
                });
                Trace?.Invoke(Inv($"[Transfer] assist candidate via {S[flyby].Name}: grid {c.Cost:F1} m/s, built ") +
                    (built.Plan.Ok ? Inv($"{built.Plan.TotalDeltaV:F1} m/s") : $"refused ({built.Plan.Refusal})"));
                return built.Plan;
            }

            // The cruise after the departure, aimed at a target through ArrivalTargeting with its
            // plane: the correction that would be needed right after the SOI exit is moved into the
            // departure (as BuildPorkchop's AimAt does), each step checked in flight. The predicted
            // periapsis, NaN when no solve.
            double AimCruise(int k, double t, ref Vector3d aim, ref Leg leg, ref Flight flight, Target target, double arrive)
            {
                double predicted = double.NaN;
                double horizon = arrive + 0.25 * (arrive - flight.AfterUT);
                for (int iter = 0; iter < 3; iter++)
                {
                    if (!ArrivalTargeting.Solve(S, Ancestor, flight.AfterR, flight.AfterV, flight.AfterUT, target.Body, target.Pe,
                            target.Inc, target.Plane, horizon, arrive, double.NaN, ArrivalTargeting.MaxDeltaV,
                            out var dvAim, out double rp, out _, out _, out _, out _))
                        return predicted;
                    predicted = rp;
                    if (dvAim.magnitude < 1e-3)
                        break;
                    var aimed = Leave(k, aim + dvAim, t);
                    if (!aimed.Ok)
                        break;
                    var aimedFlight = FlyClimb(aimed);
                    if (!aimedFlight.Ok)
                        break;
                    aim += dvAim;
                    leg = aimed;
                    flight = aimedFlight;
                }
                return predicted;
            }

            // The flyby, flown from (rPe, vPe) at tPe in the flyby body: the burn toward the wanted
            // excess (none under MinBurn), then the path to the SOI exit. False when no velocity
            // there clears the floor, or the path does not leave.
            bool FlyFlyby(int flyby, Vector3d rPe, Vector3d vPe, double tPe, Vector3d wanted, out Vector3d dv,
                out Patch exit, out List<Patch> patches, out string why)
            {
                var B = S[flyby];
                exit = null;
                patches = null;
                why = null;
                if (!AsymptoteBurn(rPe, vPe, wanted, B.Mu, Floor(flyby), out dv, out var vAfter))
                {
                    why = $"no flyby of {B.Name} above its atmosphere or terrain gives the excess wanted";
                    return false;
                }
                if (dv.magnitude < FlybyModel.MinBurn)
                {
                    dv = Vector3d.zero;
                    vAfter = vPe;
                }
                double tOut = Finite(TimeToRadius(rPe, vAfter, B.Mu, B.Soi), 10 * S.OrbitalPeriod(flyby));
                patches = PatchedPropagator.Propagate(S, flyby, rPe, vAfter, tPe, tPe + 1.2 * tOut + 3600, null, 3);
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    if (p.Body != flyby)
                        break;
                    if (p.End == PatchEnd.Impact)
                    {
                        why = $"the flyby hits {B.Name}";
                        return false;
                    }
                    if (p.End == PatchEnd.Escape && i + 1 < patches.Count)
                    {
                        exit = patches[i + 1];
                        return true;
                    }
                }
                why = $"the path does not leave {B.Name} after the flyby";
                return false;
            }

            Built BuildSiblingAssist(int flyby, AssistCandidate c)
            {
                Ct.ThrowIfCancellationRequested();
                int k = Up.Count - 1, cF = Up[k], cT = Down[0];
                var B = S[flyby];
                string name = B.Name;
                double muA = S[Ancestor].Mu;
                double t = c.DepartureUT, tF = c.FlybyUT, tA = c.ArrivalUT;
                if (!(tF > t) || !(tA > tF))
                    return Failed(NoAssist("bad candidate dates"));
                S.StateRelativeToParent(cF, t, out var rF, out var vF);
                S.StateRelativeToParent(flyby, tF, out var rB, out _);
                if (!Lambert.Solve(rF, rB, tF - t, muA, c.Prograde1, c.Revolutions1, c.LongPeriod1, out var v1, out _))
                    return Failed($"No transfer found to {name}.");

                // 1. The climb, aimed at the real SOI exit (as BuildPorkchop).
                Vector3d aim = v1 - vF;
                Leg leg = null;
                Flight flight = null;
                for (int iter = 0; iter < MaxExitIterations; iter++)
                {
                    leg = Leave(k, aim, t);
                    if (!leg.Ok)
                        return Failed(leg.Refusal);
                    flight = FlyClimb(leg);
                    if (!flight.Ok)
                        break;
                    double rest = tF - flight.AfterUT;
                    if (!(rest > 0) || !Lambert.Solve(flight.AfterR, rB, rest, muA, c.Prograde1, c.Revolutions1, c.LongPeriod1, out var v1x, out _))
                        break;
                    Vector3d miss = v1x - flight.AfterV;
                    if (miss.magnitude < ExitTolerance)
                        break;
                    aim += miss;
                }
                if (!flight.Ok)
                    return FlightFailure(FlightRefusal(flight));
                var built = new Built { Unplanned = flight.Unplanned, UnplannedUT = flight.UnplannedUT };

                // 2. The cruise aimed at the flyby periapsis, in its plane (the flyby body's own frame).
                var flyTarget = new Target(flyby, Math.Max(c.FlybyPeriapsis, B.LowOrbitRadius), double.NaN, double.NaN,
                    CelestialFrames.ToLocal(B.FrameToCommon, c.Normal));
                double predictedFlyby = AimCruise(k, t, ref aim, ref leg, ref flight, flyTarget, tF);

                // 3. The flyby as the aimed cruise meets it.
                var cruise = PatchedPropagator.Propagate(S, Ancestor, flight.AfterR, flight.AfterV, flight.AfterUT,
                    tF + 0.25 * (tF - flight.AfterUT) + 86_400, null, 4);
                var inside = cruise.FirstOrDefault(p => p.Body == flyby);
                if (inside == null)
                    return FlightFailure($"The cruise does not reach {name}.");
                NoteCruise(built, cruise.TakeWhile(p => p != inside), flyby);

                var plan = NewPlan();
                plan.Steps.AddRange(leg.Steps);
                plan.Steps.AddRange(flight.Steps);
                var failed = AppendFlyby(built, plan, flyby, inside.StartR, inside.StartV, inside.StartUT, tA, c.Prograde2, k + 1,
                    out var arr, out double tPe);
                if (failed != null)
                    return failed;
                AddCorrections(plan.Steps, Ancestor, k, flyTarget, flight.TopExitUT, leg.FirstBurnUT, tPe, inside.StartUT, null);
                var departure = plan.Steps.FirstOrDefault(st => st.Kind == StepKind.Injection);
                if (departure != null)
                    departure.Explanation += $". Gravity assist via {name}: see the flyby step";
                if (double.IsNaN(predictedFlyby))
                    plan.Warnings.Add($"Departure could not be aimed at the {name} flyby periapsis: the corrections will do it.");

                var targets = ClimbTargets(leg);
                targets[k] = flyTarget;
                targets[k + 1] = arr.FirstTarget;
                targets[k + 2] = arr.FirstTarget;
                plan.ExcessBody = cF;
                plan.RequiredExcess = aim;
                return Finish(built, plan, leg.FirstBurnUT, arr.ArrivalUT, targets);
            }

            // The flyby of 'flyby' met from (rIn, vIn) at utIn in its SOI (periapsis ahead), then the trip
            // to the target's branch arriving at tA: the Flyby and its SOI exit on flybyLeg, the
            // corrections and the arrival on flybyLeg + 1, appended to the plan with its assist fields.
            // Null when it can be flown; else the failure.
            Built AppendFlyby(Built built, TransferPlan plan, int flyby, Vector3d rIn, Vector3d vIn, double utIn, double tA,
                bool prograde2, int flybyLeg, out Arrival arr, out double tPe)
            {
                arr = null;
                var B = S[flyby];
                string name = B.Name;
                int cT = Down[0];
                double muA = S[Ancestor].Mu;
                tPe = utIn + Finite(TimeToPeriapsis(rIn, vIn, B.Mu), 0);
                KeplerPropagator.Propagate(rIn, vIn, B.Mu, tPe - utIn, out var rPe, out var vPe);
                if (rPe.magnitude < Floor(flyby))
                    return FlightFailure($"The flyby of {name} would pass below its atmosphere or terrain ({Km(flyby, rPe.magnitude)} km).");

                // The excess wanted out of the flyby: Lambert from the flyby body at the periapsis to
                // the target's branch, then the flown exit matched to it (as the climb).
                S.StateRelativeToParent(flyby, tPe, out var rBp, out var vBp);
                S.StateRelativeToParent(cT, tA, out var rT, out var vT);
                if (!(tA > tPe) || !Lambert.Solve(rBp, rT, tA - tPe, muA, prograde2, 0, false, out var w1, out var w2))
                    return Failed($"No transfer found from {name} to {S[cT].Name}.");
                Vector3d wanted = w1 - vBp, wa = w2 - vT;
                Vector3d dv = Vector3d.zero;
                Patch exit = null;
                for (int iter = 0; iter < MaxExitIterations; iter++)
                {
                    if (!FlyFlyby(flyby, rPe, vPe, tPe, wanted, out dv, out exit, out _, out string why))
                        return FlightFailure($"The flyby of {name} cannot be flown as planned: {why}.");
                    double rest = tA - exit.StartUT;
                    if (!(rest > 0) || !Lambert.Solve(exit.StartR, rT, rest, muA, prograde2, 0, false, out var x1, out var x2))
                        break;
                    Vector3d miss = x1 - exit.StartV;
                    wa = x2 - vT;
                    if (miss.magnitude < ExitTolerance || dv.sqrMagnitude == 0)
                        break;
                    wanted += miss;
                }

                // The arrival from the flown exit, then the flyby burn aimed at its first target.
                arr = ChooseArrival(wa, tA, flybyLeg + 1, true, Ancestor, exit.StartR, exit.StartV, exit.StartUT);
                double residual = double.NaN;
                double horizon = tA + 0.25 * (tA - exit.StartUT);
                for (int iter = 0; iter < 3; iter++)
                {
                    var target = arr.FirstTarget;
                    if (double.IsNaN(target.Pe) || !ArrivalTargeting.Solve(S, Ancestor, exit.StartR, exit.StartV, exit.StartUT, target.Body,
                            target.Pe, target.Inc, target.Plane, horizon, tA, double.NaN, ArrivalTargeting.MaxDeltaV,
                            out var dvAim, out _, out _, out _, out _, out _))
                        break;
                    residual = dvAim.magnitude;
                    if (residual < ExitTolerance || dv.sqrMagnitude == 0)
                        break;
                    if (!FlyFlyby(flyby, rPe, vPe, tPe, wanted + dvAim, out var dv2, out var exit2, out _, out _))
                        break;
                    wanted += dvAim;
                    dv = dv2;
                    exit = exit2;
                }

                // The cruise after the flyby, to the arrival: an SOI the plan does not include is warned about.
                NoteCruise(built, PatchedPropagator.Propagate(S, Ancestor, exit.StartR, exit.StartV, exit.StartUT, tA, null, 4), flyby);

                plan.CaseName = CaseName + " via " + name;
                plan.Assist = AssistKind.Sibling;
                plan.FlybyBody = flyby;
                Vector3d sIn = ArrivalTargeting.IncomingAsymptote(rIn, vIn, B.Mu);
                Vector3d sOut = Hyperbola.Asymptote(rPe, vPe + dv, B.Mu);
                double turn = sIn.sqrMagnitude > 0 && sOut.sqrMagnitude > 0 ? AngleDeg(sIn, sOut) : double.NaN;
                string power = dv.sqrMagnitude > 0 ? Inv($", powered: {dv.magnitude:F0} m/s at the periapsis") : ", no burn";
                var fly = Burn(StepKind.Flyby, flyby, tPe, dv,
                    Inv($"Flyby of {name} at {Km(flyby, rPe.magnitude)} km: turns the path by {turn:F1}° toward {S[cT].Name}{power}. The corrections before it aim at its periapsis and plane; the ones after it aim at {S[arr.FirstTarget.Body].Name}"),
                    flybyLeg);
                fly.TargetExcess = wanted;
                arr.FirstTarget.ApplyTo(fly);
                plan.Steps.Add(fly);
                plan.Steps.Add(Step(StepKind.Escape, flyby, exit.StartUT, 0, $"Leave the {name} SOI", flybyLeg));

                plan.Steps.AddRange(arr.Steps);
                plan.Warnings.AddRange(arr.Warnings);
                plan.FinalLegRequest = arr.FinalLegRequest ?? AfterFlybyRequest(exit);
                var arrival = S[cT];
                double entry = tA - HyperbolicTime(arrival.LowOrbitRadius, Math.Max(1, wa.magnitude), arrival.Mu, arrival.Soi);
                int before = plan.Steps.Count;
                AddCorrections(plan.Steps, Ancestor, flybyLeg + 1, arr.FirstTarget, exit.StartUT, tPe, tA, entry, arr.Notes);
                if (plan.Steps.Count > before)
                {
                    var first = plan.Steps[before];
                    first.Explanation = $"Correction after the {name} flyby: aims {Describe(arr.FirstTarget)} ({Solved})";
                    if (residual >= ArrivalTargeting.SkipThreshold)
                    {
                        first.DeltaVMagnitude = residual;
                        first.Explanation += Inv($". Estimated from the planned flyby: {residual:F0} m/s");
                    }
                }
                return null;
            }

            // An SOI on a cruise that the plan does not include (the flyby body is planned).
            void NoteCruise(Built built, IEnumerable<Patch> patches, int flyby)
            {
                if (built.Unplanned >= 0)
                    return;
                foreach (var p in patches)
                    if (p.End == PatchEnd.Encounter && p.NextBody != flyby && !Planned.Contains(p.NextBody))
                    {
                        built.Unplanned = p.NextBody;
                        built.UnplannedUT = p.EndUT;
                        return;
                    }
            }

            // The request of the leg after the flyby, from the flown exit (FarRendezvous predicts the
            // arrival from it).
            TransferRequest AfterFlybyRequest(Patch exit) => new TransferRequest
            {
                FromBody = exit.Body,
                ToBody = To,
                R0 = exit.StartR,
                V0 = exit.StartV,
                UT0 = exit.StartUT,
                Arrival = Q.Arrival,
                ArrivalPeriapsisRadius = Q.ArrivalPeriapsisRadius,
                ArrivalApoapsisRadius = Q.ArrivalApoapsisRadius,
                ArrivalInclinationDeg = Q.ArrivalInclinationDeg,
                MaxTripSeconds = Q.MaxTripSeconds,
                ArrivalPlaneNormal = Q.ArrivalPlaneNormal,
            };
        }
    }
}
