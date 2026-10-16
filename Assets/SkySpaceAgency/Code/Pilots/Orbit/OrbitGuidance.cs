using System;
using KSP.Sim;

namespace K2D2.OrbitPlanning
{
    /// <summary>A target orbit: apsis radii (m, Ra ≥ Rp) and the unit normal of its plane (Zup frame).</summary>
    public struct TargetOrbit
    {
        public double Ra;
        public double Rp;
        public Vector3d PlaneNormal;

        /// <summary>The orbit through the state (r, v): its apsis radii and its unit normal r × v.</summary>
        public static TargetOrbit FromState(Vector3d r, Vector3d v, double mu)
        {
            var el = OrbitMath.Elements(r, v, mu);
            return new TargetOrbit
            {
                Ra = el.ApoapsisRadius,
                Rp = el.PeriapsisRadius,
                PlaneNormal = el.AngularMomentum.normalized
            };
        }

        public double InclinationRad => Math.Acos(Math.Max(-1, Math.Min(1, PlaneNormal.z / PlaneNormal.magnitude)));
    }

    /// <summary>
    /// Closed-loop guidance toward a target orbit: the velocity change that puts the vessel, from
    /// where it is now, on the orbit of apsis radii (ra, rp) in the plane of a given normal. A
    /// controller re-evaluates it every frame and steers along it until it vanishes, so the result
    /// does not depend on the game's finite-burn model (see TargetOrbitBurn). Pure maths, tested
    /// in the EditMode tests. All vectors are in the Zup frame.
    /// </summary>
    public static class OrbitGuidance
    {
        // Below this radial speed (m/s) its sign is meaningless: the side is chosen from the radius.
        const double MinRadialSpeed = 1.0;
        // Below this horizontal speed (m/s) the current direction of travel is undefined.
        const double MinHorizontalSpeed = 1e-6;

        static readonly Vector3d NaN = new Vector3d(double.NaN, double.NaN, double.NaN);

        /// <summary>
        /// target − v, where target = v_r·r̂ + v_t·t̂ is the velocity of the target orbit at the
        /// current radius: a = (ra + rp)/2, h = sqrt(mu·2·ra·rp/(ra + rp)), v_t = h/|r|,
        /// v_r = ±sqrt(v² − v_t²) with v² from vis-viva. t̂ = normalize(n × r̂), with n flipped when
        /// t̂ would point against the current horizontal velocity (the orbit keeps its direction of
        /// motion). The sign of v_r is radialSign when it is ±1 (fixed by the caller, at ignition),
        /// otherwise RadialSign of the current state. A radius outside [rp, ra] by more than the
        /// tolerance (see IsReachable) widens the range to it (ra' = max(ra, |r|), rp' =
        /// min(rp, |r|)): the vessel becomes an apsis there and the other apsis is kept, the best
        /// effort from a place the target orbit does not pass through. ra and rp may come in
        /// either order. NaN components when |r| or n is zero.
        /// </summary>
        public static Vector3d DeltaVToOrbit(Vector3d r, Vector3d v, double mu, double ra, double rp, Vector3d planeNormal,
            int radialSign = 0)
        {
            double rMag = r.magnitude;
            double nMag = planeNormal.magnitude;
            if (!(rMag > 0) || !(nMag > 0))
                return NaN;
            if (ra < rp)
            {
                double swap = ra;
                ra = rp;
                rp = swap;
            }

            Vector3d up = r / rMag;
            Vector3d n = planeNormal / nMag;
            Vector3d tangent = Vector3d.Cross(n, up);
            double tangentMag = tangent.magnitude;
            if (!(tangentMag > 0))
                return NaN;   // the radius is along the plane normal: no velocity of that plane passes here
            tangent = tangent / tangentMag;

            double radialSpeed = Vector3d.Dot(v, up);
            Vector3d horizontalVelocity = v - radialSpeed * up;
            if (Vector3d.Dot(tangent, horizontalVelocity) < 0)
                tangent = -1.0 * tangent;

            if (!IsReachable(rMag, ra, rp))
            {
                ra = Math.Max(ra, rMag);
                rp = Math.Min(rp, rMag);
            }
            double radius = rMag;

            double a = (ra + rp) / 2;
            double h = Math.Sqrt(mu * 2 * ra * rp / (ra + rp));
            double vt = h / radius;
            // v² − v_t² from vis-viva, rewritten as mu·(ra − r)(r − rp)/(a·r²): the same value,
            // exactly 0 at both apsides (no cancellation on a circular target).
            double vr2 = mu * (ra - radius) * (radius - rp) / (a * radius * radius);
            int sign = radialSign == 1 || radialSign == -1 ? radialSign : RadialSign(r, v, ra, rp);
            double vr = sign * Math.Sqrt(Math.Max(0, vr2));

            return vr * up + vt * tangent - v;
        }

