using K2UI;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;

namespace K2D2.Lift
{
    public class LiftSettings
    {
        public ClampSetting<float> heading = new ("lift.heading", 90,  0 , 360);
 
        public Setting<int> start_altitude_km = new ("lift.start_altitude_km", 2);

        public ClampSetting<float> mid_rotate_ratio = new ("lift.mid_rotate_ratio", 0.2f, 0, 1);

        public ClampSetting<float> end_rotate_ratio = new ("lift.end_rotate_ratio", 0.5f, 0, 1);

        public LiftSettings()
        {
            start_altitude_km.listeners += v => update_altitudes();
            mid_rotate_ratio.listeners += v => update_altitudes();
            end_rotate_ratio.listeners += v => update_altitudes();
            destination_Ap_km.listeners += v => update_altitudes();

            update_altitudes();
        }

        public void update_altitudes()
        {
            _mid_rotate_altitude_km = Mathf.Lerp(start_altitude_km.V, target_Ap_km, mid_rotate_ratio.V);
            _end_rotate_altitude_km = Mathf.Lerp(start_altitude_km.V, target_Ap_km, end_rotate_ratio.V);
        }

        float _mid_rotate_altitude_km = -1;
        public float mid_rotate_altitude_km
        {
            get {
                return _mid_rotate_altitude_km;
            }
        }

        float _end_rotate_altitude_km = -1;
        public float end_rotate_altitude_km
        {
            get {
                return _end_rotate_altitude_km;
            }
        }

        public Setting<int> destination_Ap_km = new("lift.destination_Ap_km", 100);

        // ASCENT PROFILE (K2-D2 1.4.0, see AscentProfile.cs) - which ExecuteController flies the
        // Ascent phase. LiftPilot freezes the choice at the start of a run. Keys, defaults and
        // ranges are upstream's, verbatim (test SettingKeysMatchUpstream), so a K2-D2 1.4.x
        // settings file reads the same.
        public EnumSetting<AscentProfile> ascent_profile = new("lift.ascent_profile", AscentProfile.Guided);

        // GRAVITY TURN tunables (see ReactiveAscent.cs) - only shown/used when ascent_profile is
        // GravityTurn.

        // How fast the rocket needs to be rising (vertical speed) before it performs its pitch
        // kick and starts the actual turn - player-tunable since heavier/higher-TWR rockets reach
        // a good kick speed at very different times.
        public ClampSetting<float> gravity_turn_kick_speed_mps = new("lift.gravity_turn_kick_speed_mps", 100f, 10f, 300f);

        public ClampSetting<float> gravity_turn_pitch_kick_deg = new("lift.gravity_turn_pitch_kick_deg", 7.5f, 1f, 25f);
        // Below about 25 s the climb flattens out in thick air: the air pressure soars, and the
        // max-Q limit cutting the throttle can leave it falling.
        public ClampSetting<float> gravity_turn_hold_time_s = new("lift.gravity_turn_hold_time_s", 45f, 25f, 120f);

        // On: the kick angle and hold time are worked out just before the kick (see AscentPredictor),
        // and their two sliders are hidden. The slider values are still the fallback if it can't
        // make a plan.
        public Setting<bool> gravity_turn_auto = new("lift.gravity_turn_auto", true);
        // High enough that near the end the rocket keeps raising its apoapsis instead of levelling off
        // in the air (see GravityTurnControl).
        public ClampSetting<float> gravity_turn_min_throttle = new("lift.gravity_turn_min_throttle", 0.25f, 0f, 0.5f);

        // Throttle limits during the turn (see ReactiveAscent.ThrottleCap).
        public ClampSetting<float> gravity_turn_max_twr = new("lift.gravity_turn_max_twr", 2.5f, 1.2f, 5f);
        public ClampSetting<float> gravity_turn_max_q_kpa = new("lift.gravity_turn_max_q_kpa", 40f, 10f, 100f);

        public ClampSetting<float> max_throttle = new ("lift.max_throttle", 1, 0.05f, 1);
        public ClampSetting<float> end_ascent_pc = new ("lift.end_ascent_pc", 0.1f, 0, 1);

        public float end_ascent_error
        {
            get => target_Ap_km * end_ascent_pc.V/100;
        }

        public Setting<bool> coasting_warp = new ("lift.coasting_warp", true);
        public Setting<bool> adjust = new ("lift.adjust", true);
        public ClampSetting<float> end_adjust_pc = new ("lift.end_adjust_pc", 0.01f, 0.001f, 0.5f);

        public float end_adjust_error
        {
            get => target_Ap_km * end_adjust_pc.V/100;
        }

        // On: the run ends with a circularization burn at apoapsis (FinalCircularize). Off (Orbit
        // goal only; the transfer and rendezvous goals always circularize): it ends once apoapsis is
        // final, after Adjust, optionally pausing (pause_on_final) so the player can plan their own.
        public Setting<bool> auto_circularize = new ("lift.auto_circularize", true);

