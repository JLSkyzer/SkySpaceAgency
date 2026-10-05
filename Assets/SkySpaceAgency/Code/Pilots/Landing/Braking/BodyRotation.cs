using System;
using KSP.Sim;

namespace K2D2.Landing.Braking
{
    // Body rotation in the game's Zup frame (z = rotation axis). The direction of rotation along z
    // is not documented, so the sign is calibrated against the surface speed the game measures.
    public static class BodyRotation
    {
        // Below this gap between the two predictions (m/s), the sign cannot be told apart.
        public const double MinSpeedDifference = 1.0;

        public static double AngularSpeed(double rotationPeriod)
        {
            double period = Math.Abs(rotationPeriod);
            if (!(period > 0) || double.IsInfinity(period))
                return 0;
            return 2 * Math.PI / period;
        }

        public static Vector3d AngularVelocity(double angularSpeed, int sign)
        {
            return new Vector3d(0, 0, sign * angularSpeed);
        }

        public static Vector3d SurfaceVelocity(Vector3d r, Vector3d v, Vector3d angularVelocity)
        {
            return v - Vector3d.Cross(angularVelocity, r);
        }

        public static bool IsDecisive(Vector3d r, Vector3d v, double angularSpeed)
        {
            Predict(r, v, angularSpeed, out double plus, out double minus);
            return Math.Abs(plus - minus) >= MinSpeedDifference;
        }

        // +1 or -1: the sign whose predicted surface speed is closer to the measured one; +1 when
        // the two predictions are too close to tell.
        public static int ChooseSign(Vector3d r, Vector3d v, double angularSpeed, double measuredSurfaceSpeed)
        {
            Predict(r, v, angularSpeed, out double plus, out double minus);
            if (Math.Abs(plus - minus) < MinSpeedDifference)
                return 1;
            return Math.Abs(plus - measuredSurfaceSpeed) <= Math.Abs(minus - measuredSurfaceSpeed) ? 1 : -1;
        }

        static void Predict(Vector3d r, Vector3d v, double angularSpeed, out double plus, out double minus)
        {
            plus = SurfaceVelocity(r, v, AngularVelocity(angularSpeed, 1)).magnitude;
            minus = SurfaceVelocity(r, v, AngularVelocity(angularSpeed, -1)).magnitude;
        }
    }
}
