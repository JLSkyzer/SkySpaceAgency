using System;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace SkySpaceAgency.Landing
{
    // The targeting helpers K2-D2 1.4.0's boost-back needs (IanMealworm, LandingTargeting.cs),
    // with their upstream names. Everything here works in ImpactModel's axes (the body frame at the
    // model's epoch), never with the Zup vectors of Precision/.
    //
    // Boost-back is a K2-D2 feature: compiled and tested here, flown only in the K2-D2 flavour
    // (ModIdentity.IsK2D2, see BoostBackStart).
    public static partial class LandingTargeting
    {
        public static double WrapDegrees180(double deg)
        {
            deg %= 360.0;
            if (deg > 180.0) deg -= 360.0;
            if (deg < -180.0) deg += 360.0;
            return deg;
        }

        // The point the coast is aimed at: aim_height above the target's terrain. Upstream aims a
        // landing from orbit a few km above the target; the boost-back aims at the ground (0).
        public class TargetSite
        {
            public readonly Vector3d point;        // body-fixed aim point, aim_height above the target
            public readonly double radius;         // distance of the aim point from the body's center
            public readonly double ground_radius;  // distance of the target's terrain from the body's center
            public readonly double aim_height;
            readonly Vector3d up, axis1, axis2;

            // aim_height_m: how high over the target the coast is aimed; by default a few percent
            // of the body's radius (see AimHeight).
            public TargetSite(ImpactModel model, double latitude, double longitude, double aim_height_m = double.NaN)
            {
                up = model.SurfaceUp(latitude, longitude);
                ground_radius = model.TerrainRadius(latitude, longitude);
                aim_height = double.IsNaN(aim_height_m) ? AimHeight(model.radius) : aim_height_m;
                radius = ground_radius + aim_height;
                point = up * radius;

                Vector3d reference = Math.Abs(up.y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0);
                axis1 = Vector3d.Cross(up, reference).normalized;
                axis2 = Vector3d.Cross(up, axis1);
            }

            // Where a body-fixed point is relative to the target, as distances along the ground in
            // two perpendicular directions - the length of (a, b) is the distance over the surface.
            // Measured as an angle around the body rather than a straight-line offset, so a point
            // on the far side of the body reads as far away instead of folding back onto the target.
            public void Offset(Vector3d body_fixed, out double a, out double b)
            {
                Vector3d d = body_fixed.normalized;
                double x = Vector3d.Dot(d, axis1), y = Vector3d.Dot(d, axis2);
                double sideways = Math.Sqrt(x * x + y * y);
                if (sideways < 1e-12)
                {
                    a = b = 0;
                    return;
                }
                double distance = radius * Math.Atan2(sideways, Vector3d.Dot(d, up));
                a = distance * x / sideways;
                b = distance * y / sideways;
            }
        }

        // A few percent of the body's radius: about 6 km on the Mun, 2 km on Minmus.
        static double AimHeight(double body_radius) => Math.Max(2000, Math.Min(10000, 0.03 * body_radius));

        public static Vector3d AfterBurn(Vector3d r, Vector3d v, double prograde, double normal, double radial = 0)
        {
            Vector3d along = v.normalized;
            Vector3d after = v + along * prograde + Vector3d.Cross(r, v).normalized * normal;
            if (radial != 0)
                after += (r - along * Vector3d.Dot(r, along)).normalized * radial;
            return after;
        }

        // Where a burn of (prograde, normal) from (r, v) at UT makes the coast come down,
        // relative to the target.
        public static bool Miss(ImpactModel model, TargetSite site, Vector3d r, Vector3d v, double UT,
            double prograde, double normal, out double a, out double b, out double impact_UT, out Vector3d impact,
            double radial = 0)
        {
            a = b = 0;
            impact = Vector3d.zero;
            if (!model.Impact(r, AfterBurn(r, v, prograde, normal, radial), UT, site.radius, out impact_UT, out Vector3d p))
                return false;

            impact = model.BodyFixed(p, impact_UT);
            site.Offset(impact, out a, out b);
            return true;
        }
    }
}
