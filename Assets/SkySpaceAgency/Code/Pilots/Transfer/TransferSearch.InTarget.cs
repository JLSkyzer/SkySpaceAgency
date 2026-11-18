using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        // Seconds from the request's UT to the state the orbit at the target is planned from: room for
        // the pilot to align before the first burn (OrbitPlanner's own lead, 60 s, comes on top).
        public const double InTargetLeadSeconds = 120;

        sealed partial class Search
        {
            // The vessel is already in the target's SOI: the Orbit tab's plan (OrbitPlanner) to the
            // requested orbit, a capture first when the path is open (hyperbolic, or leaving the SOI),
            // instead of a refusal (in game, 2026-11-18: after a Minmus → Kerbin escape that missed,
            // every Resume answered "The target is the body you are orbiting."). Planned in the body's
            // own frame, where its inclination is the game's; the burns are given in the shared one.
            // The burns are orbit changes with a planned vector: the pilot flies each toward the orbit
            // its vector gives from the state at its time (TransferFlight.GuidanceFor), and a resume
            // plans again from the actual state.
            TransferPlan InTargetSoi()
            {
                CaseName = InTarget;
                var body = S[To];
                QuaternionD toCommon = body.FrameToCommon;
                KeplerPropagator.Propagate(CelestialFrames.ToLocal(toCommon, Q.R0), CelestialFrames.ToLocal(toCommon, Q.V0), body.Mu,
                    InTargetLeadSeconds, out var r, out var v);
                double ut = Q.UT0 + InTargetLeadSeconds;
                var info = new BodyInfo
                {
                    Mu = body.Mu,
                    Radius = body.Radius,
                    // The transfer's floor: the atmosphere, or the terrain on an airless body.
                    AtmosphereDepth = Math.Max(body.AtmosphereDepth, body.MaxTerrainHeight),
                    SoiRadius = body.Soi,
                };
                var targets = new OrbitTargets
                {
                    PeAltitude = ArrRp - body.Radius,
                    ApAltitude = ArrRa - body.Radius,
                    InclinationDeg = double.IsNaN(ArrInc) ? (double?)null : ArrInc,
                };
                var orbit = OrbitPlanner.Plan(r, v, ut, info, targets);
                if (!orbit.Ok)
                    return Refuse($"Already in the {body.Name} SOI: {OrbitPlanner.Describe(orbit.Error, body.Name)}");
                if (orbit.Burns.Count == 0)
                    return Refuse($"Already in the requested orbit around {body.Name}.");

                var plan = NewPlan();
                foreach (var burn in orbit.Burns)
                {
                    var st = Burn(StepKind.OrbitChange, To, burn.UT, toCommon * burn.DeltaV,
                        Inv($"{burn.Label}: {Km(To, burn.Target.Rp)} × {Km(To, burn.Target.Ra)} km (the Orbit tab's plan, the vessel being in the {body.Name} SOI)"), 0);
                    new Target(To, burn.Target.Rp, burn.Target.Ra, ArrInc).ApplyTo(st);
                    plan.Steps.Add(st);
                }
                return Finish(new Built(), plan, orbit.Burns[0].UT, orbit.Burns[orbit.Burns.Count - 1].UT, null).Plan;
            }
        }
    }
}
