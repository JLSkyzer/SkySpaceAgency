using System;
using KSP.Sim;

namespace K2D2.Landing.Braking
{
    // End of a propulsive landing, below FinalHeight: stand up, lean against the horizontal drift
    // to cancel it, and never cut the thrust only because SAS lags behind.
    public static class FinalDescent
    {
        public const double FinalHeight = 50;      // m above the ground
        public const double TiltGain = 1.0;        // k in atan(k * v_h / g)
        public const double MaxTiltDegrees = 15;
        public const double MinContactSpeed = 0.5; // m/s
        // Below this horizontal speed (m/s) there is no meaningful drift direction.
        const double MinHorizontalSpeed = 1e-6;

        public static bool IsFinal(double height) => height < FinalHeight;

        // Tilt off the vertical, in degrees: atan(k * v_h / g), at most MaxTiltDegrees.
        // 0 (straight up) on unknown inputs; the cap when there is no gravity.
        public static double TiltDegrees(double horizontalSpeed, double gravity)
        {
            if (double.IsNaN(horizontalSpeed) || double.IsNaN(gravity) || !(horizontalSpeed > MinHorizontalSpeed))
                return 0;
            if (!(gravity > 0))
                return MaxTiltDegrees;
            double degrees = Math.Atan(TiltGain * horizontalSpeed / gravity) * 180 / Math.PI;
            return Math.Min(degrees, MaxTiltDegrees);
        }

        // Unit thrust direction: up, tilted by TiltDegrees against the horizontal part of the
        // surface velocity. All vectors in the same frame; up need not be unit.
        public static Vector3d AimDirection(Vector3d up, Vector3d surfaceVelocity, double gravity)
        {
            Vector3d upUnit = up.normalized;
            Vector3d horizontal = surfaceVelocity - upUnit * Vector3d.Dot(surfaceVelocity, upUnit);
            double speed = horizontal.magnitude;
            double tilt = TiltDegrees(speed, gravity) * Math.PI / 180;
            if (tilt == 0)
                return upUnit;
            Vector3d drift = horizontal * (1 / speed);
            return upUnit * Math.Cos(tilt) - drift * Math.Sin(tilt);
        }

        // Share of the computed throttle to apply when the vessel points alignmentErrorDegrees
        // away from the aim: cos(error), 0 from 90° on (or when unknown).
        public static double ThrustFactor(double alignmentErrorDegrees)
        {
            if (double.IsNaN(alignmentErrorDegrees))
                return 0;
            return Math.Max(0, Math.Cos(alignmentErrorDegrees * Math.PI / 180));
        }

        // Target speed at contact: the player's touch-down speed, never below MinContactSpeed,
        // or a touch-down speed of 0 would hover instead of landing.
        public static double ContactSpeed(double touchDownSpeed)
        {
            if (double.IsNaN(touchDownSpeed))
                return MinContactSpeed;
            return Math.Max(touchDownSpeed, MinContactSpeed);
        }
    }
}
