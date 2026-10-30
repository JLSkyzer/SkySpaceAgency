using System;
using System.Globalization;

namespace K2D2.Landing.Heating
{
    public enum HeatGuardEvent { None, Started, Stopped }

    // The live heat guard's decision, every frame of an atmospheric descent: from On (75 %) of the
    // hottest part's max temperature, above Mach 1, thrust surface retrograde (the reentry flux grows
    // with the cube of the air speed), from MinThrottle at On to full at Full (85 %), only while
    // aligned within MaxAngleDeg; stop under Off (70 %) or below Mach 1, where the game gives no
    // reentry flux (ExposedAreaProportion 0, read in the IL). Ceiling (90 %) is the level it defends:
    // above it at full throttle it raises the alarm. Pure state machine, tested in EditMode.
    public class HeatGuard
    {
        public const double On = 0.75;
        public const double Off = 0.70;
        public const double Full = 0.85;
        public const double Ceiling = 0.90;
        public const double MinThrottle = 0.25;
        public const double MaxAngleDeg = 15;

        public bool Active { get; private set; }
        /// <summary>Wanted throttle (0..1) while Active and aligned; 0 otherwise.</summary>
        public float Throttle { get; private set; }
        /// <summary>Above Ceiling while thrusting at full throttle (or without thrust).</summary>
        public bool Overheating { get; private set; }
        public bool NoThrust { get; private set; }
        public double PeakRatio { get; private set; }

        public void Reset()
        {
            Active = false;
            Throttle = 0;
            Overheating = false;
            NoThrust = false;
            PeakRatio = 0;
        }

        /// <param name="ratio">Hottest part's Temperature / MaxTemp.</param>
        /// <param name="mach">The vessel's Mach number.</param>
        /// <param name="angleDeg">Angle between the vessel's thrust axis and surface retrograde.</param>
        /// <param name="hasThrust">An active engine can push.</param>
        public HeatGuardEvent Update(double ratio, double mach, double angleDeg, bool hasThrust)
        {
            if (double.IsNaN(ratio))
                ratio = 0;
            PeakRatio = Math.Max(PeakRatio, ratio);
            HeatGuardEvent ev = HeatGuardEvent.None;
            bool supersonic = mach >= 1;
            if (!Active && supersonic && ratio >= On)
            {
                Active = true;
                ev = HeatGuardEvent.Started;
            }
            else if (Active && (!supersonic || ratio < Off))
            {
                Active = false;
                ev = HeatGuardEvent.Stopped;
            }

            float wanted = Active ? (float)ThrottleFor(ratio) : 0f;
            NoThrust = Active && !hasThrust;
            Overheating = ratio > Ceiling && (NoThrust || wanted >= 1f);
            bool aligned = !double.IsNaN(angleDeg) && angleDeg <= MaxAngleDeg;
            Throttle = Active && hasThrust && aligned ? wanted : 0f;
            return ev;
        }

        public static double ThrottleFor(double ratio)
        {
            if (ratio >= Full)
                return 1;
            double f = (ratio - On) / (Full - On);
            return Math.Max(MinThrottle, Math.Min(1, MinThrottle + f * (1 - MinThrottle)));
        }

        public static string OverheatMessage(string part, double ratio) => string.Format(CultureInfo.InvariantCulture,
            "Overheating: {0} at {1:0} % of its max temperature, cannot slow down enough.", part ?? "a part", ratio * 100);

        public const string NoThrustMessage = "Heat guard: no thrust left to slow down.";
    }
}
