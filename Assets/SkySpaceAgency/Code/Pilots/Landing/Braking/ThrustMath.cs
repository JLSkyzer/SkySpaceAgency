using System.Collections.Generic;

namespace K2D2.Landing.Braking
{
    // Engine arithmetic shared by BurndV and the braking simulation. Thrust in kN and mass in t,
    // so thrust / mass is in m/s², like the rest of the mod.
    public static class ThrustMath
    {
        // 0 instead of NaN or infinity when there is no thrust or no mass.
        public static double Acceleration(double thrustKN, double massT)
        {
            if (!(thrustKN > 0) || !(massT > 0) || double.IsInfinity(thrustKN) || double.IsInfinity(massT))
                return 0;
            return thrustKN / massT;
        }

        // Isp of several engines firing together: total thrust over total propellant flow.
        // Engines without thrust are ignored; a thrusting engine with an unknown Isp makes the
        // result unknown (0), and the caller then keeps the mass constant, which is pessimistic.
        public static double CombinedIsp(IReadOnlyList<double> thrustsKN, IReadOnlyList<double> isps)
        {
            double thrust = 0, flow = 0;
            for (int i = 0; i < thrustsKN.Count; i++)
            {
                double f = thrustsKN[i];
                if (!(f > 0))
                    continue;
                double isp = isps[i];
                if (!(isp > 0))
                    return 0;
                thrust += f;
                flow += f / isp;
            }
            return flow > 0 ? thrust / flow : 0;
        }
    }
}
