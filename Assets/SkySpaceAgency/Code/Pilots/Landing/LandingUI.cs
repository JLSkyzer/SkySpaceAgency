using System;
using System.Collections.Generic;
using System.Globalization;
using K2D2.Landing.Braking;
using K2D2.KSPService;
using K2D2.UI;
using K2UI;
using K2UI.Tabs;
using KSP.Game;
using UnityEngine.UIElements;
using KTools;



namespace K2D2.Landing
{
    class LandingUI : K2Page
    {
        LandingPilot pilot;
        public LandingUI(LandingPilot pilot)
        {
            this.pilot = pilot;
            code = "landing";
        }

        public VisualElement landing_infos;
        public Label collision_value;
        public FullStatus status_bar;

        public ToggleButton run_button;

        public Button touch_down;

        // One target block per profile (target_settings_vac / target_settings_atmo in Landing.uxml),
        // each bound to its own settings object, never rebound.
        class TargetBlock
        {
            public string suffix;
            public LandingSettings settings;
            public DropdownField drop;
            public Button pick;
            public Label status;
            public bool? pick_available;
            // Keyed by the exact string shown in drop, since DropdownField only ever gives us the
            // selected label back in its ChangeEvent - not an index or the underlying record.
            public Dictionary<string, ReduxWaypoint> choices = new();
        }

        TargetBlock target_vac;
        TargetBlock target_atmo;
        TargetBlock VisibleTarget => LandingProfile.IsAtmospheric ? target_atmo : target_vac;

        const string PickText = "Pick on map";
        const string PickTooltip = "Click the body in the map view to set the landing target";
        const string WaitingText = "Click the body… (Esc to cancel)";
        const string UnavailableText = "Map picking unavailable in this game version";
        const string OpenMapText = "Open the map (M), then click the body.";
        const string ReduxAlsoSavedText = "Redux's Waypoints window also saved this click as a waypoint.";
        // How long to wait for the map view after asking the game to open it, before starting anyway.
        const float MapOpenTimeout = 3f;

        // Session-only (never saved): the last point picked on the map, listed as PickedName.
        ReduxWaypoint? picked_point;

        // The pick in progress: the block that asked for it (null when none), whether the placement
        // still waits for the map view to open, and the vessel it was asked for.
        TargetBlock pick_owner;
        bool pick_begin_pending;
        float pick_begin_deadline;
        object pick_vessel;
        // Player waypoint count (all bodies) before the pick, checked again on the tick after a
        // completed pick: Redux's own Waypoints window listens to the same event and may save one.
        int pick_waypoints_before = -1;
        TargetBlock pick_count_check;

        // KscLocation retries a full game lookup while it fails: at most once a second from here.
        float ksc_retry_time = -1;

        // Precision Landing orbit gate (see UpdateOrbitGate below) - the same periapsis limit as
        // the start check (PrecisionStart.MaxPeriapsisAltitude and its message), so the two can
        // never drift apart. Vacuum-only (precision_landing_orbit_gate_vac).
        Label orbit_gate_label;
        // The last precision plan, its warnings or the final distance (precision_plan_vac, and
        // precision_plan_atmo for a parachute precision return).
        Label precision_plan_label;
        Label precision_plan_label_atmo;
        // Atmo: the note under the precision toggle, and the calibration suggestion after a
        // precision return with its Apply button.
        Label atmo_precision_note;
        VisualElement reentry_calibration;
        Label reentry_suggestion;

        // Atmo/Vacuum profile panels (see LandingSettings.cs's class comment) - shown/hidden
        // together every UI tick by UpdateProfilePanels, based on LandingProfile.IsAtmospheric.
        // Each pair is bound once, ever, in onInit() below to its own fixed settings object -
        // never rebound, since K2Page.onInit() only runs once per game session.
        VisualElement landing_mode_group_atmo;
        VisualElement landing_mode_group_vac;
        VisualElement atmo_settings_panel;
        VisualElement vac_settings_panel;

        // "Land with stage" (LandingPilotStages.cs): one dropdown per profile panel, both showing
        // pilot.landing_stage_choice; the list is read again every UI tick (the Δv changes).
        DropdownField stage_drop_atmo;
        DropdownField stage_drop_vac;
        List<LandingStageData> stage_choices = new();

        // "Land" for a parachute landing (one button, no engine), "Brake" otherwise.
        string IdleLabel => pilot.parachute_mode ? "Land" : "Brake";

        // The mode the tab is about: the running one, else the selector. Flipping the selector
        // during a propulsive run must not change what the tab shows (Touch Down in particular).
        // A parachute precision return is a parachute landing from its first burn.
        bool ParachuteLanding => pilot.isRunning
            ? pilot.mode == LandingPilot.Mode.Parachute || pilot.reentry_run
            : pilot.parachute_mode;

        // Logged once per session, then silent: the readout runs every UI tick.
        bool readout_failure_logged = false;

