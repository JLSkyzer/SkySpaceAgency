using KTools;
using UnityEngine.UIElements;


namespace SkySpaceAgency.Landing
{
    // How the boost-back turns the vessel round before its burn (see BoostBackBurn). K2-D2 1.4.0
    // (IanMealworm); the boost-back flies in the K2-D2 flavour only (BoostBackStart).
    public enum FlipStyle
    {
        // Engines lit from the start, so their gimbals help turn (like Super Heavy).
        Powered,
        // Turn first, then burn (like Falcon 9).
        Unpowered,
    }

    // How to land on an atmospheric body (LandingSettings.mode, atmo profile only).
    public enum LandingMethod
    {
        Propulsive,
        Parachute,
    }

    // Atmo/Vacuum profile split: LandingPilot constructs TWO of these (settings_atmo/
    // settings_vac), one per SettingsFile (k2d2_landing_atmo.json / k2d2_landing_vac.json), so
    // tuning one profile can never touch the other's saved values or UI. Every field keeps its
    // original key name ("land.xxx") in both files, since the files themselves are what keeps the
    // two profiles apart.
    //
    // The LAND tab (AppUI/Tabs/LandTab.cs) binds both profiles' instances to its controls: the Atmo
    // panel shows the settings meaningful on an atmospheric body (warp timing, brake timing, touchdown
    // speed profile, the target lat/lon, the precision toggle enabled in Parachute mode only, the
    // reentry drag scale), the Vacuum panel its own. TouchDown's tunables (touch_down_max_angle etc.)
    // live on TouchDown.cs.
    public class LandingSettings
    {
        public Setting<bool> auto_warp;
        public ClampSetting<float> burn_before;
        public Setting<int> rotation_warp_duration;
        // Warp with check of rotation
        public ClampSetting<float> max_rotation;

        public float brake_speed
        {
            get => 50;
        }

        public float pause_time
        {
            get => 1;
        }

        public ClampSetting<float> start_touchdown_altitude;

        public ClampSetting<float> touch_down_ratio;

        public ClampSetting<float> touch_down_speed;

        // TARGET (precision landing). Waypoint list, map picker or manual lat/lon (LAND tab). On the
        // Atmo profile precision flies in Parachute mode only (the parachute precision return,
        // LandingPilotReentry.cs): the LAND tab disables the toggle and keeps it off in Propulsive mode.
        public Setting<bool> precision_landing;
        public Setting<float> target_latitude;
        public Setting<float> target_longitude;

        // Landing mode. Atmo profile only: null on the vacuum profile, where the landing is
        // always propulsive (see LandingPilot.parachute_mode).
        public EnumSetting<LandingMethod> mode;

        // Deploy the legs on entering Brake instead of Touch Down. Both profiles.
        public Setting<bool> deploy_legs_early;

        // Parachute precision return: multiplies the capsule's drag in the reentry predictor
        // (ReentryContext -> ReentryVehicle.DragScale), the calibration the in-game accuracy rests
        // on. Atmo profile only (null on the vacuum profile). Set by the player only: the slider in
        // ADVANCED, or Apply on the suggestion shown after a precision return (never applied alone).
        public ClampSetting<float> reentry_drag_scale;

        // Landing with a chosen stage: "Drop the lower stage at the latest at (km)", as typed.
        // Empty = automatic (LandingStages.ParseDropHeight). It raises the computed drop height,
        // never lowers it. Both profiles; bound in LandingUI (a text field, so it can be empty).
        public Setting<string> drop_stage_at_km;

        // Boost-back (K2-D2 1.4.0, keys, defaults and ranges verbatim so a K2-D2 1.4.x file loads
        // unchanged). Declared on both profiles and in both flavours, as upstream; read only by the
        // boost-back, which flies in the K2-D2 flavour only (BoostBackStart). entry_angle_of_attack:
        // how far the vessel is held off retrograde in the entry guidance (AtmoEntryGuidance);
        // entry_flight_path_angle: the from-orbit entry steepness (not ported: kept for the files);
        // boost_back_flip: see FlipStyle.
        public ClampSetting<float> entry_angle_of_attack;
        public ClampSetting<float> entry_flight_path_angle;
        public EnumSetting<FlipStyle> boost_back_flip;

        public LandingSettings(SettingsFile file, bool atmospheric)
        {
            auto_warp = new("land.auto_warp", true, file);
            burn_before = new("land.burnBefore", 0, 0, 10, file);
            rotation_warp_duration = new("land.rotation_warp_duration", 60, file);
            max_rotation = new("land.max_rotation", 10, 5, 30, file);

            start_touchdown_altitude = new("land.touch_down_altitude", 1500, 500, 5000, file);
            touch_down_ratio = new("land.touch_down_ratio", 0.5f, 0.5f, 3, file);
            touch_down_speed = new("land.touch_down_speed", 2.5f, 0, 10, file);

            precision_landing = new("land.precision_landing", false, file);
            target_latitude = new("land.target_latitude", 0f, file);
            target_longitude = new("land.target_longitude", 0f, file);

            deploy_legs_early = new("land.deploy_legs_early", false, file);
            drop_stage_at_km = new("land.drop_stage_at_km", "", file);
            entry_angle_of_attack = new("land.entry_angle_of_attack", 10, 0, 30, file);
            entry_flight_path_angle = new("land.entry_flight_path_angle", 10, 0, 60, file);
            boost_back_flip = new("land.boost_back_flip", FlipStyle.Powered, file);
            if (atmospheric)
            {
                mode = new("land.mode", LandingMethod.Propulsive, file);
                reentry_drag_scale = new("land.reentry_drag_scale", 1f, 0.3f, 3f, file);
            }
        }

        // Atmo profile: precision means the parachute return in Parachute mode; in Propulsive mode
        // it is the boost-back, in the K2-D2 flavour only (BoostBackStart).
        public static bool PrecisionAllowed(LandingMethod method) =>
            method == LandingMethod.Parachute || ModIdentity.IsK2D2;

        public float compute_limit_speed(float altitude)
        {
            // just to have understandable settings (not 0.1)
            float div = 10;
            return altitude * touch_down_ratio.V / div + touch_down_speed.V;
        }
    }
}
