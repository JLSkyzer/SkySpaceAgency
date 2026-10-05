using System;
using KSP.Sim;

namespace K2D2.OrbitPlanning
{
    // Two-body orbital elements from a state vector (position and velocity relative to the body,
    // in the game's "Zup" frame where z is the body's rotation axis).
    public struct OrbitElements
    {
        public double SemiMajorAxis;
        public double Eccentricity;
        public Vector3d EccentricityVector;
        public Vector3d AngularMomentum;
        public double ApoapsisRadius;
        public double PeriapsisRadius;
        public double InclinationRad;
        public double Period;
        public bool IsClosed => SemiMajorAxis > 0 && Eccentricity < 1;
    }

    // Pure orbital math on plain vectors: no game state, so it can be unit-tested in the editor.
    public static class OrbitMath
    {
        public static readonly Vector3d North = new Vector3d(0, 0, 1);

        public static OrbitElements Elements(Vector3d r, Vector3d v, double mu)
        {
            double rMag = r.magnitude;
            double vMag = v.magnitude;
            var el = new OrbitElements();
            el.SemiMajorAxis = 1.0 / (2.0 / rMag - vMag * vMag / mu);
            el.AngularMomentum = Vector3d.Cross(r, v);
            el.EccentricityVector = ((vMag * vMag - mu / rMag) * r - Vector3d.Dot(r, v) * v) / mu;
            el.Eccentricity = el.EccentricityVector.magnitude;
            el.ApoapsisRadius = el.SemiMajorAxis * (1 + el.Eccentricity);
            el.PeriapsisRadius = el.SemiMajorAxis * (1 - el.Eccentricity);
            el.InclinationRad = Math.Acos(Clamp(el.AngularMomentum.z / el.AngularMomentum.magnitude));
            el.Period = el.IsClosed
                ? 2 * Math.PI * Math.Sqrt(el.SemiMajorAxis * el.SemiMajorAxis * el.SemiMajorAxis / mu)
                : double.PositiveInfinity;
            return el;
        }

        // Angle in [0, 2π) from 'from' to 'to', both in the orbital plane, in the direction of motion (about h).
        public static double PlaneAngle(Vector3d from, Vector3d to, Vector3d h)
        {
            Vector3d a = from.normalized;
            Vector3d b = to.normalized;
            double angle = Math.Atan2(Vector3d.Dot(Vector3d.Cross(a, b), h.normalized), Vector3d.Dot(a, b));
            return angle < 0 ? angle + 2 * Math.PI : angle;
        }

        // Seconds until the vessel reaches the in-plane direction 'dir' (Kepler's equation). On a
        // practically circular orbit the angle is taken from the current position at the mean motion.
        public static double TimeToDirection(Vector3d r, Vector3d v, double mu, Vector3d dir)
        {
            var el = Elements(r, v, mu);
            double meanMotion = Math.Sqrt(mu / (el.SemiMajorAxis * el.SemiMajorAxis * el.SemiMajorAxis));
            if (el.Eccentricity < 1e-6)
                return PlaneAngle(r, dir, el.AngularMomentum) / meanMotion;

            double nu0 = PlaneAngle(el.EccentricityVector, r, el.AngularMomentum);
            double nu1 = PlaneAngle(el.EccentricityVector, dir, el.AngularMomentum);
            double dM = MeanAnomaly(nu1, el.Eccentricity) - MeanAnomaly(nu0, el.Eccentricity);
            if (dM < 0)
                dM += 2 * Math.PI;
            return dM / meanMotion;
        }

        // Seconds until true anomaly 'nu' (0 = periapsis, π = apoapsis). Needs a non-circular orbit.
        public static double TimeToTrueAnomaly(Vector3d r, Vector3d v, double mu, double nu)
        {
            var el = Elements(r, v, mu);
            Vector3d e = el.EccentricityVector.normalized;
            Vector3d q = Vector3d.Cross(el.AngularMomentum.normalized, e);
            return TimeToDirection(r, v, mu, Math.Cos(nu) * e + Math.Sin(nu) * q);
        }

        // Orbit radius where the vessel crosses the in-plane direction 'dir'.
        public static double RadiusAtDirection(OrbitElements el, double mu, Vector3d dir)
        {
            double p = el.AngularMomentum.sqrMagnitude / mu;
            if (el.Eccentricity < 1e-6)
                return p;
            double nu = PlaneAngle(el.EccentricityVector, dir, el.AngularMomentum);
            return p / (1 + el.Eccentricity * Math.Cos(nu));
        }

        // Rodrigues rotation of v about a unit axis.
        public static Vector3d Rotate(Vector3d v, Vector3d axisUnit, double angle)
        {
            double c = Math.Cos(angle);
            double s = Math.Sin(angle);
            return c * v + s * Vector3d.Cross(axisUnit, v) + (Vector3d.Dot(axisUnit, v) * (1 - c)) * axisUnit;
        }

        static double MeanAnomaly(double nu, double e)
        {
            double E = 2 * Math.Atan2(Math.Sqrt(1 - e) * Math.Sin(nu / 2), Math.Sqrt(1 + e) * Math.Cos(nu / 2));
            double M = E - e * Math.Sin(E);
            return M < 0 ? M + 2 * Math.PI : M;
        }

        static double Clamp(double x) => Math.Max(-1, Math.Min(1, x));
    }
}