        public override bool onInit()
        {
            landing_infos = panel.Q<VisualElement>("landing_infos");
            collision_value = panel.Q<Label>("collision_value");

            run_button = panel.Q<ToggleButton>("run");
            touch_down = panel.Q<Button>("touch_down");
            status_bar = new FullStatus(panel);

            // Waypoint picker - one per profile: KSC and the point picked on the map, then whatever
            // the player's already placed on the current body via Redux's own Waypoints window
            // (K2D2.Landing.ReduxWaypoints, reflection-based since that system is internal), then
            // the game's waypoints. Rebuilt every UI tick for the visible profile (see onUpdateUI()
            // below) rather than via a click listener, since DropdownField opens its own native
            // list on pointer-down instead of dispatching a bubbling ClickEvent - a per-tick
            // rebuild also keeps the list current if a waypoint's added while this tab is open.
            //
            // Each block writes straight to its own settings object (settings_vac/settings_atmo),
            // not through the profile-dependent pilot.settings property.
            target_vac = SetupTarget("_vac", pilot.settings_vac);
            target_atmo = SetupTarget("_atmo", pilot.settings_atmo);
            buildWaypointList(VisibleTarget);

            pilot.is_running_event += is_running => run_button.Value = is_running;
            // Same "give him a little life" touch as Node/Lift: K2's 3 grille lines cascade
            // on/off with the autopilot, via the same is_running_event run_button already
            // listens to above.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners += v =>
            {
                pilot.isRunning = v;
                if (v && !pilot.isRunning)
                {
                    // Refused by the start checks (pilot.last_error is shown below): put the
                    // toggle back. This re-enters the listener with false, which sets the label.
                    run_button.Value = false;
                    return;
                }
                run_button.label = v ? "Stop" : IdleLabel;
            };

            touch_down.listenClick(() =>
            {
                // Hidden in parachute mode (onUpdateUI); a parachute landing never goes to Touch Down.
                if (ParachuteLanding)
                    return;
                pilot.isRunning = true;
                if (pilot.isRunning && pilot.mode != LandingPilot.Mode.Parachute)
                    pilot.setMode(LandingPilot.Mode.TouchDown);
            });

            // Atmo/Vacuum profile panels - two full sibling groups/panels in Landing.uxml, each
            // bound exactly once here to its own fixed settings/TouchDown instance (never
            // rebound - see this class's field comments and LandingSettings.cs's class comment).
            landing_mode_group_atmo = panel.Q<VisualElement>("landing_mode_group_atmo");
            landing_mode_group_vac = panel.Q<VisualElement>("landing_mode_group_vac");
            atmo_settings_panel = panel.Q<VisualElement>("atmo_settings_panel");
            vac_settings_panel = panel.Q<VisualElement>("vac_settings_panel");

            // Pass `panel` (the whole tab page) here, not vac_settings_panel/atmo_settings_panel -
            // LandingSettings.setupUI/setupBasicUI and TouchDown's SetupFullUI/SetupBasicUI all
            // reach elements that live OUTSIDE the ADVANCED foldout's own sub-panels
            // (precision_landing_vac, target_settings_vac, and the orbit gate label all live in
            // landing_mode_group_vac, a sibling of advanced_foldout - see Landing.uxml).
            // VisualElement.Q<T>() searches the whole subtree under whatever root it's given, so
            // passing the narrower vac_settings_panel/atmo_settings_panel makes
            // root.Q<K2Toggle>("precision_landing_vac") return null - Bind() on that null throws a
            // NullReferenceException that unwinds all the way up through K2Page.Init ->
            // TabbedPage.Init -> K2D2Window.OnUiReload, aborting every other tab's Init() too.
            // Passing `panel` everywhere is always safe: Q<T>() finds a uniquely-named element
            // anywhere under it regardless of nesting depth.
            pilot.settings_vac.setupUI(panel);
            pilot.brake.SetupFullUI(panel);

            pilot.settings_atmo.setupBasicUI(panel);
            pilot.brake.SetupBasicUI(panel);

            // Reset resets BOTH profiles together, not just whichever panel happens to be showing
            // right now, against two SettingsFile instances instead of the one SettingsFile.
            // Instance the shared addResetButton helper hardcodes (see its own comment in
            // K2Page.cs - it's shared by Node/Lift/Dock too, so it can't just be changed there).
            addLandingResetButton(panel.Q<Foldout>("advanced_foldout"));

            // Landing with a chosen stage.
            stage_drop_atmo = SetupStageChoice("land_stage_atmo");
            stage_drop_vac = SetupStageChoice("land_stage_vac");
            BindDropHeight(panel.Q<TextField>("drop_stage_at_km_atmo"), pilot.settings_atmo);
            BindDropHeight(panel.Q<TextField>("drop_stage_at_km_vac"), pilot.settings_vac);

            // Precision Landing orbit gate - see UpdateOrbitGate's own comment. Reverts the toggle
            // the instant someone tries to turn it on from too high an orbit, rather than letting
            // it sit on and only get refused once Circularize.cs actually runs. Vacuum-only, same
            // as the toggle itself.
            orbit_gate_label = panel.Q<Label>("precision_landing_orbit_gate_vac");
            precision_plan_label = panel.Q<Label>("precision_plan_vac");
            precision_plan_label_atmo = panel.Q<Label>("precision_plan_atmo");
            atmo_precision_note = panel.Q<Label>("atmo_precision_note");
            reentry_calibration = panel.Q<VisualElement>("reentry_calibration_atmo");
            reentry_suggestion = panel.Q<Label>("reentry_suggestion_atmo");
            panel.Q<Button>("reentry_apply_atmo").clicked += () => pilot.ApplySuggestedDragScale();
            pilot.settings_vac.precision_landing.listeners += v =>
            {
                if (!v) return;
                if (!PeriapsisTooHigh(out double periapsisAlt_m))
                    return;

                pilot.logger.LogInfo($"[LandingUI] Precision Landing refused - periapsis too high " +
                    $"({periapsisAlt_m:n0} m, max {Precision.PrecisionStart.MaxPeriapsisAltitude:n0} m).");
                pilot.settings_vac.precision_landing.V = false;
            };

            return true;
        }

