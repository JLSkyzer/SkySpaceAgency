using System;
using System.Collections.Generic;
using System.Globalization;

namespace K2D2.Landing.Braking
{
    public enum DropReason { None, Dry, Altitude }

    public struct StagePlanInput
    {
        // From the current state, with Stages (the active stage first, the landing stage last, at
        // least two) and StopHeight: the hand-over height (0 = SafeAltitude). DropHeight is set by
        // the plan.
        public BrakeInput Brake;
        // The player's "drop the lower stage at the latest at" height (m above the terrain); 0 or
        // NaN = automatic. It raises the drop height, never lowers it.
        public double PlayerDropHeight;
    }

    public struct StagePlan
    {
        public bool Ok;
        public string Error;               // why the landing with this stage is refused; null when Ok
        // Lowest height (m above the terrain) along the descent where the landing stage alone can
        // still finish the braking; NaN when the lower stages run dry before any such limit.
        public double SafeDropHeight;
        public double DropHeight;          // used: max(MinDropHeight, SafeDropHeight, player's)
        public BrakeResult Brake;          // FindStart with DropHeight
        public bool LandingStageReached;   // the planned braking drops every lower stage
        public bool DroppedDry;            // the last planned drop is for lack of fuel
        public double DropAltitude;        // m above the terrain at the last planned drop; NaN without one
        public double SpeedAtDrop;         // m/s, surface speed at the last planned drop; NaN without one
        public double LandingDeltaVNeeded; // m/s the landing stage spends in the plan
        public double LandingDeltaVAvailable;
        public int Simulations;            // simulated burns, for the log

