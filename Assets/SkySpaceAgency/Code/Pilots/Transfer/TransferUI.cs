using System;
using System.Collections.Generic;
using K2D2.KSPService;
using K2D2.OrbitPlanning;
using K2D2.UI;
using K2UI;
using K2UI.Tabs;
using KTools;
using UnityEngine.UIElements;

namespace K2D2.Transfer
{
    // The Transfer tab's choices, kept between sessions.
    public static class TransferSettings
    {
        public static Setting<string> target_body = new("transfer.target_body", "");
        public static EnumSetting<ArrivalKind> arrival = new("transfer.arrival", ArrivalKind.Orbit);
        public static Setting<bool> low_circular = new("transfer.low_circular", true);
        public static Setting<float> pe_km = new("transfer.pe_km", 100);
        public static Setting<float> ap_km = new("transfer.ap_km", 100);
        public static Setting<bool> inc_enabled = new("transfer.inc_enabled", false);
        public static ClampSetting<float> inc_deg = new("transfer.inc_deg", 0, 0, 180);
        public static Setting<bool> prepare_only = new("transfer.prepare_only", false);
        public static Setting<string> max_trip_days = new("transfer.max_trip_days", "");
    }

    // Transfer tab (spec 4.1): the choices, the buttons, and the plan of TransferPilot (case, dates,
    // steps, Δv per stage, warnings). The texts and tables come from TransferReadout (pure).
    public class TransferUI : K2Page
    {
        // Every element this page looks up, by name (checked by the UI tests against the window).
        public static readonly string[] Elements =
        {
            "target_body", "arrival", "low_circular", "low_orbit_hint", "orbit_fields", "pe_km", "ap_row", "ap_km",
            "inc_enabled", "inc_deg", "prepare_only", "gravity_assist", "max_trip_days",
            "plan", "start", "stop", "resume", "cancel_saved", "resume_offer",
            "status_pilot", "pilot_console", "progress",
            "plan_group", "case", "departure", "trip", "steps", "totals", "margin", "stages", "stage_note", "warnings",
        };

        const float RefreshSeconds = 0.5f;

        readonly TransferPilot pilot;
        readonly BurndV burn_dv = new BurndV();

        DropdownField target_drop;
        FloatField pe_field, ap_field, inc_field;
        VisualElement orbit_fields, ap_row, plan_group, steps_el, stages_el, warnings_el;
        Label low_orbit_hint, resume_offer, case_label, departure_label, trip_label, totals_label, margin_label, stage_note;
        Button plan_bt, start_bt, stop_bt, resume_bt, cancel_bt;
        FullStatus status_bar;

        BodySystem list_system;
        List<BodyChoices.Entry> entries = new List<BodyChoices.Entry>();
        float next_body_try;
        string ui_error;

        // The plan last seen flying: its flown steps are shown done, and its budget starts at StepIndex.
        TransferPlan flown_plan;
        TransferPlan estimated_plan;
        List<string> estimated_warnings = new List<string>();
        float next_refresh;

        public TransferUI(TransferPilot pilot)
        {
            this.pilot = pilot;
            code = "transfer";
        }

