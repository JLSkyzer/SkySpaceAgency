using System;
using KSP.Sim;

namespace K2D2.Landing.Precision
{
    // Surface geometry on a rotating body, in the body-relative inertial Zup frame (z = rotation
    // axis, latitude measured from the equatorial plane). Pure maths, tested in EditMode.
    public static class SurfaceGeometry
    {
        const double Deg = Math.PI / 180;

        // A known surface point (the vessel now) with its game latitude/longitude. It fixes the
        // body-fixed axes: longitude is measured from it about z, with the measured sign.
        public struct Reference
        {
            public Vector3d Position;  // inertial, at the reference time
            public double Latitude;    // degrees
            public double Longitude;   // degrees
            public int LonSign;        // +1: longitude grows with a positive rotation about z
        }

        static double Azimuth(Vector3d v) => Math.Atan2(v.y, v.x);

        // Unit inertial vector, at the reference time, of the surface point (latDeg, lonDeg).
        public static Vector3d Direction(Reference reference, double latDeg, double lonDeg)
        {
            double phi = Azimuth(reference.Position) + reference.LonSign * (lonDeg - reference.Longitude) * Deg;
            double lat = latDeg * Deg;
            double c = Math.Cos(lat);
            return new Vector3d(c * Math.Cos(phi), c * Math.Sin(phi), Math.Sin(lat));
        }

        // Inverse of Direction; longitude wrapped to [-180, 180).
        public static void LatLon(Reference reference, Vector3d position, out double latDeg, out double lonDeg)
        {
            latDeg = Math.Atan2(position.z, Math.Sqrt(position.x * position.x + position.y * position.y)) / Deg;
            double dPhi = Azimuth(position) - Azimuth(reference.Position);
            lonDeg = WrapDeg(reference.Longitude + reference.LonSign * dPhi / Deg);
        }

        static double WrapDeg(double d)
        {
            d = (d + 180) % 360;
            if (d < 0) d += 360;
            return d - 180;
        }

        // Where a body-fixed point is dt seconds later: rotated by omega * dt about z.
        public static Vector3d AtTime(Vector3d positionNow, Vector3d omega, double dt)
        {
            double a = omega.z * dt;
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Vector3d(c * positionNow.x - s * positionNow.y,
                                s * positionNow.x + c * positionNow.y,
                                positionNow.z);
        }

        // Metres between two directions, along the surface.
        public static double GreatCircle(Vector3d a, Vector3d b, double radius)
        {
            return radius * Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b));
        }

        // Miss of `to` relative to `from`, split along the ground track (positive: ahead) and
        // across it (positive: left, along from x groundTrackDir), in metres.
        public static void Split(Vector3d from, Vector3d to, Vector3d groundTrackDir, double radius,
            out double alongM, out double crossM)
        {
            Vector3d n = from / from.magnitude;
            Vector3d t = groundTrackDir - Vector3d.Dot(groundTrackDir, n) * n;
            t = t / t.magnitude;
            Vector3d left = Vector3d.Cross(n, t);
            double x = Vector3d.Dot(to, t);
            double y = Vector3d.Dot(to, left);
            double z = Vector3d.Dot(to, n);
            alongM = radius * Math.Atan2(x, z);
            crossM = radius * Math.Atan2(y, Math.Sqrt(x * x + z * z));
        }
    }
}