        // Default off since K2-D2 1.4.0 (a saved value is kept).
        public Setting<bool> pause_on_final = new ("lift.pause_on_final", false);

        public Setting<bool> heading_correction = new ("lift.heading_correction", true);

        // ROLL PROGRAM (see Ascent.cs for the full reasoning). Lives in the Profile foldout
        // alongside Heading, not Advanced - it's a per-launch choice like heading, not a tuning
        // knob. Off by default; measured relative to the vessel's OWN launch orientation rather
        // than an absolute compass/navball reading (see Ascent.cs).
        public Setting<bool> roll_program = new("lift.roll_program", false);
        public ClampSetting<float> roll_program_altitude_km = new("lift.roll_program_altitude_km", 0.1f, 0, 5);
        public ClampSetting<float> roll_program_angle_deg = new("lift.roll_program_angle_deg", 0, -180, 180);

        // How fast Ascent.cs's roll ramp (ramped_roll_target_deg) is allowed to advance toward
        // roll_program_angle_deg - player-tunable since different vessels can tolerate a faster
        // roll than others before SAS overshoots (see Ascent.cs's class comment). Default and range
        // centered on 3 deg/s.
        public ClampSetting<float> roll_program_rate_deg_s = new("lift.roll_program_rate_deg_s", 3f, 0.5f, 15f);

        // TRANSFER GOAL (sub-project 4.2, see LiftGoal.cs). Orbit is today's ascent, unchanged.
        public EnumSetting<LiftGoal> goal = new("lift.goal", LiftGoal.Orbit);
        // The target body, by name (resolved against the snapshot the plan is made from).
        public Setting<string> transfer_target = new("lift.transfer_target", "");

        // RENDEZVOUS GOAL (sub-project 4.4): the target vessel's GUID ("D" format), and whether the
        // Rendezvous pilot docks after the rendezvous.
        public Setting<string> rendezvous_target = new("lift.rendezvous_target", "");
        public Setting<bool> rendezvous_dock = new("lift.rendezvous_dock", true);

        // The ascent lead measured by the transfer ascents, per body and per ascent profile
        // (lift.ascent_lead.<body>.s and .deg for Guided, lift.ascent_lead.<body>.gt.s and .gt.deg
        // for Gravity Turn, so the two profiles never mix their leads); -1 until a first one
        // (LiftGoalRules.AscentLead then uses its estimate).
        readonly System.Collections.Generic.Dictionary<string, (Setting<float> s, Setting<float> deg)> measured_leads =
            new System.Collections.Generic.Dictionary<string, (Setting<float>, Setting<float>)>();

        public static string AscentLeadPrefix(string body, AscentProfile profile) =>
            $"lift.ascent_lead.{body ?? ""}." + (profile == AscentProfile.GravityTurn ? "gt." : "");

        public (Setting<float> s, Setting<float> deg) MeasuredLead(string body, AscentProfile profile = AscentProfile.Guided)
        {
            string prefix = AscentLeadPrefix(body, profile);
            if (!measured_leads.TryGetValue(prefix, out var pair))
            {
                pair = (new Setting<float>(prefix + "s", -1f), new Setting<float>(prefix + "deg", -1f));
                measured_leads[prefix] = pair;
            }
            return pair;
        }

        // The Ap the ascent flies to: the setting, or for one transfer run the parking orbit's
        // (never written into the player's setting).
        int ap_override_km = -1;
        public int target_Ap_km => ap_override_km > 0 ? ap_override_km : destination_Ap_km.V;

        public void SetApOverride(int km)
        {
            ap_override_km = km;
            update_altitudes();
        }

        Label end_ascent_alt;
        Label end_adjust_alt;

        Group adjust_group;

        void setLabels()
        {
            end_ascent_alt.text = $"End Ascent Alt. : {target_Ap_km - end_ascent_error:n2} km";
            end_adjust_alt.text = $"Final Adjust Alt : {target_Ap_km - end_adjust_error:n2} km";
        }

        K2Toggle auto_circularize_toggle, pause_toggle;

        // ASCENT PROFILE (K2-D2 1.4.0). Must match nothing in the uxml: the choices are set here.
        const string GuidedChoice = "Guided";
        const string GravityTurnChoice = "Gravity Turn (Experimental)";
        static string ProfileChoice(AscentProfile profile) => profile == AscentProfile.GravityTurn ? GravityTurnChoice : GuidedChoice;

        DropdownField ascent_profile_drop;
        VisualElement guided_profile_settings, guided_roll_settings, gravity_turn_settings, graph;

        // The profile the run would fly (Gravity Turn for the Orbit goal only) decides which
        // settings show; the dropdown itself shows for the Orbit goal only. The ascent graph only
        // plots Guided's fixed pitch curve: hidden with Gravity Turn.
        void UpdateProfileSettings()
        {
            if (ascent_profile_drop == null)
                return;
            bool orbit = goal.V == LiftGoal.Orbit;
            AscentProfile flown = LiftGoalRules.ProfileFor(goal.V, ascent_profile.V);
            ascent_profile_drop.Show(orbit);
            guided_profile_settings.Show(flown == AscentProfile.Guided);
            guided_roll_settings.Show(flown == AscentProfile.Guided);
            gravity_turn_settings.Show(flown == AscentProfile.GravityTurn);
            graph.Show(flown == AscentProfile.Guided);
        }