        public static Vector3d DeltaVToOrbit(Vector3d r, Vector3d v, double mu, TargetOrbit target, int radialSign = 0) =>
            DeltaVToOrbit(r, v, mu, target.Ra, target.Rp, target.PlaneNormal, radialSign);

        /// <summary>
        /// Sign of the target radial speed from the state: that of the current radial speed, or,
        /// under 1 m/s, +1 when |r| is closer to rp than to ra (about to climb), −1 otherwise.
        /// </summary>
        public static int RadialSign(Vector3d r, Vector3d v, double ra, double rp)
        {
            double rMag = r.magnitude;
            double radialSpeed = rMag > 0 ? Vector3d.Dot(v, r) / rMag : 0;
            if (Math.Abs(radialSpeed) >= MinRadialSpeed)
                return Math.Sign(radialSpeed);
            double high = Math.Max(ra, rp);
            double low = Math.Min(ra, rp);
            return Math.Abs(rMag - low) <= Math.Abs(high - rMag) ? 1 : -1;
        }

        /// <summary>
        /// True when the radius lies in [rp, ra] up to max(100 m, 0.1 %) (same tolerance as
        /// OrbitPlanner.SameRadius): the target orbit passes through it.
        /// </summary>
        public static bool IsReachable(double radius, double ra, double rp)
        {
            double high = Math.Max(ra, rp);
            double low = Math.Min(ra, rp);
            return radius >= low - Tolerance(low) && radius <= high + Tolerance(high);
        }

        static double Tolerance(double radius) => Math.Max(100, 0.001 * radius);

        /// <summary>
        /// Components of dv in the current frame: along r̂ (radial, positive up), along t̂ (the
        /// horizontal part of v, positive in the direction of travel) and along r̂ × t̂ (the current
        /// orbit normal r × v). All NaN when |r| is not positive; horizontal and normal are 0 when
        /// the horizontal speed is undefined.
        /// </summary>
        public static void Split(Vector3d dv, Vector3d r, Vector3d v, out double radial, out double horizontal, out double normal)
        {
            double rMag = r.magnitude;
            if (!(rMag > 0))
            {
                radial = horizontal = normal = double.NaN;
                return;
            }

            Vector3d up = r / rMag;
            radial = Vector3d.Dot(dv, up);

            Vector3d horizontalVelocity = v - Vector3d.Dot(v, up) * up;
            double speed = horizontalVelocity.magnitude;
            if (!(speed >= MinHorizontalSpeed))
            {
                horizontal = normal = 0;
                return;
            }
            Vector3d tangent = horizontalVelocity / speed;
            horizontal = Vector3d.Dot(dv, tangent);
            normal = Vector3d.Dot(dv, Vector3d.Cross(up, tangent));
        }

        /// <summary>
        /// Rebuilds a direction from its Split components in one of the game's frames, given that
        /// frame's local up (r̂) and horizontal forward (t̂). The game's frames are the Zup frame
        /// with Y and Z swapped (a reflection), then rotated, so the Zup normal r̂ × t̂ becomes
        /// forward × up there, not up × forward. Normalized; zero when all components are 0.
        /// </summary>
        public static Vector3d AimInFrame(double radial, double horizontal, double normal, Vector3d up, Vector3d forward)
        {
            Vector3d aim = radial * up + horizontal * forward + normal * Vector3d.Cross(forward, up);
            double mag = aim.magnitude;
            return mag > 0 ? aim / mag : Vector3d.zero;
        }
    }
}
