using System;

namespace K2D2.Node
{
    /// <summary>
    /// Aligning before warping to a burn (TargetOrbitBurn). On rails warp the vessel cannot turn
    /// (2026-10-25: 99.4° off, 51.8 s before an injection, still warping): so before any warp toward a
    /// burn the vessel drops to 1x and turns to the burn direction as it will be at the burn start,
    /// then warps to a few seconds before the burn, where the alignment is checked again at 1x. A long
    /// wait, or one during which the local frame turns a lot, pre-aligns a second time shortly
    /// before the burn. Pure, tested.
    /// </summary>
    public static class BurnAlignPolicy
    {
        // Aligned: within this angle (°) and turning slower than this (°/s).
        public const double MaxAngleDeg = 2;
        public const double MaxRateDegPerS = 0.5;
        // The pre-alignment gives up after the larger of this and twice the alignment lead (s).
        public const double MinTimeoutSeconds = 60;
        // The warp toward the burn ends this long before its start (s), longer for a long burn.
        public const double WarpTailSeconds = 10, LongBurnWarpTailSeconds = 15, LongBurnSeconds = 60;
        // A second pre-alignment when the wait is longer than the orbit period, or the local frame
        // (and so the SAS target set in it) turns more than this (°) during the wait.
        public const double MaxFrameTurnDeg = 30;
        // The second pre-alignment starts this long before the alignment lead (s).
        public const double SecondPreAlignMargin = 30;

        public static bool Aligned(double angleDeg, double rateDegPerS) =>
            angleDeg < MaxAngleDeg && rateDegPerS < MaxRateDegPerS;

        public static double TimeoutSeconds(double alignLead) =>
            Math.Max(MinTimeoutSeconds, 2 * (double.IsNaN(alignLead) ? 0 : alignLead));

        public static double WarpTail(double burnSeconds) =>
            burnSeconds > LongBurnSeconds ? LongBurnWarpTailSeconds : WarpTailSeconds;

        /// <summary>waitSeconds: from now to the burn start; period: the orbit's (∞ on an escape);
        /// frameTurnDeg: how far the local frame turns from now to the burn (the radius direction).</summary>
        public static bool SecondPreAlign(double waitSeconds, double period, double frameTurnDeg) =>
            (period > 0 && !double.IsInfinity(period) && waitSeconds > period) || frameTurnDeg > MaxFrameTurnDeg;

        /// <summary>When the second pre-alignment starts: before the alignment lead and its margin.</summary>
        public static double SecondPreAlignUT(double burnStartUT, double alignLead) =>
            burnStartUT - alignLead - SecondPreAlignMargin;

        /// <summary>After the warp: re-align before igniting when the drift exceeds the aligned angle.</summary>
        public static bool NeedsRealign(double driftDeg) => driftDeg > MaxAngleDeg;
    }
}
