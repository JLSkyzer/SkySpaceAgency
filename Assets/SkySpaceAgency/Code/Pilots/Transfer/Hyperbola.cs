using System;
using KSP.Sim;

namespace K2D2.Transfer
{
    // Hyperbolic two-body relations for leaving and entering an SOI (patched conics): the excess
    // velocity v∞ is the asymptote of the hyperbola inside the SOI. Pure code: no game API.
    public static class Hyperbola
    {
        // The two velocities at r whose outgoing asymptote is vInf (direction and magnitude).
        // Both lie in the plane of r and vInf: vA turns about r × vInf (the short way to the
        // asymptote), vB the other way. Returns false, with zero outputs, when r lies on the
        // asymptote line (r parallel to vInf), or for a zero v∞ or a bad input.
        public static bool VelocityForAsymptote(Vector3d r, Vector3d vInf, double mu, out Vector3d vA, out Vector3d vB)
        {
            vA = Vector3d.zero;
            vB = Vector3d.zero;
            double rMag = r.magnitude, vInfMag = vInf.magnitude;
            if (!(mu > 0) || !(rMag > 0) || !(vInfMag > 0) || double.IsInfinity(rMag) || double.IsInfinity(vInfMag))
                return false;

            Vector3d rHat = r / rMag;
            Vector3d sHat = vInf / vInfMag;
            Vector3d normal = Vector3d.Cross(rHat, sHat);
            double sinPhi = normal.magnitude;
            if (sinPhi < 1e-9)
                return false;
            Vector3d hHat = normal / sinPhi;
            double phi = Math.Atan2(sinPhi, Vector3d.Dot(rHat, sHat)); // (0, π)

            if (!Branch(rMag, rHat, hHat, phi, vInfMag, mu, out vA)
                || !Branch(rMag, rHat, -1.0 * hHat, 2 * Math.PI - phi, vInfMag, mu, out vB))
            {
                vA = Vector3d.zero;
                vB = Vector3d.zero;
                return false;
            }
            return true;
        }

        // One hyperbola through r, moving about hHat, whose outgoing asymptote is 'phi' ahead of r
        // (in the direction of motion). With a = μ/v∞², k = sqrt(e² − 1), p = a·k² and the true
        // anomaly ν = ν∞ − phi (cos ν∞ = −1/e), the orbit equation r(1 + e·cos ν) = p becomes
        // a·k² − r·sin(phi)·k − r(1 − cos phi) = 0, whose roots have opposite signs: one hyperbola.
        static bool Branch(double r, Vector3d rHat, Vector3d hHat, double phi, double vInf, double mu, out Vector3d v)
        {
            double a = mu / (vInf * vInf);
            double sinPhi = Math.Sin(phi);
            double oneMinusCos = 2 * Math.Sin(phi / 2) * Math.Sin(phi / 2);
            double k = (r * sinPhi + Math.Sqrt(r * r * sinPhi * sinPhi + 4 * a * r * oneMinusCos)) / (2 * a);
            double e = Math.Sqrt(1 + k * k);
            double p = a * k * k;
            double h = Math.Sqrt(mu * p);
            double nu = Math.Acos(-1 / e) - phi;

            double vRadial = mu / h * e * Math.Sin(nu);
            double vTransverse = h / r;
            v = vRadial * rHat + vTransverse * Vector3d.Cross(hHat, rHat);
            return IsFinite(v) && h > 0;
        }

        // The outgoing excess velocity of a hyperbolic state (the asymptote it leaves along).
        // Zero for a state that is not hyperbolic.
        public static Vector3d Asymptote(Vector3d r, Vector3d v, double mu)
        {
            double rMag = r.magnitude;
            double energy = 0.5 * v.sqrMagnitude - mu / rMag;
            if (!(energy > 0) || !(rMag > 0))
                return Vector3d.zero;
            double vInf = Math.Sqrt(2 * energy);

            Vector3d h = Vector3d.Cross(r, v);
            Vector3d eVec = ((v.sqrMagnitude - mu / rMag) * r - Vector3d.Dot(r, v) * v) / mu;
            double e = eVec.magnitude;
            if (h.magnitude < 1e-9 * rMag * v.magnitude || !(e > 1))
                return vInf * v.normalized; // radial: the motion is already along the asymptote line

            Vector3d p = eVec / e;
            Vector3d q = Vector3d.Cross(h.normalized, p);
            return vInf * ((-1 / e) * p + (Math.Sqrt(e * e - 1) / e) * q);
        }

        // The burn at periapsis rp that turns an arrival at v∞ into an orbit of apoapsis radius ra
        // (ra = rp for a circular orbit).
        public static double CaptureDeltaV(double vInf, double rPeriapsis, double targetApoapsisRadius, double mu)
        {
            double vHyperbola = Math.Sqrt(vInf * vInf + 2 * mu / rPeriapsis);
            double vOrbit = Math.Sqrt(2 * mu / rPeriapsis - 2 * mu / (rPeriapsis + targetApoapsisRadius));
            return vHyperbola - vOrbit;
        }

        internal static bool IsFinite(Vector3d v) =>
            !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z)
            && !double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);
    }
}
