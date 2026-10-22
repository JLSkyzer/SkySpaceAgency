using System;

namespace K2D2.Landing.Precision
{
    // Sign of the body rotation in the Zup frame, measured from the game: the game longitude of
    // one fixed inertial position, read twice. The body turns under it by ω·dt, so a body-fixed
    // point at that position the second time sat at azimuth −ω_z·dt the first time, and the
    // longitude moves by −LonSign·ω_z·dt (SurfaceGeometry's convention): ω_z's sign is
    // −sign(drift)·LonSign. Used when the surface-speed calibration cannot decide (polar orbits,
    // slow bodies). Pure maths, tested in EditMode.
    public static class RotationProbe
    {
        public const double MinSeconds = 2;        // game seconds between the two readings
        // The drift must be within these factors of the expected ω·dt.
        public const double MinRatio = 0.5;
        public const double MaxRatio = 2;
        // Below this surface speed of the rotation × an hour, the sign changes nothing (m).
        public const double NegligibleM = 10;
        public const string DisagreeMessage = "Rotation direction measurements disagree: measuring again, press Land again in a few seconds.";

        /// <summary>
        /// The surface-speed calibration and the probe both measured the sign (non-zero) and found
        /// opposite ones: one of them is wrong, so a precision landing must not trust either.
        /// </summary>
        public static bool Disagree(int speedSign, int probeSign) => speedSign != 0 && probeSign != 0 && speedSign != probeSign;

        /// <summary>
        /// Sign (+1 / −1) of ω_z from two game longitudes (degrees) of one inertial position dt game
        /// seconds apart, or 0 with a reason. omega: |ω| (rad/s), radius: body radius (m). A
        /// rotation too slow to matter gives +1 with a note in reason.
        /// </summary>
        public static int Decide(double lonBeforeDeg, double lonAfterDeg, double dt, int lonSign,
            double omega, double radius, out string reason)
        {
            reason = null;
            if (omega * radius * 3600 < NegligibleM)
            {
                reason = "rotation negligible: sign +1 kept";
                return 1;
            }
            if (!(dt >= MinSeconds) || double.IsNaN(lonBeforeDeg) || double.IsNaN(lonAfterDeg))
            {
                reason = "The body's rotation could not be measured (no valid reading).";
                return 0;
            }
            double drift = Wrap180(lonAfterDeg - lonBeforeDeg);
            double expected = omega * dt * 180 / Math.PI;
            double ratio = Math.Abs(drift) / expected;
            if (ratio < MinRatio || ratio > MaxRatio)
            {
                reason = $"The body's rotation could not be measured (longitude moved {drift:0.000000}° in {dt:0.0} s, expected {expected:0.000000}°).";
                return 0;
            }
            return -Math.Sign(drift) * (lonSign < 0 ? -1 : 1);
        }

        static double Wrap180(double d)
        {
            d = (d + 180) % 360;
            if (d < 0) d += 360;
            return d - 180;
        }
    }
}