        public override bool onInit()
        {
            foreach (string name in Elements)
                if (panel.Q(name) == null)
                {
                    K2D2_Plugin.logger.LogError($"[Transfer] the Transfer tab has no element named {name}");
                    return false;
                }

            target_drop = panel.Q<DropdownField>("target_body");
            target_drop.RegisterValueChangedCallback(evt =>
            {
                int i = BodyChoices.FindLabel(entries, evt.newValue);
                if (i >= 0)
                    TransferSettings.target_body.V = entries[i].Name;
            });
            panel.Q<InlineEnum>("arrival").Bind(TransferSettings.arrival, "Encounter;Orbit");
            panel.Q<K2Toggle>("low_circular").Bind(TransferSettings.low_circular);
            // Unticking starts the fields from the target's low orbit.
            TransferSettings.low_circular.listeners -= OnLowCircular; // once, even after a re-init
            TransferSettings.low_circular.listeners += OnLowCircular;
            low_orbit_hint = panel.Q<Label>("low_orbit_hint");
            orbit_fields = panel.Q("orbit_fields");
            ap_row = panel.Q("ap_row");
            pe_field = panel.Q<FloatField>("pe_km").Bind(TransferSettings.pe_km);
            ap_field = panel.Q<FloatField>("ap_km").Bind(TransferSettings.ap_km);
            inc_field = panel.Q<FloatField>("inc_deg").Bind(TransferSettings.inc_deg);
            panel.Q<K2Toggle>("inc_enabled").Bind(TransferSettings.inc_enabled);
            panel.Q<K2Toggle>("prepare_only").Bind(TransferSettings.prepare_only);

            // Gravity assists come with sub-project 4.6.
            var assist = panel.Q<K2Toggle>("gravity_assist");
            assist.value = false;
            assist.tooltip = "Coming soon";
            assist.SetEnabled(false);

            var max_trip = panel.Q<TextField>("max_trip_days");
            max_trip.isDelayed = true;
            max_trip.value = TransferSettings.max_trip_days.V;
            max_trip.RegisterValueChangedCallback(evt => TransferSettings.max_trip_days.V = evt.newValue ?? "");

            plan_bt = panel.Q<Button>("plan");
            start_bt = panel.Q<Button>("start");
            stop_bt = panel.Q<Button>("stop");
            resume_bt = panel.Q<Button>("resume");
            cancel_bt = panel.Q<Button>("cancel_saved");
            plan_bt.clicked += OnPlan;
            start_bt.clicked += () => { ui_error = null; pilot.Start(TransferSettings.prepare_only.V); };
            stop_bt.clicked += () => { ui_error = null; pilot.Stop(); };
            resume_bt.clicked += () => { ui_error = null; pilot.Resume(); };
            cancel_bt.clicked += () => { ui_error = null; pilot.CancelSaved(); };

            resume_offer = panel.Q<Label>("resume_offer");
            status_bar = new FullStatus(panel);
            plan_group = panel.Q("plan_group");
            case_label = panel.Q<Label>("case");
            departure_label = panel.Q<Label>("departure");
            trip_label = panel.Q<Label>("trip");
            steps_el = panel.Q("steps");
            totals_label = panel.Q<Label>("totals");
            margin_label = panel.Q<Label>("margin");
            stages_el = panel.Q("stages");
            stage_note = panel.Q<Label>("stage_note");
            warnings_el = panel.Q("warnings");
            return true;
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;
            if (pilot.isRunning)
                flown_plan = pilot.Current;
            UpdateBodies();
            UpdateControls();
            UpdateStatus();
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= next_refresh || pilot.Current != estimated_plan)
            {
                next_refresh = now + RefreshSeconds;
                UpdatePlan();
            }
            return true;
        }

        // ---------------------------------------------------------------- bodies

        void UpdateBodies()
        {
            // The pilot's latest snapshot when there is one; else a body list read from the game.
            BodySystem s = pilot.System;
            if (s == null && list_system == null && UnityEngine.Time.realtimeSinceStartup >= next_body_try)
            {
                next_body_try = UnityEngine.Time.realtimeSinceStartup + 2;
                try
                {
                    s = GameBodySystem.Build(GeneralTools.Current_UT, out string error);
                    if (s == null)
                        K2D2_Plugin.logger.LogInfo($"[Transfer] tab: no body list yet ({error})");
                }
                catch (Exception e)
                {
                    K2D2_Plugin.logger.LogInfo($"[Transfer] tab: no body list yet ({e.GetType().Name}: {e.Message})");
                }
            }
            if (s == null || s == list_system)
                return;
            list_system = s;
            entries = BodyChoices.Grouped(s);
            target_drop.choices = BodyChoices.Labels(entries);
            int i = BodyChoices.FindName(entries, TransferSettings.target_body.V);
            target_drop.SetValueWithoutNotify(i >= 0 ? entries[i].Label : "Choose a body");
        }

        bool TryTarget(out BodyNode body)
        {
            body = null;
            int i = BodyChoices.FindName(entries, TransferSettings.target_body.V);
            if (i < 0 || list_system == null)
                return false;
            body = list_system[entries[i].Index];
            return true;
        }

