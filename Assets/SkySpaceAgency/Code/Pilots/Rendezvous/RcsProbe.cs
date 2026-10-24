using System;
using KSP.Sim;

namespace K2D2.Rendezvous
{
    // Which vessel axis each RCS input (FlightCtrlState X, Y, Z) really moves the vessel along,
    // measured by a probe that pulses each input and reads the velocity change. Never assumed:
    // the old assistant's comment said the axes were wrong.
    // Vessel axes: 0 = right, 1 = up, 2 = forward (the control part's frame).
    // Pure code, tested in EditMode.
    public class RcsMapping
    {
        public const int Right = 0, Up = 1, Forward = 2;
        public const double MinDominance = 0.8;   // the matched axis carries at least 80 % of an input's Δv
        public const double DeadFraction = 0.1;   // under 10 % of the strongest input's Δv: dead
        static readonly string[] InputNames = { "X", "Y", "Z" };
        static readonly string[] AxisNames = { "right", "up", "forward" };

        public bool Ok;
        public string Refusal;   // null when Ok
        public int[] AxisOf;     // per input (X, Y, Z): the vessel axis it moves along, -1 when dead
        public int[] Sign;       // per input: +1 when a positive input moves the vessel along +axis, -1 the other way, 0 when dead
        public bool[] Dead;      // per input: its Δv is under DeadFraction of the strongest, under minResponse, or unreadable

        // dvX..dvZ: the velocity change measured for a positive pulse on each input, inertial.
        // vesselRight/Up/Forward: the vessel's axes, inertial, at the time of the pulses.
        // minResponse: the noise floor of the measurement, m/s.
        // A dead input is not a refusal: the caller falls back to the main engine for that axis
        // or refuses (Review Focus 4). Refused: no input responds, a live input whose Δv is not
        // mostly along one axis (MinDominance), and two live inputs on the same axis.
        public static RcsMapping FromResponses(Vector3d dvX, Vector3d dvY, Vector3d dvZ,
            Vector3d vesselRight, Vector3d vesselUp, Vector3d vesselForward, double minResponse)
        {
            var m = new RcsMapping { AxisOf = new int[3], Sign = new int[3], Dead = new bool[3] };
            var dv = new[] { dvX, dvY, dvZ };
            var axes = new[] { Unit(vesselRight), Unit(vesselUp), Unit(vesselForward) };
            if (axes[0].sqrMagnitude == 0 || axes[1].sqrMagnitude == 0 || axes[2].sqrMagnitude == 0)
                return Refuse(m, "the vessel's axes are unreadable");

            double strongest = 0;
            for (int i = 0; i < 3; i++)
                if (Finite(dv[i])) strongest = Math.Max(strongest, dv[i].magnitude);
            // Dead: under DeadFraction of the strongest response (an input that barely moves the
            // vessel next to the others), or under minResponse (the measurement noise floor).
            double deadBelow = Math.Max(minResponse, DeadFraction * strongest);

            for (int i = 0; i < 3; i++)
            {
                m.AxisOf[i] = -1;
                if (!Finite(dv[i]) || !(dv[i].magnitude >= deadBelow) || dv[i].magnitude == 0)
                {
                    m.Dead[i] = true;
                    continue;
                }
                int best = 0;
                double bestDot = 0;
                for (int k = 0; k < 3; k++)
                {
                    double d = Vector3d.Dot(dv[i], axes[k]);
                    if (Math.Abs(d) > Math.Abs(bestDot)) { best = k; bestDot = d; }
                }
                m.AxisOf[i] = best;
                m.Sign[i] = bestDot >= 0 ? 1 : -1;
            }

            if (m.Dead[0] && m.Dead[1] && m.Dead[2])
                return Refuse(m, "no RCS response on any axis");

            for (int i = 0; i < 3; i++)
            {
                if (m.Dead[i]) continue;
                double share = Math.Abs(Vector3d.Dot(dv[i], axes[m.AxisOf[i]])) / dv[i].magnitude;
                if (share < MinDominance)
                    return Refuse(m, $"input {InputNames[i]} moves the vessel along no single axis ({share * 100:F0} % along its {AxisNames[m.AxisOf[i]]} axis)");
            }

            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    if (!m.Dead[i] && !m.Dead[j] && m.AxisOf[i] == m.AxisOf[j])
                        return Refuse(m, $"inputs {InputNames[i]} and {InputNames[j]} both move the vessel along its {AxisNames[m.AxisOf[i]]} axis");

            m.Ok = true;
            return m;
        }

        static RcsMapping Refuse(RcsMapping m, string why)
        {
            m.Ok = false;
            m.Refusal = why;
            return m;
        }

        static Vector3d Unit(Vector3d v) => Finite(v) && v.magnitude > 1e-9 ? v.normalized : Vector3d.zero;

        static bool Finite(Vector3d v)
        {
            double sum = v.x + v.y + v.z;
            return !double.IsNaN(sum) && !double.IsInfinity(sum);
        }
    }
}
