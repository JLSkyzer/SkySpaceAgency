using System;

namespace K2D2.Landing.Braking
{
    // How fast the descent may go at a given height above the ground.
    public static class DescentEnvelope
    {
        // Share of the active thrust the planning counts on, leaving the rest as margin.
        public const double ThrustFraction = 0.85;

        // Speed from which ThrustFraction of the thrust still stops at the ground, plus the
        // touch-down speed, and never more than the player's own altitude/speed profile.
        public static double MaxSpeed(double height, double accel, double gravity, double touchDownSpeed, double playerLimit)
        {
            double envelope = StoppingSpeed(height, ThrustFraction * Sanitize(accel), gravity) + touchDownSpeed;
            return Math.Min(envelope, playerLimit);
        }

        // False when even full thrust can no longer stop before the ground. The touch-down speed
        // is slack: arriving at it is a landing, not a crash.
        public static bool CanStop(double speed, double height, double accel, double gravity, double touchDownSpeed)
        {
            return speed <= StoppingSpeed(height, Sanitize(accel), gravity) + touchDownSpeed;
        }

        // Speed that a net deceleration of (accel - gravity) cancels over the given height.
        static double StoppingSpeed(double height, double accel, double gravity)
        {
            double net = accel - gravity;
            if (!(net > 0) || !(height > 0))
                return 0;
            return Math.Sqrt(2 * net * height);
        }

        static double Sanitize(double accel) => accel > 0 && !double.IsInfinity(accel) ? accel : 0;
    }
}