        // ---------------------------------------------------------------- controls

        void UpdateControls()
        {
            bool idle = !pilot.isRunning && !pilot.Searching;
            plan_bt.SetEnabled(idle);
            plan_bt.text = pilot.Searching ? "Searching..." : "Plan";
            start_bt.SetEnabled(pilot.CanStart);
            stop_bt.SetEnabled(pilot.isRunning || pilot.Searching);
            stop_bt.text = pilot.Searching && !pilot.isRunning ? "Cancel" : "Stop";
            // Resume only with a saved plan; Cancel with a saved plan or while flying.
            resume_bt.Show(pilot.HasSavedPlan);
            resume_bt.SetEnabled(pilot.CanResume);
            cancel_bt.Show(pilot.HasSavedPlan || pilot.isRunning);

            bool low = TransferSettings.low_circular.V;
            bool orbit = TransferSettings.arrival.V == ArrivalKind.Orbit;
            orbit_fields.Show(!low);
            ap_row.Show(orbit);
            inc_field.SetEnabled(TransferSettings.inc_enabled.V);
            if (TryTarget(out var body))
            {
                double km = (body.LowOrbitRadius - body.Radius) / 1000;
                low_orbit_hint.text = low
                    ? $"{(orbit ? "Capture into" : "Fly past at")} {km:F0} km above {body.Name}: 10 km above its " +
                      (body.AtmosphereDepth >= body.MaxTerrainHeight ? "atmosphere." : "highest terrain.")
                    : $"Lowest safe altitude at {body.Name}: {km:F0} km.";
            }
            else
                low_orbit_hint.text = "Choose a target body.";
        }

        // Unticking Low circular starts the fields from the target's low orbit.
        void OnLowCircular(bool on)
        {
            if (!on && TryTarget(out var body))
            {
                float km = (float)Math.Round((body.LowOrbitRadius - body.Radius) / 1000, 1);
                TransferSettings.pe_km.V = km;
                TransferSettings.ap_km.V = km;
            }
        }

        // The target goes by name and the orbit as altitudes: the pilot resolves both against the
        // bodies of its own snapshot, not against this tab's list.
        void OnPlan()
        {
            ui_error = null;
            if (!TransferReadout.TryInput(TransferSettings.target_body.V, TransferSettings.arrival.V, TransferSettings.low_circular.V,
                    TransferSettings.pe_km.V, TransferSettings.ap_km.V, TransferSettings.inc_enabled.V, TransferSettings.inc_deg.V,
                    TransferSettings.max_trip_days.V, out var input, out string error))
            {
                ui_error = error;
                return;
            }
            pilot.Plan(input);
        }

        // ---------------------------------------------------------------- status

        void UpdateStatus()
        {
            status_bar.Reset();
            status_bar.avatar?.SetRunning(pilot.isRunning);
            if (!string.IsNullOrEmpty(ui_error))
                status_bar.Warning(ui_error);
            else if (!string.IsNullOrEmpty(pilot.Status))
                status_bar.Status(pilot.Status, pilot.Phase == TransferPhase.Failed || pilot.Status.StartsWith("Refused")
                    ? StatusLine.Level.Warning : StatusLine.Level.Normal);
            else
                status_bar.Status("Choose a target, then Plan.");
            if (pilot.Searching)
                status_bar.Progress(pilot.SearchProgress, "Searching");

            string offer = pilot.isRunning ? null : pilot.ResumeOffer;
            resume_offer.Show(!string.IsNullOrEmpty(offer));
            resume_offer.text = offer ?? "";
        }

        // ---------------------------------------------------------------- plan

