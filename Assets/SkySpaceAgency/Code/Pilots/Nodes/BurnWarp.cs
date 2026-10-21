using System;

namespace K2D2.Node
{
    /// <summary>
    /// Timing, warp and throttle formulas of the closed-loop burns (TargetOrbitBurn): how long
    /// before the burn the vessel must be at 1x to finish turning, how fast time may run while the
    /// engines push, and a throttle that cannot overshoot in one warped frame. Pure maths, tested in
    /// the EditMode tests (BurnWarpTests).
    /// </summary>
    public static class BurnWarp
    {
        // Alignment lead bounds (s) and the settling time added to the turn (s).
        public const double MinLead = 20;
        public const double MaxLead = 120;
        public const double SettleTime = 8;
        // Turn rate (°/s) assumed until one is measured: conservative for most rockets.
        public const double DefaultTurnRate = 3;
        // Time constant (s of real time) of the turn-rate low-pass.
        public const double TurnRateTimeConstant = 1;

        // 5 % rule: the Δv applied in one frame stays under this share of the remaining Δv.
        public const double MaxFrameShare = 0.05;
        // 10 s rule: the remaining burn lasts at least this many seconds of real time.
        public const double MinRealBurnTime = 10;

        public const float MinThrottle = 0.05f;
        // The throttle aims at burning the remaining Δv in at least this much game time (s).
        public const double ThrottleTimeConstant = 1;

        /// <summary>
        /// Seconds before the burn start at which the warp must stop so the vessel can turn
        /// angleToGoDeg at turnRateDegPerS and settle: clamp(angle / rate + 8, 20, 120). An unknown
        /// or non-positive rate uses DefaultTurnRate; an unknown angle gives MaxLead.
        /// </summary>
        public static double AlignmentLead(double angleToGoDeg, double turnRateDegPerS)
        {
            if (double.IsNaN(angleToGoDeg))
                return MaxLead;
            double rate = turnRateDegPerS > 0 ? turnRateDegPerS : DefaultTurnRate;
            double lead = Math.Abs(angleToGoDeg) / rate + SettleTime;
            return Math.Max(MinLead, Math.Min(MaxLead, lead));
        }

        /// <summary>
        /// 5 % rule: highest whole warp rate at which one frame of frameRealDt real seconds at full
        /// thrust (accel m/s²) applies at most 5 % of remainingDv. 1 on any bad input.
        /// </summary>
        public static double FivePercentRate(double remainingDv, double accel, double frameRealDt)
        {
            if (!(remainingDv > 0) || !(accel > 0) || !(frameRealDt > 0))
                return 1;
            return Math.Max(1, Math.Floor(MaxFrameShare * remainingDv / (accel * frameRealDt) + 1e-9));
        }

        /// <summary>
        /// 10 s rule: highest whole warp rate at which the remaining burn (remainingDv / accel
        /// seconds of game time) still lasts 10 s of real time. 1 on any bad input.
        /// </summary>
        public static double TenSecondRate(double remainingDv, double accel)
        {
            if (!(remainingDv > 0) || !(accel > 0))
                return 1;
            return Math.Max(1, Math.Floor(remainingDv / accel / MinRealBurnTime + 1e-9));
        }

        /// <summary>
        /// Highest warp rate allowed while burning: the lower of the 5 % rule and the 10 s rule,
        /// at least 1. Example: 500 m/s left at 5 m/s² with 0.02 s frames gives 250 by the 5 %
        /// rule but 10 by the 10 s rule (100 s of burn left), so 10.
        /// </summary>
        public static double MaxBurnRate(double remainingDv, double accel, double frameRealDt)
        {
            if (!(remainingDv > 0) || !(accel > 0) || !(frameRealDt > 0))
                return 1;
            return Math.Min(FivePercentRate(remainingDv, accel, frameRealDt), TenSecondRate(remainingDv, accel));
        }

        /// <summary>
        /// Index of the highest warp level whose rate is at most maxRate (levels in ascending
        /// order, index 0 = 1x). 0 when none qualifies or the levels are unknown.
        /// </summary>
        public static int HighestIndexAtOrBelow(float[] rates, double maxRate)
        {
            if (rates == null || double.IsNaN(maxRate))
                return 0;
            int best = 0;
            for (int i = 0; i < rates.Length; i++)
                if (rates[i] > 0 && rates[i] <= maxRate)
                    best = i;
            return best;
        }

        /// <summary>
        /// clamp(remainingDv / (accel · max(1 s, frameRealDt · rate)), 0.05, 1): burns the remaining
        /// Δv in no less than one frame of game time, so a warped frame cannot overshoot. 0 without
        /// thrust or with an unknown Δv.
        /// </summary>
        public static float Throttle(double remainingDv, double accel, double frameRealDt, double rate)
        {
            if (!(accel > 0) || double.IsNaN(remainingDv))
                return 0;
            double frameGame = frameRealDt * rate;
            double horizon = frameGame > ThrottleTimeConstant ? frameGame : ThrottleTimeConstant;
            double t = remainingDv / (accel * horizon);
            if (!(t > MinThrottle))
                return MinThrottle;
            return t > 1 ? 1f : (float)t;
        }

        // Late ignition: the alignment wait past the start gives up after this many seconds of game
        // time at least, or LateTimeoutLeads alignment leads.
        public const double MinLateTimeout = 60;
        public const double LateTimeoutLeads = 2;

        /// <summary>
        /// Whether a turn-rate sample is meaningful: only at 1x (a CurrentRate of 0 means no time
        /// warp object, hence 1x) and not on the frame after a warp-index change. Under warp the aim
        /// sweeps at the orbital rate times the warp (about 60°/s of real time at x1000 in low
        /// orbit), so an angle drop there says nothing about the vessel's turn.
        /// </summary>
        public static bool ShouldSampleTurnRate(double currentRate, bool warpIndexChanged)
        {
            return !warpIndexChanged && currentRate <= 1;
        }

        /// <summary>Seconds of game time past the burn start after which a vessel still not aligned
        /// gives up: max(60 s, 2 × the alignment lead); an unknown lead counts as MaxLead.</summary>
        public static double LateIgnitionTimeout(double alignLead)
        {
            double lead = double.IsNaN(alignLead) ? MaxLead : alignLead;
            return Math.Max(MinLateTimeout, LateTimeoutLeads * lead);
        }

        /// <summary>True once now is more than LateIgnitionTimeout(alignLead) past burnStartUT.</summary>
        public static bool AlignmentTimedOut(double now, double burnStartUT, double alignLead)
        {
            return now - burnStartUT > LateIgnitionTimeout(alignLead);
        }

        /// <summary>
        /// Low-passed turn rate (°/s of real time) from the steering angle dropping from
        /// angleBefore to angleNow in realDt seconds. Increases (the aim moved away), a still angle
        /// and a non-positive or unknown realDt keep previousRate; without a previous rate (NaN or
        /// ≤ 0) the sample is taken as is.
        /// </summary>
        public static double MeasuredTurnRate(double previousRate, double angleBefore, double angleNow, double realDt)
        {
            if (!(realDt > 0) || double.IsNaN(angleBefore) || double.IsNaN(angleNow))
                return previousRate;
            double drop = angleBefore - angleNow;
            if (!(drop > 0))
                return previousRate;
            double sample = drop / realDt;
            if (!(previousRate > 0))
                return sample;
            double alpha = 1 - Math.Exp(-realDt / TurnRateTimeConstant);
            return previousRate + alpha * (sample - previousRate);
        }
    }
}
