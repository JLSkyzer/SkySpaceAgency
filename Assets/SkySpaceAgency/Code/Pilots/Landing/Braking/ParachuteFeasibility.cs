namespace K2D2.Landing.Braking
{
    // Parachute landing: start checks (no engine checks at all) and the two alerts shown under
    // canopy, where nothing can be done about them since there is no engine.
    public static class ParachuteFeasibility
    {
        public const double CanopyAlertHeight = 500; // m above the ground
        public const double CanopyAlertSpeed = 10;   // m/s down

        public const string NoAtmosphereMessage = "No atmosphere here: parachute landing needs one.";
        public const string NoParachuteMessage = "No parachute on this vessel.";
        public const string DeorbitFirstMessage = "Trajectory stays above the atmosphere: deorbit first.";
        public const string TooFastMessage = "Too fast under canopy";
        public const string NoParachuteLeftMessage = "No parachute left";

        // atmosphereDepth and periapsisAltitude in m above the body radius. usableParachutes:
        // parachutes that are not CUT. The first failure refuses the start, in the spec's order.
        // Inside the atmosphere, the trajectory no longer matters: only it is waived.
        public static FeasibilityResult Check(bool hasAtmosphere, double atmosphereDepth, bool inAtmosphere,
            double periapsisAltitude, int usableParachutes)
        {
            if (!hasAtmosphere)
                return Refuse(NoAtmosphereMessage);
            if (usableParachutes <= 0)
                return Refuse(NoParachuteMessage);
            // !(a < b) also refuses an unknown (NaN) periapsis.
            if (!inAtmosphere && !(periapsisAltitude < atmosphereDepth))
                return Refuse(DeorbitFirstMessage);
            return new FeasibilityResult { Ok = true };
        }

        // At least one parachute DEPLOYED, below CanopyAlertHeight, descending faster than
        // CanopyAlertSpeed.
        public static bool TooFastUnderCanopy(int deployed, double height, double descentSpeed)
        {
            return deployed > 0 && height < CanopyAlertHeight && descentSpeed > CanopyAlertSpeed;
        }

        // Every parachute is CUT or gone while the vessel still flies.
        public static bool NoParachuteLeft(int usable, bool landedOrSplashed)
        {
            return usable <= 0 && !landedOrSplashed;
        }

        static FeasibilityResult Refuse(string error) => new FeasibilityResult { Ok = false, Error = error };
    }
}
