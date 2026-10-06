using System;

namespace K2D2.OrbitPlanning
{
    /// <summary>
    /// Timing of a finite burn around an impulsive instant. The game treats a node's Time as the
    /// START of the burn (ManeuverNodeData.Time: "Universal time at which the burn begins"), so a
    /// burn meant to be effective at an apsis has to start half its duration earlier. Pure maths,
    /// tested in the EditMode tests.
    /// </summary>
    public static class BurnTiming
    {
        const double G0 = 9.80665;

        /// <summary>
        /// Duration in seconds of a burn of deltaV (m/s) at constant thrust (kN) from an initial
        /// mass (t). With isp (s) > 0 the mass loss is accounted for (Tsiolkovsky); otherwise the
        /// mass is taken as constant. NaN when thrust or mass is not positive or an input is NaN.
        /// </summary>
        public static double Duration(double deltaV, double thrustKN, double massT, double isp)
        {
            if (double.IsNaN(deltaV) || double.IsNaN(thrustKN) || double.IsNaN(massT) || double.IsNaN(isp))
                return double.NaN;
            if (thrustKN <= 0 || massT <= 0)
                return double.NaN;

            double dv = Math.Abs(deltaV);
            // kN / t = m/s^2, so m0/F is directly seconds per (m/s).
            if (isp > 0)
            {
                double ve = isp * G0;
                return massT * ve / thrustKN * (1 - Math.Exp(-dv / ve));
            }
            return dv * massT / thrustKN;
        }

        /// <summary>
        /// Node start time for a burn centered on impulseUT: impulseUT - duration/2, never before
        /// earliestUT. A NaN duration (no thrust information) keeps the impulse time unchanged.
        /// </summary>
        public static double CenteredStart(double impulseUT, double duration, double earliestUT)
        {
            if (double.IsNaN(duration))
                return impulseUT;
            return Math.Max(impulseUT - duration / 2, earliestUT);
        }
    }
}
