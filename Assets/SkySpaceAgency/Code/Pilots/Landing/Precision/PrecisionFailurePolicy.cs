using System.Globalization;

namespace K2D2.Landing.Precision
{
    // The four precision burns, in flight order.
    public enum PrecisionStage { OrbitPrep, PlaneChange, Deorbit, MidCourse }

    public enum FailureAction
    {
        Stop,              // stop the pilot with the reason (the orbit still clears the ground)
        Skip,              // log, warn, and go on with the normal sequence (warps, brake, guidance)
        FallBackNoTarget,  // land without target (the non-precision sequence from QuickWarp)
    }

    // What a failed precision burn does (a refused plan, a refused or aborted burn). Once the
    // vessel is on an impact course the pilot must keep flying: a failure never stops it there.
    // Pure, tested in EditMode.
    public static class PrecisionFailurePolicy
    {
        /// <summary>
        /// MidCourse: Skip (the deorbit is flown, the vessel is falling; the terminal guidance
        /// corrects the rest). Any other burn: FallBackNoTarget when the trajectory hits the
        /// ground (a deorbit that failed after some of its Δv, or any burn that left the orbit
        /// crossing the ground), Stop otherwise (still in a safe orbit).
        /// </summary>
        public static FailureAction Decide(PrecisionStage stage, bool orbitHitsGround)
        {
            if (stage == PrecisionStage.MidCourse)
                return FailureAction.Skip;
            return orbitHitsGround ? FailureAction.FallBackNoTarget : FailureAction.Stop;
        }

        /// <summary>
        /// The trajectory hits the ground: the game's terrain collision search found one, or the
        /// periapsis is under the body radius (a NaN periapsis, an open orbit, counts as clear).
        /// </summary>
        public static bool OrbitHitsGround(double periapsisRadius, double bodyRadius, bool collisionPredicted) =>
            collisionPredicted || periapsisRadius < bodyRadius;

        public static string SkipMessage(string reason) => "Course correction skipped: " + Trim(reason) + ".";

        public static string FallBackMessage(string reason) =>
            string.Format(CultureInfo.InvariantCulture, "Precision landing aborted: {0}. Landing without target.", Trim(reason));

        static string Trim(string reason) => string.IsNullOrEmpty(reason) ? "unknown reason" : reason.Trim().TrimEnd('.');
    }
}
