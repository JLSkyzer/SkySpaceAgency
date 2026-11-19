using System;

namespace K2D2.Landing.Braking
{
    // Standing on the ground while the height reading may still say tens of metres up (from
    // K2-D2 1.4.0, IanMealworm's LandingPilot.UpdateStanding): barely moving with the engines
    // throttled well below hovering, for long enough that in the air the vessel would have
    // fallen faster than that. Upstream, a landing from orbit came down on ground the height
    // reading put 70 m below it, the game did not flag it as landed either, and the final
    // descent kept steering on the ground. Only while the engines are landing it; on very low
    // gravity (Minmus) the wait gets too long to tell, and the game's landed flag has to do.
    public sealed class StandingCheck
    {
        public const double SpeedLimitMps = 1.5;
        public const double ThrustLimitG = 0.5;
        public const double MinHoldS = 1.5;
        public const double MaxHoldS = 5;

        double still_since = double.NaN;

        // Long enough for the unbalanced part of gravity (at the thrust limit) to add twice the
        // speed limit: a vessel in the air could not stay below it that long.
        public static double HoldSeconds(double gravity)
        {
            return Math.Max(MinHoldS, 2 * SpeedLimitMps / Math.Max(1e-6, (1 - ThrustLimitG) * gravity));
        }

        // False where the hold would exceed MaxHoldS (Minmus): the check never fires there.
        public static bool Applies(double gravity) => HoldSeconds(gravity) <= MaxHoldS;

        public void Reset() => still_since = double.NaN;

        // Called every frame. now in s (game time), groundSpeed in m/s, thrustAccel = throttle ×
        // available acceleration (m/s²), gravity in m/s². True once the vessel has held still with
        // low thrust for HoldSeconds(gravity) while powered (Brake or TouchDown).
        public bool Update(double now, bool powered, double groundSpeed, double thrustAccel, double gravity)
        {
            bool still = powered && Applies(gravity)
                && groundSpeed < SpeedLimitMps
                && thrustAccel <= ThrustLimitG * gravity;
            if (!still)
            {
                still_since = double.NaN;
                return false;
            }
            if (double.IsNaN(still_since))
                still_since = now;
            return now - still_since >= HoldSeconds(gravity);
        }
    }
}
