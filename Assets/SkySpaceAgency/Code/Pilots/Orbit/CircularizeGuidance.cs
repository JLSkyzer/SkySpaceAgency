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
        /// (perpendicular to r): OrbitGuidance.DeltaVToOrbit toward the circular orbit of radius |r|
        /// in the current plane. NaN components when |r| is not positive; zero when the horizontal
        /// speed is below 1e-6 m/s (no defined direction: the caller treats the burn as done or refused).
        /// </summary>
        public static Vector3d DeltaVToCircular(Vector3d r, Vector3d v, double mu)
        {
            double rMag = r.magnitude;
            if (!(rMag > 0))
                return new Vector3d(double.NaN, double.NaN, double.NaN);

            Vector3d up = r / rMag;
            Vector3d horizontal = v - Vector3d.Dot(v, up) * up;
            if (!(horizontal.magnitude >= MinHorizontalSpeed))
                return Vector3d.zero;

            return OrbitGuidance.DeltaVToOrbit(r, v, mu, rMag, rMag, Vector3d.Cross(r, v));
        }

        /// <summary>
        /// Components of dv along r̂ (radial, positive up) and along t̂ (horizontal, positive in the
        /// direction of travel); see OrbitGuidance.Split. Both NaN when |r| is not positive;
        /// horizontal is 0 when the horizontal speed is undefined.
        /// </summary>
        public static void Split(Vector3d dv, Vector3d r, Vector3d v, out double radial, out double horizontal)
        {
            OrbitGuidance.Split(dv, r, v, out radial, out horizontal, out _);
        }
    }
}