        void UpdatePlan()
        {
            var plan = pilot.Current;
            if (plan != estimated_plan)
            {
                estimated_plan = plan;
                estimated_warnings = plan != null && plan.Ok && !pilot.isRunning
                    ? TransferReadout.LongBurnWarnings(plan, pilot.System, EstimateBurnSeconds)
                    : new List<string>();
            }

            var warnings = new List<string>();
            if (plan != null && plan.Ok)
            {
                warnings.AddRange(plan.Warnings);
                foreach (string w in estimated_warnings)
                    warnings.Add("Estimate: " + w);
            }
            FillWarnings(warnings);

            if (plan == null || !plan.Ok)
            {
                plan_group.Show(false);
                return;
            }
            plan_group.Show(true);
            double now = GeneralTools.Current_UT;
            bool flown = plan == flown_plan;
            int from = flown ? pilot.StepIndex : 0;

            case_label.text = "Case: " + plan.CaseName;
            departure_label.text = TransferReadout.Departure(plan, now);
            trip_label.text = TransferReadout.Trip(plan);

            steps_el.Clear();
            foreach (var row in TransferReadout.Steps(plan, pilot.System, now, from))
                steps_el.Add(StepElement(row));

            var budget = TransferFlight.RemainingBudget(plan, from, pilot.Stages);
            totals_label.text = TransferReadout.Totals(plan, budget);
            margin_label.text = TransferReadout.Margin(budget);
            margin_label.style.color = budget.Ok ? new StyleColor(StyleKeyword.Null) : new StyleColor(new UnityEngine.Color(1f, 0.55f, 0.4f));

            stages_el.Clear();
            var stage_rows = TransferReadout.Stages(plan, from, pilot.Stages, budget, NextStage(), out string note);
            foreach (var row in stage_rows)
            {
                var label = new Label(row.Text);
                label.style.whiteSpace = WhiteSpace.Normal;
                if (row.ByHand)
                    label.style.color = new UnityEngine.Color(0.88f, 0.78f, 0.52f);
                stages_el.Add(label);
            }
            stage_note.Show(note != null);
            stage_note.text = note ?? "";
        }

        static VisualElement StepElement(TransferReadout.StepRow row)
        {
            var block = new VisualElement();
            block.style.marginTop = 4;
            var line = new VisualElement();
            line.style.flexDirection = FlexDirection.Row;
            var kind = new Label(row.Kind);
            kind.style.flexGrow = 1;
            kind.style.whiteSpace = WhiteSpace.Normal;
            var dv = new Label(row.DeltaV);
            dv.AddToClassList("advanced-info-row-value");
            line.Add(kind);
            line.Add(dv);
            block.Add(line);
            var when = new Label(row.When);
            when.style.fontSize = 12;
            block.Add(when);
            if (!string.IsNullOrEmpty(row.Explanation))
            {
                var why = new Label(row.Explanation);
                why.style.fontSize = 12;
                why.style.whiteSpace = WhiteSpace.Normal;
                why.style.color = new UnityEngine.Color(0.78f, 0.86f, 0.94f);
                block.Add(why);
            }
            if (row.Done)
                block.style.opacity = 0.5f;
            return block;
        }

        void FillWarnings(List<string> warnings)
        {
            warnings_el.Clear();
            foreach (string w in warnings)
            {
                var label = new Label("Warning: " + w);
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.color = new UnityEngine.Color(0.88f, 0.78f, 0.52f);
                warnings_el.Add(label);
            }
        }

        // The next stage the game would fire (above it: staged already), null when unknown.
        static int? NextStage()
        {
            try
            {
                var staging = K2D2_Plugin.Instance?.current_vessel?.VesselComponent?.SimulationObject?.Staging;
                if (staging == null)
                    return null;
                int count = staging.StageCount;
                bool lastActive = count > 0 && staging.AvailableStages[count - 1].IsActive;
                return K2D2.Landing.Precision.JettisonPlan.NextStage(count, lastActive);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // A burn's length with the current stage, as TransferPilot estimates it at the ignition.
        double EstimateBurnSeconds(double dv)
        {
            if (!(dv > 0))
                return 0;
            try
            {
                burn_dv.Compute_Thrust();
                double thrust = burn_dv.active_thrust;
                if (thrust <= 0)
                    thrust = burn_dv.full_thrust.magnitude;
                return BurnTiming.Duration(dv, thrust, burn_dv.mass, burn_dv.active_isp);
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }
    }
}