        DropdownField SetupStageChoice(string name)
        {
            DropdownField drop = panel.Q<DropdownField>(name);
            drop?.RegisterValueChangedCallback(evt =>
            {
                int index = LandingStages.IndexOfLabel(stage_choices, evt.newValue);
                if (index < 0 || pilot.isRunning || index == pilot.landing_stage_choice)
                    return;
                pilot.landing_stage_choice = index;
                pilot.logger.LogInfo($"[LandingUI] land with stage {index + 1} ({evt.newValue})");
            });
            return drop;
        }

        // A text field, so that empty means automatic. A value that is not a number of km is
        // shown in red and counts as automatic.
        static void BindDropHeight(TextField field, LandingSettings settings)
        {
            if (field == null)
                return;
            void Show(string v)
            {
                field.SetValueWithoutNotify(v ?? "");
                field.style.color = double.IsNaN(LandingStages.ParseDropHeight(v))
                    ? new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.88f, 0.52f, 0.52f))
                    : new UnityEngine.UIElements.StyleColor(StyleKeyword.Null);
            }
            Show(settings.drop_stage_at_km.V);
            settings.drop_stage_at_km.listeners += Show;
            field.RegisterValueChangedCallback(evt => settings.drop_stage_at_km.V = (evt.newValue ?? "").Trim());
        }

        // The stage list of the active vessel, in the staging stack's order; the choice falls
        // back to the current stage when its stage is gone. Locked while a landing runs.
        void UpdateStageChoices()
        {
            var vessel = pilot.current_vessel?.VesselComponent;
            List<LandingStageData> list = vessel == null ? null : LandingPilot.ReadStageList(vessel, out _);
            if (list != null && list.Count > 0)
                stage_choices = list;
            else if (!pilot.isRunning)
                stage_choices = new List<LandingStageData>();
            if (!pilot.isRunning && pilot.landing_stage_choice >= stage_choices.Count)
                pilot.landing_stage_choice = 0;

            List<string> labels = LandingStages.Labels(stage_choices);
            string shown;
            if (pilot.isRunning && pilot.staged_landing_stage >= 0)
                shown = $"Stage {pilot.staged_drops_left + 1} (landing stage, {pilot.staged_drops_left} drop(s) left)";
            else if (stage_choices.Count == 0)
                shown = "No stage data";
            else
                shown = LandingStages.Label(pilot.landing_stage_choice, stage_choices[pilot.landing_stage_choice]);
            bool atmo_parachute = pilot.settings_atmo.mode.V == LandingMethod.Parachute;
            foreach (DropdownField drop in new[] { stage_drop_atmo, stage_drop_vac })
            {
                if (drop == null)
                    continue;
                drop.choices = labels;
                drop.SetValueWithoutNotify(shown);
                drop.SetEnabled(!pilot.isRunning && labels.Count > 1);
            }
            // The stage choice is for powered braking only.
            stage_drop_atmo?.Show(!atmo_parachute);
        }

        // Same idea as K2Page.addResetButton (Node/Lift/Dock's shared helper), but resets the
        // land_atmo AND land_vac SettingsFile instances together, since Landing's settings no
        // longer live in the one SettingsFile.Instance that helper hardcodes.
        void addLandingResetButton(VisualElement parent)
        {
            if (parent == null)
                return;

            Button reset_bt = new Button() { text = "Reset" };
            reset_bt.name = "reset_advanced";
            reset_bt.style.height = 30;
            reset_bt.style.marginTop = 8;

            parent.Add(reset_bt);

            reset_bt.RegisterCallback<ClickEvent>(evt =>
            {
                SettingsFile.Get("land_atmo")?.Reset("land");
                SettingsFile.Get("land_vac")?.Reset("land");
            });
        }

        // Shows exactly one of each profile-pair (landing_mode_group_*/*_settings_panel) at a
        // time, based on LandingProfile.IsAtmospheric - same per-tick refresh pattern
        // UpdateOrbitGate below uses, so a body change while this tab's open switches panels
        // immediately instead of needing a tab close/reopen.
        void UpdateProfilePanels()
        {
            bool atmo = LandingProfile.IsAtmospheric;
            landing_mode_group_atmo.Show(atmo);
            landing_mode_group_vac.Show(!atmo);
            atmo_settings_panel.Show(atmo);
            vac_settings_panel.Show(!atmo);
        }

        TargetBlock SetupTarget(string suffix, LandingSettings settings)
        {
            var block = new TargetBlock
            {
                suffix = suffix,
                settings = settings,
                drop = panel.Q<DropdownField>("waypoint_drop" + suffix),
                pick = panel.Q<Button>("pick_target" + suffix),
                status = panel.Q<Label>("pick_status" + suffix),
            };
            block.drop.RegisterCallback<ChangeEvent<string>>(evt =>
            {
                if (evt.newValue != null && block.choices.TryGetValue(evt.newValue, out var waypoint))
                {
                    block.settings.target_latitude.V = (float)waypoint.latitude;
                    block.settings.target_longitude.V = (float)waypoint.longitude;
                }
            });
            block.pick.text = PickText;
            block.pick.clicked += () => OnPickClicked(block);
            SetStatus(block, "");
            return block;
        }

        static void SetStatus(TargetBlock block, string text)
        {
            block.status.text = text ?? "";
            block.status.Show(!string.IsNullOrEmpty(text));
        }

        // The launch pad entry, when the game gives its location and the vessel is on that body.
        ReduxWaypoint? KscEntry(string bodyName)
        {
            // Another body than the home world: no lookup (it would fail and build a reason every second).
            string home_name = null;
            try { home_name = GameManager.Instance?.Game?.UniverseModel?.HomeWorld?.Name; }
            catch (Exception) { /* treated as no home world */ }
            if (home_name == null || home_name != bodyName)
                return null;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < ksc_retry_time)
                return null;
            if (!KscLocation.TryGet(out string ksc_body, out double raw_lat, out double raw_lon))
            {
                ksc_retry_time = now + 1f;
                return null;
            }
            ksc_retry_time = -1;
            if (ksc_body != bodyName)
                return null;
            // WaypointChoices does not normalize: the label and the target must be in range.
            if (!TargetPoint.TryNormalize(raw_lat, raw_lon, out double lat, out double lon))
            {
                ReduxWaypoints.LogOnce(string.Format(CultureInfo.InvariantCulture,
                    "[Landing] target: KSC coordinates rejected ({0:R}, {1:R}), entry omitted.", raw_lat, raw_lon));
                return null;
            }
            return new ReduxWaypoint { name = WaypointChoices.KscName, body_name = ksc_body, latitude = lat, longitude = lon };
        }

        void buildWaypointList(TargetBlock block)
        {
            var waypoint_choices = block.choices;
            var waypoint_drop = block.drop;
            waypoint_choices.Clear();

            var body = pilot.current_vessel?.currentBody();
            var choices = new List<string>();
            var built = new List<WaypointChoice>();

            if (body != null)
            {
                // A point picked on another body means nothing here: drop it.
                if (picked_point != null && picked_point.Value.body_name != body.Name)
                    picked_point = null;

                // KSC and the picked point, the player's own waypoints, then the game's (missions
                // that need a precise point create one when they activate): see WaypointChoices.
                var player = ReduxWaypoints.available ? ReduxWaypoints.GetWaypointsForBody(body.Name) : new List<ReduxWaypoint>();
                built = WaypointChoices.Build(player, ReduxWaypoints.GetGameWaypoints(), body.Name,
                    KscEntry(body.Name), picked_point);
                foreach (var choice in built)
                {
                    waypoint_choices[choice.Label] = choice.Waypoint;
                    choices.Add(choice.Label);
                }
            }

            // This runs every UI tick (see onUpdateUI()) so the list stays current if a waypoint's
            // added while this tab's open. A rebuild NEVER writes the target settings (not even on a
            // body change): only an explicit choice does (dropdown change, Pick on map, typing in the
            // fields). Everything below goes through SetValueWithoutNotify.
            string current = waypoint_drop.value;

            // Keep the shown entry only while it still matches the saved target (the player may have typed
            // other coordinates since); otherwise fall through to the saved-target / Custom path.
            if (current != null && choices.Contains(current)
                && waypoint_choices.TryGetValue(current, out var shown)
                && WaypointChoices.IndexOfSaved(new List<WaypointChoice> { new WaypointChoice { Label = current, Waypoint = shown } },
                    block.settings.target_latitude.V, block.settings.target_longitude.V) == 0)
            {
                waypoint_drop.choices = choices;
                waypoint_drop.SetValueWithoutNotify(current);
                return;
            }

            if (body == null)
            {
                waypoint_drop.choices = new List<string> { "No body" };
                waypoint_drop.SetValueWithoutNotify("No body");
                return;
            }

            // Show the saved target: the entry sitting on it, else a "Custom (lat, lon)" entry listed
            // last. The Custom entry is not in waypoint_choices, so choosing it writes nothing (it is
            // the saved value already).
            double saved_lat = block.settings.target_latitude.V;
            double saved_lon = block.settings.target_longitude.V;
            int index = WaypointChoices.IndexOfSaved(built, saved_lat, saved_lon);
            string selected;
            if (index >= 0)
                selected = built[index].Label;
            else
            {
                selected = WaypointChoices.Custom(body.Name, saved_lat, saved_lon).Label;
                if (choices.Contains(selected))
                    selected += " #2";
                choices.Add(selected);
            }
            waypoint_drop.choices = choices;
            waypoint_drop.SetValueWithoutNotify(selected);
        }

        // ---- Map target picker (ReduxPlacement) ----

        void UpdatePickButton(TargetBlock block)
        {
            bool available = ReduxPlacement.Available;
            if (block.pick_available == available)
                return;
            block.pick_available = available;
            block.pick.SetEnabled(available);
            block.pick.tooltip = available ? PickTooltip : UnavailableText;
        }

        static GameState? CurrentState()
        {
            try { return GeneralTools.Game?.GlobalGameState?.GetGameState()?.GameState; }
            catch (Exception) { return null; }
        }

        static int CountPlayerWaypoints()
        {
            try { return ReduxWaypoints.GetWaypointsForBody(null).Count; }
            catch (Exception) { return -1; }
        }

        // Switches flight view to the map with the game's own toggle (ViewController.DataProvider.
        // SetMapEnabled, documented as "transitions the game state to map view"). False when that is
        // not reachable: the caller then tells the player to open the map himself.
        bool OpenMap()
        {
            try
            {
                var provider = GeneralTools.Game?.ViewController?.DataProvider;
                if (provider == null)
                {
                    ReduxWaypoints.LogOnce("[Landing] target: cannot open the map: ViewController.DataProvider is null.");
                    return false;
                }
                provider.SetMapEnabled(true);
                pilot.logger.LogInfo("[Landing] target: map view requested (ViewDataProvider.SetMapEnabled(true)).");
                return true;
            }
            catch (Exception e)
            {
                ReduxWaypoints.LogOnce($"[Landing] target: cannot open the map: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        void OnPickClicked(TargetBlock block)
        {
            // A click while a pick waits (or waits for the map) cancels it.
            if (pick_owner != null || ReduxPlacement.Waiting)
            {
                CancelPick("cancelled from the button");
                return;
            }
            if (!ReduxPlacement.Available)
            {
                SetStatus(block, UnavailableText);
                return;
            }

            SetStatus(block, "");
            pick_owner = block;
            pick_vessel = pilot.current_vessel?.VesselComponent;
            pick_count_check = null;
            block.pick.text = WaitingText;

            if (CurrentState() == GameState.FlightView)
            {
                if (OpenMap())
                {
                    // Placement starts once the map view is up (UpdatePick), so Redux never gets a
                    // BeginPlacement while the game is still switching views.
                    pick_begin_pending = true;
                    pick_begin_deadline = UnityEngine.Time.realtimeSinceStartup + MapOpenTimeout;
                    return;
                }
                SetStatus(block, OpenMapText);
            }
            StartPlacement();
        }

        void StartPlacement()
        {
            pick_begin_pending = false;
            var block = pick_owner;
            if (block == null)
                return;

            // Counted when Begin is called, not at the click: a waypoint added by hand while waiting
            // for the map must not look like one saved by Redux's own window.
            pick_waypoints_before = CountPlayerWaypoints();
            bool started = ReduxPlacement.Begin(
                (body, lat, lon) => OnPicked(block, body, lat, lon),
                message => OnPickFailed(block, message));
            if (!started)
            {
                // Members or Instance missing (logged once by ReduxPlacement).
                pick_owner = null;
                block.pick.text = PickText;
                SetStatus(block, UnavailableText);
            }
            // A Begin that threw has already called OnPickFailed, which restored the button.
        }

        void OnPickFailed(TargetBlock block, string message)
        {
            if (pick_owner == block)
                pick_owner = null;
            pick_begin_pending = false;
            block.pick.text = PickText;
            if (message != "cancelled")
                SetStatus(block, message);
        }

        void OnPicked(TargetBlock block, string body, double raw_lat, double raw_lon)
        {
            if (pick_owner == block)
                pick_owner = null;
            block.pick.text = PickText;
            // Redux's own window, if it handled the same click, may run after us: checked next tick.
            pick_count_check = block;

            if (!TargetPoint.TryNormalize(raw_lat, raw_lon, out double lat, out double lon))
            {
                pilot.logger.LogWarning(string.Format(CultureInfo.InvariantCulture,
                    "[Landing] target: picked point rejected, not a valid position: raw ({0}, {1:R}, {2:R}).", body, raw_lat, raw_lon));
                SetStatus(block, "Picked point is not a valid position: ignored.");
                return;
            }

            string current = pilot.current_vessel?.currentBody()?.Name;
            string refusal = TargetPoint.Accept(body, current);
            if (refusal != null)
            {
                pilot.logger.LogInfo("[Landing] target: " + refusal);
                SetStatus(block, refusal);
                return;
            }

            picked_point = new ReduxWaypoint
            {
                name = WaypointChoices.PickedName,
                body_name = body,
                latitude = lat,
                longitude = lon,
                from_mission = false,
            };
            buildWaypointList(block);
            foreach (var entry in block.choices)
            {
                if (entry.Value.name == WaypointChoices.PickedName && !entry.Value.from_mission)
                {
                    block.drop.SetValueWithoutNotify(entry.Key);
                    break;
                }
            }
            // Written last: the rebuild above may have auto-selected another entry.
            block.settings.target_latitude.V = (float)lat;
            block.settings.target_longitude.V = (float)lon;

            pilot.logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Landing] target: target set from the map to ({0:0.0000}, {1:0.0000}) on {2} ({3} profile).",
                lat, lon, body, block.suffix.TrimStart('_')));
            SetStatus(block, string.Format(CultureInfo.InvariantCulture, "Target set to ({0:0.00}, {1:0.00}).", lat, lon));
        }

        /// <summary>
        /// Abandons a waiting pick (or one waiting for the map): Redux placement cancelled, our
        /// handlers removed, button text restored. Safe to call when nothing waits. Called by the
        /// per-tick checks below and by LandingPilot.onReset (vessel change, scene change, a pilot start).
        /// </summary>
        internal void CancelPick(string reason)
        {
            if (pick_owner == null && !ReduxPlacement.Waiting)
                return;
            var block = pick_owner;
            pick_owner = null;
            pick_begin_pending = false;
            ReduxPlacement.Cancel();
            if (block != null)
                block.pick.text = PickText;
            pilot.logger.LogInfo($"[Landing] target: pick cancelled ({reason}).");
        }

        // Every UI tick, visible or not: drives ReduxPlacement.Tick (how a cancel in Redux is seen),
        // the delayed start once the map is up, and the cancel conditions.
        void UpdatePick()
        {
            ReduxPlacement.Tick();

            if (pick_count_check != null)
            {
                var block = pick_count_check;
                pick_count_check = null;
                int after = CountPlayerWaypoints();
                if (pick_waypoints_before >= 0 && after > pick_waypoints_before)
                {
                    pilot.logger.LogWarning($"[Landing] target: Redux's Waypoints window saved a waypoint from the pick (player waypoints {pick_waypoints_before} -> {after}).");
                    SetStatus(block, string.IsNullOrEmpty(block.status.text)
                        ? ReduxAlsoSavedText
                        : block.status.text + "\n" + ReduxAlsoSavedText);
                }
            }

            if (pick_owner == null)
                return;

            var state = CurrentState();
            string reason = null;
            if (!isVisible)
                reason = "Land tab hidden";
            else if (!K2D2_Plugin.IsMainWindowOpen)
                reason = "window closed";
            else if (pilot.isRunning)
                reason = "landing started";
            else if (!ReferenceEquals(pilot.current_vessel?.VesselComponent, pick_vessel))
                reason = "vessel changed";
            else if (state != GameState.FlightView && state != GameState.Map3DView)
                reason = "left flight and map view";
            else if (pick_owner != VisibleTarget)
                reason = "landing profile changed";
            else if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Escape))
                reason = "Escape";

            if (reason != null)
            {
                CancelPick(reason);
                return;
            }

            if (pick_begin_pending)
            {
                if (state == GameState.Map3DView)
                    StartPlacement();
                else if (UnityEngine.Time.realtimeSinceStartup > pick_begin_deadline)
                {
                    pilot.logger.LogInfo("[Landing] target: the map view did not open, starting the pick anyway.");
                    SetStatus(pick_owner, OpenMapText);
                    StartPlacement();
                }
            }
        }

        void AddInfoRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("advanced-info-row");

            var label_el = new Label(label);
            label_el.AddToClassList("advanced-info-row-label");
            row.Add(label_el);

            var value_el = new Label(value);
            value_el.AddToClassList("advanced-info-row-value");
            row.Add(value_el);

            landing_infos.Add(row);
        }

        // An info row whose value comes from game queries that may throw: show "?" instead of
        // breaking the whole table, and log the first failure only.
        void AddSafeInfoRow(string label, System.Func<string> value)
        {
            string text;
            try
            {
                text = value();
            }
            catch (System.Exception e)
            {
                text = "?";
                if (!readout_failure_logged)
                {
                    readout_failure_logged = true;
                    pilot.logger.LogInfo("[LandingUI] legs/parachutes readout failed: " + e);
                }
            }
            AddInfoRow(label, text);
        }

        // Always-visible "am I about to hit something" readout, next to the Brake/Touch Down
        // buttons regardless of whether LANDING INFO below is expanded. Kept separate from
        // updateContext()/landing_infos so it never depends on that Foldout's collapsed state.
        void updateCollisionStatus()
        {
            collision_value.text = pilot.collision_detected ? "Detected" : "None detected";
        }

        // The current periapsis altitude is above PrecisionStart.MaxPeriapsisAltitude (state
        // vectors, valid for any conic). False without a vessel.
        bool PeriapsisTooHigh(out double periapsisAlt_m)
        {
            periapsisAlt_m = double.NaN;
            var vessel = pilot.current_vessel?.VesselComponent;
            if (vessel?.Orbit == null)
                return false;
            KSP.Sim.IKeplerPatch orbit = vessel.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            LandingTargeting.OrbitalElementsFromStateVectors(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter, out _, out _, out double periapsis_radius);
            periapsisAlt_m = periapsis_radius - body.radius;
            return periapsisAlt_m > Precision.PrecisionStart.MaxPeriapsisAltitude;
        }

        // Precision Landing orbit gate. The status_bar console/status lines get wiped and
        // rebuilt every single UI tick (see status_bar.Reset() below), so a one-shot message from
        // the precision_landing toggle listener (onInit above) would only ever be visible for a
        // single frame. This runs every tick instead, keeping a small persistent label under the
        // toggle in sync with the same periapsis check (and message) as the start check.
        void UpdateOrbitGate()
        {
            bool too_high = PeriapsisTooHigh(out _);
            orbit_gate_label.Show(too_high);
            if (!too_high)
                return;
            orbit_gate_label.text = Precision.PrecisionStart.PeriapsisTooHighMessage;

            // Only before a start: once running, the orbit is expected to move and must not cancel
            // the landing's precision mode.
            if (!pilot.isRunning && pilot.settings_vac.precision_landing.V)
                pilot.settings_vac.precision_landing.V = false;
        }

        // The last precision plan (budget, predicted miss), its warnings, or the final distance.
        void UpdatePrecisionPlanLabel()
        {
            string text = "";
            if (pilot.precision_run || !string.IsNullOrEmpty(pilot.precision_final_message))
            {
                var lines = new List<string>();
                if (!string.IsNullOrEmpty(pilot.precision_final_message))
                    lines.Add(pilot.precision_final_message);
                if (!string.IsNullOrEmpty(pilot.precision_summary))
                    lines.Add(pilot.precision_summary);
                lines.AddRange(pilot.precision_warnings);
                text = string.Join("\n", lines);
            }
            precision_plan_label.text = text;
            precision_plan_label.Show(text.Length > 0);
            precision_plan_label_atmo.text = text;
            precision_plan_label_atmo.Show(text.Length > 0);
        }

        // Atmo: the note (Propulsive mode: precision is for Parachute mode) and, after a precision
        // return, the suggested reentry drag scale (applied only by the Apply button).
        void UpdateReentrySuggestion()
        {
            bool parachute = pilot.settings_atmo.mode.V == LandingMethod.Parachute;
            atmo_precision_note.Show(!parachute);
            bool suggestion = !double.IsNaN(pilot.suggested_drag_scale) && !pilot.isRunning;
            reentry_calibration.Show(suggestion);
            if (suggestion)
                reentry_suggestion.text = Precision.DragCalibration.SuggestionMessage(pilot.suggested_drag_scale, pilot.settings_atmo.reentry_drag_scale.V);
        }

        // A planned descent's own phases (LandingPilotDescent.cs), else the precision ones.
        string PhaseText(LandingPilot.Mode mode)
        {
            if (pilot.deorbit_run)
            {
                switch (mode)
                {
                    case LandingPilot.Mode.Deorbit: return "Deorbit burn";
                    case LandingPilot.Mode.PreBrake: return "Pre-brake before the atmosphere";
                    case LandingPilot.Mode.AeroCoast: return "Atmospheric coast, surface retrograde";
                }
            }
            if (mode == LandingPilot.Mode.AeroCoast)
                return "Atmospheric coast: slowing down to cool";
            return PrecisionPhaseText(mode);
        }

        static string PrecisionPhaseText(LandingPilot.Mode mode)
        {
            switch (mode)
            {
                case LandingPilot.Mode.Calibrate: return "Precision: measuring the body's rotation";
                case LandingPilot.Mode.OrbitPrep: return "Precision: orbit preparation";
                case LandingPilot.Mode.PlaneChange: return "Precision: plane change";
                case LandingPilot.Mode.Deorbit: return "Precision: deorbit burn";
                case LandingPilot.Mode.MidCourse: return "Precision: mid-course correction";
                case LandingPilot.Mode.ReentryCoast: return "Precision: coast to the service module jettison";
                case LandingPilot.Mode.Jettison: return "Precision: jettisoning the service module";
                default: return null;
            }
        }

        public void updateContext()
        {
            landing_infos.Clear();

            AddInfoRow("Fall Speed", $"{pilot.current_falling_speed:n2} m/s");
            AddInfoRow("Altitude", StrTool.DistanceToString(pilot.altitude));

            // Legs and parachutes, shown even before starting: "No legs" tells the player the
            // landing will go ahead without any.
            var vessel = pilot.current_vessel?.VesselComponent;
            if (vessel != null)
            {
                AddSafeInfoRow("Legs", () => LandingGear.Summary(vessel));
                if (LandingProfile.IsAtmospheric)
                {
                    AddSafeInfoRow("Parachutes", () =>
                    {
                        ParachuteCounts chutes = Parachutes.Count(vessel);
                        return chutes.Total == 0 ? "None"
                            : $"{chutes.Deployed} deployed, {chutes.SemiDeployed} semi, {chutes.Armed} armed, {chutes.Stowed} stowed";
                    });
                }
            }

            if (pilot.collision_detected)
            {
                // Braking rows mean nothing under a canopy: no burn is planned.
                if (!ParachuteLanding)
                {
                    AddInfoRow("Collision In", StrTool.DurationToString(pilot.adjusted_collision_UT - GeneralTools.Game.UniverseModel.UniverseTime));
                    AddInfoRow("Collision Speed", $"{pilot.speed_collision:n2} m/s");
                    AddInfoRow("Start Burn In", StrTool.DurationToString(pilot.startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime));
                    AddInfoRow("Burn Duration", $"{pilot.burn_duration:n2} s");
                }

                // Precision landing readouts - informational only, nothing steers toward this
                // yet. Predicted Landing lets players confirm the lat/lon math is sane in-game
                // (compare it against where the ship actually comes down).
                AddInfoRow("Predicted Landing", $"{pilot.predicted_landing_lat:n2}, {pilot.predicted_landing_lon:n2}");
                if (pilot.settings_vac.precision_landing.V && !pilot.PrecisionActive)
                    AddInfoRow("Target Error", StrTool.DistanceToString((float)pilot.target_error_m));
            }

            // Precision landing: the phase and the distance to the target (predicted from the
            // braking simulation every 2 s outside the burns, live in Touch Down).
            if (pilot.PrecisionActive)
            {
                AddInfoRow("Precision Phase", PhaseText(pilot.mode) ?? pilot.mode.ToString());
                AddInfoRow(pilot.precision_distance_label, double.IsNaN(pilot.precision_distance_m)
                    ? "no prediction yet" : StrTool.DistanceToString((float)pilot.precision_distance_m));
            }

            // Same idea as Lift's LIFT INFO: numeric telemetry from whichever sub-controller is
            // actually driving the vessel right now (TouchDown's Max/Delta Speed while braking;
            // WarpTo has nothing to report during the warp phases) folds into this table via
            // ExecuteController.UpdateInfoRows, instead of scrolling through the console text
            // alongside the narrative status line below.
            if (pilot.isRunning)
            {
                pilot.current_executor?.UpdateInfoRows(AddInfoRow);

                // Atmospheric descent: the hottest part against its max temperature (heat guard).
                if (!string.IsNullOrEmpty(pilot.heat_readout))
                    AddInfoRow("Hottest Part", pilot.heat_readout);

                // Landing with a chosen stage: the drop height and the landing stage's Δv.
                if (pilot.StagedBraking && pilot.stage_plan_valid)
                    AddInfoRow("Stage Drop", $"{pilot.staged_drops_left} left, at {StrTool.DistanceToString((float)pilot.stage_drop_height)} at the latest");

                if (isRunning && pilot.burn_dV.burned_dV > 0)
                    AddInfoRow("Burned", $"{pilot.burn_dV.burned_dV:n1} m/s");
            }
        }

        public override bool onUpdateUI()
        {
            // Before the visibility gate: a pick must be watched (and cancelled) while hidden too.
            UpdatePick();

            if (!base.onUpdateUI())
                return false;

            // Atmospheric precision landing is for Parachute mode only: keep the toggle off otherwise.
            if (pilot.settings_atmo.precision_landing.V && pilot.settings_atmo.mode.V != LandingMethod.Parachute)
                pilot.settings_atmo.precision_landing.V = false;

            updateCollisionStatus();
            updateContext();
            var target = VisibleTarget;
            buildWaypointList(target);
            UpdatePickButton(target);
            UpdateOrbitGate();
            UpdatePrecisionPlanLabel();
            UpdateReentrySuggestion();
            UpdateProfilePanels();
            UpdateStageChoices();

            status_bar.Reset();

            // Collision prediction only matters for the info panel above; it has nothing to do
            // with the mode display below, so this no longer early-returns on
            // !collision_detected (that used to freeze the touch_down button/status text
            // whenever collision detection legitimately flipped false mid-descent).
            var state = GeneralTools.Game.GlobalGameState.GetState();
            if (state != GameState.FlightView)
            {
                status_bar.Console("Landing is only available in Fligh View");
                return true;
            }

            touch_down.Show(pilot.mode != LandingPilot.Mode.TouchDown && !ParachuteLanding);
            if (!pilot.isRunning)
                run_button.label = IdleLabel; // follows the mode selector
            if (pilot.isRunning)
            {
                switch (pilot.mode)
                {
                    default:
                    case LandingPilot.Mode.Off: break;
                    case LandingPilot.Mode.Calibrate:
                    case LandingPilot.Mode.OrbitPrep:
                    case LandingPilot.Mode.PlaneChange:
                    case LandingPilot.Mode.Deorbit:
                    case LandingPilot.Mode.MidCourse:
                    case LandingPilot.Mode.ReentryCoast:
                    case LandingPilot.Mode.Jettison:
                    case LandingPilot.Mode.PreBrake:
                    case LandingPilot.Mode.AeroCoast:
                        status_bar.Status(PhaseText(pilot.mode));
                        break;
                    case LandingPilot.Mode.Pause:
                        status_bar.Status("Pause");
                        break;
                    case LandingPilot.Mode.QuickWarp:
                        status_bar.Status("Quick Warp");
                        break;
                    case LandingPilot.Mode.RotationWarp:
                        status_bar.Warning("Rotating Warp");
                        break;
                    case LandingPilot.Mode.Waiting:
                        status_bar.Status($"Waiting : {StrTool.DurationToString(pilot.startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime)}");
                        break;
                    case LandingPilot.Mode.Brake:
                        if (pilot.brake_result_valid && pilot.brake_result.Status == BrakeStatus.Impossible)
                            status_bar.Error("Cannot stop before the ground!");
                        else
                            status_bar.Warning($"Brake !");
                        break;
                    case LandingPilot.Mode.TouchDown:
                        if (pilot.brake.cannot_stop)
                            status_bar.Error("Cannot stop before the ground!");
                        else if (pilot.PrecisionActive && pilot.precision_drop_message != null)
                            status_bar.Warning(pilot.precision_drop_message);
                        else
                            status_bar.Warning($"Touch Down...");
                        break;
                    case LandingPilot.Mode.Parachute:
                        // Alerts only: without engines, nothing can be done about them.
                        if (pilot.parachute_descent.no_parachute_left)
                            status_bar.Error(ParachuteFeasibility.NoParachuteLeftMessage);
                        else if (pilot.parachute_descent.too_fast_under_canopy)
                            status_bar.Error(ParachuteFeasibility.TooFastMessage);
                        else
                            status_bar.Warning("Parachute descent...");
                        break;
                }

                if (pilot.current_executor != null && !string.IsNullOrEmpty(pilot.current_executor.status_line))
                    status_bar.Console(pilot.current_executor.status_line);

                // A staged plan that no longer works, or stopped drops, replace the status line.
                if (!string.IsNullOrEmpty(pilot.stage_message))
                {
                    if (pilot.stage_plan_alarm)
                        status_bar.Error(pilot.stage_message);
                    else
                        status_bar.Console(pilot.stage_message);
                }
                if (!string.IsNullOrEmpty(pilot.last_warning))
                    status_bar.Console(pilot.last_warning);
                // Planned descent: its Δv breakdown; the heat guard's alarms.
                if (pilot.deorbit_run && !string.IsNullOrEmpty(pilot.descent_summary))
                    status_bar.Console(pilot.descent_summary);
                if (!string.IsNullOrEmpty(pilot.heat_message))
                    status_bar.Error(pilot.heat_message);
                if (pilot.PrecisionActive)
                {
                    if (!string.IsNullOrEmpty(pilot.precision_summary))
                        status_bar.Console(pilot.precision_summary);
                    foreach (string warning in pilot.precision_warnings)
                        status_bar.Console(warning);
                }
            }
            else
            {
                // A refused start or a failed phase stays visible until the next successful
                // start; status_bar.Reset() wipes everything else every tick. A precision landing's
                // final distance stays the same way.
                if (!string.IsNullOrEmpty(pilot.last_error))
                    status_bar.Error(pilot.last_error);
                else if (!string.IsNullOrEmpty(pilot.precision_final_message))
                    status_bar.Status(pilot.precision_final_message);
                else
                    status_bar.Status("Landing autopilot not enabled");
            }

            //    UI_Tools.Console("SurfaceVelocity" + StrTool.VectorToString(SurfaceVelocity.vector));

            return true;
        }

    }
}