        public string Describe()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string drop = LandingStageReached
                ? string.Format(c, "last drop at {0:0} m ({1}, {2:0} m/s)", DropAltitude, DroppedDry ? "dry" : "forced", SpeedAtDrop)
                : "no drop planned before the stop";
            return string.Format(c,
                "{0} start {1}, drop height {2:0} m (safe {3}), {4}, landing stage Δv {5:0} of {6:0} m/s, stop {7:0} m, {8} sims",
                Ok ? "ok" : "refused", Brake.Status, DropHeight, double.IsNaN(SafeDropHeight) ? "not limiting" : SafeDropHeight.ToString("0", c) + " m",
                drop, LandingDeltaVNeeded, LandingDeltaVAvailable, Brake.StopAltitude, Simulations) + (Ok ? "" : " -> " + Error);
        }
    }

    // A landing with a chosen stage: the lower stages brake first and leave when dry, or at the
    // drop height at the latest; the landing stage finishes. The drop height is the highest of:
    //  - MinDropHeight above the terrain, so the dropped stage falls away from the landing site;
    //  - the safe height: below it, the landing stage alone, braking at once from the predicted
    //    state at the drop (after BrakeSimulator.StageGap per stage still to drop), would stop
    //    under the hand-over height (BrakeInput.StopHeight) or spend more than its Δv / DeltaVMargin;
    //  - the player's height.
    // The plan and its start are refined a few rounds, since the start depends on the drop height
    // and the safe height on the trajectory. Pure maths, tested in EditMode.
    public static class StagedBrakePlan
    {
        public const double MinDropHeight = 2000;  // m
        public const double DeltaVMargin = 1.20;
        public const int MaxRounds = 3;
        const double Settled = 50;      // m: a round that moves the drop height less ends the search
        const int CoarseChecks = 16;    // samples tried along the descent before refining
        // m under the hand-over height still accepted: the latest start leaves the planned drop
        // exactly at the limit, and the check from a sample (Kepler coast through the gap) differs
        // from the planned run (RK4) by a few metres.
        const double StopSlack = 10;

        public static StagePlan Plan(StagePlanInput input)
        {
            BrakeStage[] stages = input.Brake.Stages;
            if (stages == null || stages.Length < 2)
                throw new ArgumentException("A staged plan needs at least two stages.", nameof(input));
            int last = stages.Length - 1;
            BrakeStage landing = stages[last];
            double available = landing.DeltaV > 0 && !double.IsInfinity(landing.DeltaV) ? landing.DeltaV : 0;
            double player = input.PlayerDropHeight > 0 && !double.IsInfinity(input.PlayerDropHeight) ? input.PlayerDropHeight : 0;
            double floor = Math.Max(MinDropHeight, player);

            var plan = new StagePlan { SafeDropHeight = double.NaN, LandingDeltaVAvailable = available, DropAltitude = double.NaN, SpeedAtDrop = double.NaN };
            BrakeInput free = input.Brake;
            free.DropHeight = 0;
            BrakeInput b = input.Brake;
            double drop = floor;
            b.DropHeight = drop;
            BrakeResult res = BrakeSimulator.FindStart(b);
            int sims = 1;
            DropSearch search = default;
            for (int round = 0; round < MaxRounds; round++)
            {
                search = SafeDrop(free, res.StartUT, available);
                sims += search.Simulations + 1;
                if (search.NoneFeasible)
                    break;
                if (!double.IsNaN(search.Height))
                    plan.SafeDropHeight = double.IsNaN(plan.SafeDropHeight) ? search.Height : Math.Max(plan.SafeDropHeight, search.Height);
                double next = double.IsNaN(plan.SafeDropHeight) ? floor : Math.Max(floor, plan.SafeDropHeight);
                bool settled = next <= drop + Settled;
                if (next > drop)
                {
                    drop = next;
                    b.DropHeight = drop;
                    res = BrakeSimulator.FindStart(b);
                    sims++;
                }
                if (settled)
                    break;
            }

            plan.DropHeight = drop;
            plan.Brake = res;
            plan.Simulations = sims;
            plan.LandingStageReached = res.FinalStageIndex == last && res.Drops != null && res.Drops.Count > 0;
            if (plan.LandingStageReached)
            {
                BrakeDrop d = res.Drops[res.Drops.Count - 1];
                plan.DroppedDry = d.Dry;
                plan.DropAltitude = d.Altitude;
                plan.SpeedAtDrop = d.SurfaceSpeed;
                plan.LandingDeltaVNeeded = res.FinalStageDeltaV;
            }

            int position = stages.Length;
            if (search.NoneFeasible)
                plan.Error = search.ThrustTooLow
                    ? ThrustMessage(position, StopHeightOf(input.Brake))
                    : CannotBrakeMessage(position, search.Speed, available);
            else if (res.Status == BrakeStatus.Impossible)
                plan.Error = plan.LandingStageReached
                    ? CannotBrakeMessage(position, plan.SpeedAtDrop, available)
                    : LandingFeasibility.CannotStopMessage;
            else if (plan.LandingStageReached && DeltaVMargin * plan.LandingDeltaVNeeded > available)
                plan.Error = CannotBrakeMessage(position, plan.SpeedAtDrop, available);
            plan.Ok = plan.Error == null;
            return plan;
        }

        public static string CannotBrakeMessage(int position, double speed, double deltaV) =>
            string.Format(CultureInfo.InvariantCulture, "Stage {0} alone cannot brake from {1:0} m/s: it has {2:0} m/s.", position, speed, deltaV);

        static string ThrustMessage(int position, double handOver) =>
            string.Format(CultureInfo.InvariantCulture,
                "Stage {0} alone cannot stop {1:0} m above the ground after the drop: its thrust is too low.", position, handOver);

        static double StopHeightOf(BrakeInput input) => input.StopHeight > 0 ? input.StopHeight : BrakeSimulator.SafeAltitude;

        /// <summary>
        /// When to drop the active lower stage now: dry first, else at or below the drop height.
        /// </summary>
        public static DropReason DropNow(bool lowerStageLeft, bool dry, double altitude, double dropHeight)
        {
            if (!lowerStageLeft)
                return DropReason.None;
            if (dry)
                return DropReason.Dry;
            return altitude <= dropHeight ? DropReason.Altitude : DropReason.None;
        }

        struct DropSearch
        {
            public double Height;        // NaN: not limiting before the lower stages run dry
            public bool NoneFeasible;    // the landing stage alone can finish from no state along the way
            public bool ThrustTooLow;    // NoneFeasible because it never stops above the hand-over (else: Δv)
            public double Speed;         // surface speed at the earliest state tried, for the message
            public int Simulations;
        }

        // Along the descent flown without a forced drop from startUT, the lowest state from which
        // the landing stage alone can finish (LandingStageCan). The states are tried from the
        // end backwards, CoarseChecks of them, then the bracket is bisected.
        static DropSearch SafeDrop(BrakeInput free, double startUT, double available)
        {
            int last = free.Stages.Length - 1;
            var samples = new List<BrakeSample>();
            // Every state with a lower stage still pushing, and the moment the last one leaves
            // (its gap begins there, so one StageGap is left before the landing stage pushes).
            var dropsLeft = new List<int>();
            bool reached = false;
            BrakeSimulator.Simulate(free, startUT, s =>
            {
                if (s.StageIndex < last && s.Thrusting)
                {
                    samples.Add(s);
                    dropsLeft.Add(last - s.StageIndex);
                }
                else if (s.StageIndex == last && !reached && samples.Count > 0)
                {
                    reached = true;
                    samples.Add(s);
                    dropsLeft.Add(1);
                }
            });
            var search = new DropSearch { Height = double.NaN, Simulations = 1 };
            if (samples.Count == 0)
                return search;

            int n = samples.Count;
            bool Can(int i, out bool thrustTooLow)
            {
                search.Simulations++;
                return LandingStageCan(free, samples[i], dropsLeft[i], available, out thrustTooLow);
            }

            if (Can(n - 1, out _))
                return search;   // still fine when the lower stages are dry (or the braking ends)

            int stride = Math.Max(1, n / CoarseChecks);
            int bad = n - 1, good = -1;
            for (int i = n - 1 - stride; i >= 0; i -= stride)
            {
                if (Can(i, out _))
                {
                    good = i;
                    break;
                }
                bad = i;
            }
            if (good < 0 && bad > 0)
            {
                if (Can(0, out _))
                    good = 0;
                else
                    bad = 0;
            }
            if (good < 0)
            {
                Can(0, out bool thrustTooLow);
                search.NoneFeasible = true;
                search.ThrustTooLow = thrustTooLow;
                search.Speed = BodyRotation.SurfaceVelocity(samples[0].Position, samples[0].Velocity, free.AngularVelocity).magnitude;
                return search;
            }
            while (bad - good > 1)
            {
                int mid = (good + bad) / 2;
                if (Can(mid, out _))
                    good = mid;
                else
                    bad = mid;
            }
            search.Height = samples[good].Altitude;
            return search;
        }

        // The landing stage alone, from the state of the sample with dropsLeft stages still to
        // drop (StageGap each, coasting), braking at once: stops at or above the hand-over height
        // and spends at most its Δv / DeltaVMargin. thrustTooLow: it failed on the stop.
        static bool LandingStageCan(BrakeInput free, BrakeSample from, int dropsLeft, double available, out bool thrustTooLow)
        {
            BrakeInput alone = BrakeSimulator.Rebase(free, from.Position, from.Velocity, from.UT);
            alone.Stages = new[] { free.Stages[free.Stages.Length - 1] };
            alone.DropHeight = 0;
            BrakeRun run = BrakeSimulator.Simulate(alone, from.UT + dropsLeft * BrakeSimulator.StageGap);
            thrustTooLow = !run.Stopped || run.StopAltitude < StopHeightOf(free) - StopSlack;
            return !thrustTooLow && DeltaVMargin * run.DeltaV <= available;
        }
    }
}
