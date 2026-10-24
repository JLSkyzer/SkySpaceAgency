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

        public ClampSetting<float> max_throttle = new ("lift.max_throttle", 1, 0, 1);
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

        // Default on: run straight through ascent -> atmosphere clear -> circularize with no
        // pause, since FinalCircularize (Controlers/Final.cs) now creates and flies the
        // circularize burn itself instead of waiting on a manually- or FlightPlan-created node.
        // Turning this off falls back to the old flow - Coasting.cs only actually pauses on
        // pause_on_final below when this is off.
        public Setting<bool> auto_circularize = new ("lift.auto_circularize", true);

        public Setting<bool> pause_on_final = new ("lift.pause_on_final", true);

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

        // The ascent lead measured by the transfer ascents, per body (lift.ascent_lead.<body>.s and
        // .deg); -1 until a first one (LiftGoalRules.AscentLead then uses its estimate).
        readonly System.Collections.Generic.Dictionary<string, (Setting<float> s, Setting<float> deg)> measured_leads =
            new System.Collections.Generic.Dictionary<string, (Setting<float>, Setting<float>)>();

        public (Setting<float> s, Setting<float> deg) MeasuredLead(string body)
        {
            body = body ?? "";
            if (!measured_leads.TryGetValue(body, out var pair))
            {
                pair = (new Setting<float>($"lift.ascent_lead.{body}.s", -1f), new Setting<float>($"lift.ascent_lead.{body}.deg", -1f));
                measured_leads[body] = pair;
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

            root.Q<K2Toggle>("auto_circularize").Bind(auto_circularize);

            root.Q<K2Toggle>("pause_on_final").Bind(pause_on_final);

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