        // The transfer and rendezvous goals always circularize: no auto-circularize toggle for them,
        // and the pause (only used without auto-circularize) is greyed whenever it cannot apply.
        public void UpdateCircularizeToggles(LiftGoal goal)
        {
            bool forced = LiftGoalRules.CircularizationForced(goal);
            auto_circularize_toggle?.Show(!forced);
            pause_toggle?.SetEnabled(!forced && !auto_circularize.V);
        }

        public void setupUI(VisualElement root)
        {
            end_ascent_alt =  root.Q<Label>("end_ascent_alt");
            end_adjust_alt = root.Q<Label>("end_adjust_alt");
            adjust_group = root.Q<K2UI.Group>("adjust_group");

            root.Q<K2Toggle>("heading_correction").Bind(heading_correction);  

            // bind to settings and add listener on the same line !
            root.Q<K2Slider>("end_ascent_pc").Bind(end_ascent_pc);
            end_ascent_pc.listen(v => setLabels());

            root.Q<K2Toggle>("coasting_warp").Bind(coasting_warp);

            // adjust is binded to setting and to show adjust group 
            root.Q<K2Toggle>("adjust").Bind(adjust);
            adjust.listen(v => adjust_group.Show(v));

            root.Q<K2Slider>("end_adjust_pc").Bind(end_adjust_pc);
            end_adjust_pc.listen(v => setLabels());

            auto_circularize_toggle = root.Q<K2Toggle>("auto_circularize").Bind(auto_circularize);

            pause_toggle = root.Q<K2Toggle>("pause_on_final").Bind(pause_on_final);

            // ASCENT PROFILE (K2-D2 1.4.0): the dropdown, and the settings of each profile.
            ascent_profile_drop = root.Q<DropdownField>("ascent_profile_drop");
            guided_profile_settings = root.Q<VisualElement>("guided_profile_settings");
            guided_roll_settings = root.Q<VisualElement>("guided_roll_settings");
            gravity_turn_settings = root.Q<VisualElement>("gravity_turn_settings");
            graph = root.Q<VisualElement>("graph");
            ascent_profile_drop.choices = new System.Collections.Generic.List<string> { GuidedChoice, GravityTurnChoice };
            ascent_profile_drop.RegisterValueChangedCallback(evt =>
                ascent_profile.V = evt.newValue == GravityTurnChoice ? AscentProfile.GravityTurn : AscentProfile.Guided);
            // Also follows Reset.
            ascent_profile.listen(profile =>
            {
                ascent_profile_drop.SetValueWithoutNotify(ProfileChoice(profile));
                UpdateProfileSettings();
            });
            goal.listeners += g => UpdateProfileSettings();

            gravity_turn_settings.Q<K2Toggle>("gravity_turn_auto").Bind(gravity_turn_auto);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_kick_speed_mps").Bind(gravity_turn_kick_speed_mps);
            var pitch_kick_slider = gravity_turn_settings.Q<K2Slider>("gravity_turn_pitch_kick_deg").Bind(gravity_turn_pitch_kick_deg);
            var hold_time_slider = gravity_turn_settings.Q<K2Slider>("gravity_turn_hold_time_s").Bind(gravity_turn_hold_time_s);
            // Auto picks these two itself, so the sliders only show when it's off.
            gravity_turn_auto.listen(v =>
            {
                pitch_kick_slider.Show(!v);
                hold_time_slider.Show(!v);
            });
            gravity_turn_settings.Q<K2Slider>("gravity_turn_min_throttle").Bind(gravity_turn_min_throttle);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_max_twr").Bind(gravity_turn_max_twr);
            gravity_turn_settings.Q<K2Slider>("gravity_turn_max_q_kpa").Bind(gravity_turn_max_q_kpa);

            // ROLL PROGRAM - .listen(), not .listeners += (see LandingSettings.setupUI's comment on
            // precision_landing for why that distinction matters): this defaults to off, so a plain
            // .listeners += would leave roll_program_settings visible on a fresh window open until
            // the toggle was flipped once.
            root.Q<K2Toggle>("roll_program").Bind(roll_program);
            var roll_program_settings = root.Q<VisualElement>("roll_program_settings");
            roll_program.listen(v => roll_program_settings.Show(v));
            roll_program_settings.Q<K2Slider>("roll_program_altitude_km").Bind(roll_program_altitude_km);
            roll_program_settings.Q<K2Slider>("roll_program_angle_deg").Bind(roll_program_angle_deg);
            roll_program_settings.Q<K2Slider>("roll_program_rate_deg_s").Bind(roll_program_rate_deg_s);
        }
    }
}
