using System.Globalization;

namespace K2D2.Landing
{
    public enum BoostBackDecision
    {
        // Not a boost-back: the landing starts as it does without it (in this mod, always).
        NotBoostBack,
        // Fly the boost-back: BoostBack -> AtmoReentry -> Brake (AtmoLandingBurn) -> Touch Down.
        BoostBack,
        // A boost-back was asked for but cannot start; the reason names the failed check.
        Refused,
    }

    // What the start rule looks at, read by LandingPilot.CheckCanStartBoostBack.
    public struct BoostBackState
    {
        public bool atmospheric_profile;     // LandingProfile.IsAtmospheric
        public LandingMethod method;         // land.mode of the Atmo profile
        public bool precision;               // land.precision_landing of the Atmo profile
        public bool landed;                  // LandedOrSplashed (on the pad, say)
        public bool staged;                  // a landing stage is chosen (Land with stage)
        public bool suborbital;              // periapsis below the body's radius
        public double altitude_m;            // above sea level
        public double atmosphere_depth_m;
        public string redux_settings_problem; // ReduxImpactPrediction.AtmoSettingsProblem(), null when right
    }

    // When a landing is a boost-back (K2-D2 1.4.0, IanMealworm; plan 2026-11-19 §4.4).
    //
    // Boost-back is a K2-D2 feature, not one of this mod (user decision 2026-11-19): with
    // isK2D2 false (ModIdentity.IsK2D2 in this mod) the answer is always NotBoostBack, so the Atmo
    // profile's precision keeps our meaning (parachute return in Parachute mode, greyed in
    // Propulsive mode) and nothing else changes. In the K2-D2 flavour, the Atmo profile with
    // land.mode Propulsive (the default, and the only mode a K2-D2 1.4.x file knows) and precision
    // on is a boost-back; Parachute mode stays our parachute return. It only starts on a stage
    // already coming back down inside the atmosphere: atmospheric precision landing from orbit is
    // not available (off upstream too, atmo_landing_from_orbit = false).
    public static class BoostBackStart
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Cheap first test, before anything is read from the game.
        public static bool Requested(bool isK2D2, bool atmospheric_profile, LandingMethod method, bool precision) =>
            isK2D2 && atmospheric_profile && method == LandingMethod.Propulsive && precision;

        public const string LandedMessage =
            "Boost-back: the vessel is on the ground. A boost-back lands a stage that is already coming back down inside the atmosphere.";
        public const string StagedMessage =
            "Boost-back: landing with a chosen stage is not available with a boost-back. Choose the current stage.";
        public const string FromOrbitMessage =
            "Boost-back: the vessel is in orbit (its periapsis is above the ground). Atmospheric precision landing from orbit " +
            "isn't available yet - for now it lands stages that are already coming back down inside the atmosphere.";

        public static string AboveAtmosphereMessage(double altitude_m, double atmosphere_depth_m) =>
            string.Format(Inv, "Boost-back: the vessel is above the atmosphere ({0:0.0} km, the atmosphere ends at {1:0.0} km). " +
                "A boost-back starts inside the atmosphere, on a stage coming back down.", altitude_m / 1000, atmosphere_depth_m / 1000);

        public static string ReduxSettingsMessage(string problem) => "Boost-back: Redux's impact prediction settings: " + problem;

        public static BoostBackDecision Check(BoostBackState s, bool isK2D2, out string reason)
        {
            reason = null;
            if (!Requested(isK2D2, s.atmospheric_profile, s.method, s.precision))
                return BoostBackDecision.NotBoostBack;

            if (s.landed)
                reason = LandedMessage;
            else if (s.staged)
                reason = StagedMessage;
            else if (!s.suborbital)
                reason = FromOrbitMessage;
            else if (s.altitude_m > s.atmosphere_depth_m)
                reason = AboveAtmosphereMessage(s.altitude_m, s.atmosphere_depth_m);
            else if (s.redux_settings_problem != null)
                reason = ReduxSettingsMessage(s.redux_settings_problem);
            return reason == null ? BoostBackDecision.BoostBack : BoostBackDecision.Refused;
        }
    }
}
