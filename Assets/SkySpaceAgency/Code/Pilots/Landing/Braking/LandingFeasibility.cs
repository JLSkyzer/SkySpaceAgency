using System.Globalization;

namespace K2D2.Landing.Braking
{
    public struct FeasibilityResult
    {
        public bool Ok;
        public string Error;   // why the start is refused (Ok == false)
        public string Warning; // shown without refusing
    }

    // Checks run when the player starts the landing autopilot. The first failure refuses the
    // start; the order matches the spec's table.
    public static class LandingFeasibility
    {
        public const double TwrMargin = 1.05;
        public const double DeltaVMargin = 1.10;

        public const string NoEngineMessage = "No active engine: stage or activate your engines first.";
        public const string CannotStopMessage = "Cannot stop before the ground with the current thrust.";
        public const string DeltaVUnknownMessage = "Δv unknown: check your fuel.";

        // accel: active thrust / mass (m/s²). brake: braking simulation, null when no collision
        // is predicted yet. deltaVRemaining: the game's Δv for the whole vessel (<= 0 or NaN = unknown).
        public static FeasibilityResult Check(double accel, double surfaceGravity, BrakeResult? brake, double deltaVRemaining)
        {
            if (!(accel > 0))
                return Refuse(NoEngineMessage);

            if (DescentEnvelope.ThrustFraction * accel <= TwrMargin * surfaceGravity)
                return Refuse(string.Format(CultureInfo.InvariantCulture,
                    "Local TWR {0:0.00}: too low to stop safely.", accel / surfaceGravity));

            if (brake == null)
                return new FeasibilityResult { Ok = true };

            if (brake.Value.Status == BrakeStatus.Impossible)
                return Refuse(CannotStopMessage);

            if (!(deltaVRemaining > 0))
                return new FeasibilityResult { Ok = true, Warning = DeltaVUnknownMessage };

            double needed = brake.Value.DeltaVNeeded;
            if (DeltaVMargin * needed > deltaVRemaining)
                return Refuse(string.Format(CultureInfo.InvariantCulture,
                    "Δv {0:0} m/s for ~{1:0} m/s needed.", deltaVRemaining, needed));

            return new FeasibilityResult { Ok = true };
        }

        static FeasibilityResult Refuse(string error) => new FeasibilityResult { Ok = false, Error = error };
    }
}
