using System;
using KSP.Sim;

namespace K2D2.OrbitPlanning
{
    public enum OrbitPlanStepOutcome
    {
        // Burn holds the next burn to fly.
        Burn,
        // The orbit reached is the target: nothing left to do.
        Done,
        // The planner refuses from the orbit reached (Error says why).
        Error,
        // The vessel is no longer around the body the plan was made for.
        BodyChanged,
        // More steps than the plan can need: something keeps the target out of reach.
        TooManySteps
    }

    public struct OrbitPlanStep
    {
        public OrbitPlanStepOutcome Outcome;
        public PlannedBurn Burn;
        public OrbitPlanError Error;
        // 1 for the first burn flown, 0 when no burn is returned.
        public int Step;
        // Burns the plan still holds, this one included.
        public int RemainingBurns;
    }

    /// <summary>
    /// An Orbit tab plan flown one burn at a time. The game is given a single maneuver node at
    /// a time: it dropped the later nodes of a multi-node plan (2026-10-18). Each next burn is
    /// planned again with the same targets, from the orbit the previous burn actually reached,
    /// so a small error of one burn is corrected by the next instead of carried over. Pure
    /// logic over OrbitPlanner, tested in the EditMode tests.
    /// </summary>
    public sealed class OrbitPlanSequence
    {
        // Steps allowed beyond the original plan, for small corrections after imprecise burns.
        public const int ExtraSteps = 2;

        public readonly OrbitTargets Targets;
        public readonly BodyInfo Body;
        public readonly int PlannedSteps;
        public readonly int MaxSteps;
        public int StepsStarted { get; private set; }

        public OrbitPlanSequence(OrbitTargets targets, BodyInfo body, int plannedSteps)
        {
            Targets = targets;
            Body = body;
            PlannedSteps = plannedSteps;
            MaxSteps = plannedSteps + ExtraSteps;
        }

        public OrbitPlanStep Next(Vector3d r, Vector3d v, double ut, BodyInfo body)
        {
            if (!SameBody(body))
                return new OrbitPlanStep { Outcome = OrbitPlanStepOutcome.BodyChanged };

            var plan = OrbitPlanner.Plan(r, v, ut, Body, Targets);
            if (!plan.Ok)
                return new OrbitPlanStep { Outcome = OrbitPlanStepOutcome.Error, Error = plan.Error };
            if (plan.Burns.Count == 0)
                return new OrbitPlanStep { Outcome = OrbitPlanStepOutcome.Done };
            if (StepsStarted >= MaxSteps)
                return new OrbitPlanStep { Outcome = OrbitPlanStepOutcome.TooManySteps, RemainingBurns = plan.Burns.Count };

            StepsStarted++;
            return new OrbitPlanStep
            {
                Outcome = OrbitPlanStepOutcome.Burn,
                Burn = plan.Burns[0],
                Step = StepsStarted,
                RemainingBurns = plan.Burns.Count
            };
        }

        bool SameBody(BodyInfo body) =>
            Math.Abs(body.Mu - Body.Mu) <= 1e-9 * Math.Abs(Body.Mu) && Math.Abs(body.Radius - Body.Radius) < 1;
    }
}
