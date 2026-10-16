using System;
using KSP.Sim;

namespace K2D2.OrbitPlanning
{
    /// <summary>
    /// Closed-loop circularization guidance: the velocity change that turns the current state into
    /// a circular orbit at the current radius, in the same plane and direction of travel. Unlike a
    /// maneuver node, it does not depend on the game's finite-burn model (which breaks when the
    /// active stage is nearly empty), so a controller can re-evaluate it every frame and steer
    /// along it until it vanishes. Pure maths, tested in the EditMode tests.
    /// </summary>
    public static class CircularizeGuidance
    {
        // Below this horizontal speed (m/s) the direction of travel is undefined.
        const double MinHorizontalSpeed = 1e-6;

        /// <summary>
        /// v_circ · t̂ − v, where v_circ = sqrt(mu/|r|) and t̂ is the normalized horizontal part of v
        /// (perpendicular to r). NaN components when |r| is not positive; zero when the horizontal
        /// speed is below 1e-6 m/s (no defined direction: the caller treats the burn as done or refused).
        /// </summary>
        public static Vector3d DeltaVToCircular(Vector3d r, Vector3d v, double mu)
        {
            double rMag = r.magnitude;
            if (!(rMag > 0))
                return new Vector3d(double.NaN, double.NaN, double.NaN);

            if (!HorizontalDirection(r, v, out Vector3d up, out Vector3d tangent))
                return Vector3d.zero;

            double vCirc = Math.Sqrt(mu / rMag);
            return vCirc * tangent - v;
        }

        /// <summary>
        /// Components of dv along r̂ (radial, positive up) and along t̂ (horizontal, positive in the
        /// direction of travel). Both NaN when |r| is not positive; horizontal is 0 when the
        /// horizontal speed is undefined.
        /// </summary>
        public static void Split(Vector3d dv, Vector3d r, Vector3d v, out double radial, out double horizontal)
        {
            if (!(r.magnitude > 0))
            {
                radial = double.NaN;
                horizontal = double.NaN;
                return;
            }

            bool hasTangent = HorizontalDirection(r, v, out Vector3d up, out Vector3d tangent);
            radial = Vector3d.Dot(dv, up);
            horizontal = hasTangent ? Vector3d.Dot(dv, tangent) : 0;
        }

        // up = r̂; tangent = normalized (v − (v·r̂) r̂). False when the horizontal speed is too small.
        static bool HorizontalDirection(Vector3d r, Vector3d v, out Vector3d up, out Vector3d tangent)
        {
            up = r / r.magnitude;
            Vector3d horizontal = v - Vector3d.Dot(v, up) * up;
            double speed = horizontal.magnitude;
            if (!(speed >= MinHorizontalSpeed))
            {
                tangent = Vector3d.zero;
                return false;
            }
            tangent = horizontal / speed;
            return true;
        }
    }
}
