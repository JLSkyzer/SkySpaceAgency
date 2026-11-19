using System;
using System.Globalization;
using KSP.Sim;

namespace SkySpaceAgency.Transfer
{
    // A flyby in the patched-conic picture (4.6): the excess arrives as vInf (in) and must leave as
    // vInf (out), both relative to the flyby body. An unpowered flyby turns the excess without
    // changing its size; a powered one burns along the motion at the periapsis, where the two
    // hyperbolas (in and out) meet. Not ok (Δv +∞) when no periapsis between minRadius and maxRadius
    // gives the turn. Pure code.
    public struct FlybySolution
    {
        public bool Ok;
        public double PeriapsisRadius, TurnDeg, DeltaV;
        public Vector3d Normal; // unit, normalize(vin × vout), the frame of the inputs
        public string Why;
    }

    public static class FlybyModel
    {
        // The periapsis is at most this share of the SOI; a burn under MinBurn (m/s) is not flown.
        public const double MaxPeriapsisShare = 0.5, MinBurn = 2;

        const double Rad = 180 / Math.PI;

        // The turn (°) of a flyby at periapsis rp: each half of it from its own hyperbola.
        public static double TurnDeg(double rp, double vInfIn, double vInfOut, double mu) =>
            (Math.Asin(1 / (1 + rp * vInfIn * vInfIn / mu)) + Math.Asin(1 / (1 + rp * vInfOut * vInfOut / mu))) * Rad;

        public static FlybySolution Solve(Vector3d vInfIn, Vector3d vInfOut, double mu, double minRadius, double maxRadius)
        {
            var f = new FlybySolution { DeltaV = double.PositiveInfinity, PeriapsisRadius = double.NaN, Why = "" };
            double vin = vInfIn.magnitude, vout = vInfOut.magnitude;
            var cross = Vector3d.Cross(vInfIn, vInfOut);
            double turn = Math.Atan2(cross.magnitude, Vector3d.Dot(vInfIn, vInfOut)) * Rad;
            f.TurnDeg = turn;
            if (!(vin > 0) || !(vout > 0) || !(cross.magnitude >= 1e-9 * vin * vout))
            {
                f.Why = "no plane: in and out excess aligned";
                return f;
            }
            f.Normal = cross / cross.magnitude;
            double max = TurnDeg(minRadius, vin, vout, mu);
            if (max < turn)
            {
                f.Why = string.Format(CultureInfo.InvariantCulture, "turn {0:F1}° above the {1:F1}° possible over the floor", turn, max);
                return f;
            }
            if (TurnDeg(maxRadius, vin, vout, mu) > turn)
            {
                f.Why = string.Format(CultureInfo.InvariantCulture, "turn {0:F2}° too small: the periapsis would be beyond half the SOI", turn);
                return f;
            }
            double lo = minRadius, hi = maxRadius; // the turn decreases with rp
            for (int i = 0; i < 200 && hi - lo > 1; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (TurnDeg(mid, vin, vout, mu) > turn) lo = mid; else hi = mid;
            }
            double rp = 0.5 * (lo + hi);
            f.Ok = true;
            f.PeriapsisRadius = rp;
            f.DeltaV = Math.Abs(Math.Sqrt(vout * vout + 2 * mu / rp) - Math.Sqrt(vin * vin + 2 * mu / rp));
            return f;
        }
    }
}
