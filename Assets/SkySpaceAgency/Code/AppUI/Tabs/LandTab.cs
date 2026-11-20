using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Game;
using KSP.Modules;
using KSP.Sim.impl;
using KTools;
using SkySpaceAgency.KSPService;
using SkySpaceAgency.Landing;
using SkySpaceAgency.Landing.Braking;
using SkySpaceAgency.Landing.Heating;
using SkySpaceAgency.Landing.Precision;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// LAND (docs/design/ui/land.png), migrated from the classic Land tab (Landing.uxml, LandingUI.cs,
    /// LandingSettings.setupUI/setupBasicUI, TouchDown.SetupFullUI/SetupBasicUI). Two profiles, as the
    /// classic tab: the atmospheric one (mode, parachute precision return, drag scale) and the vacuum one
    /// (precision landing), the shown one following the body (LandingProfile.IsAtmospheric); each bound
    /// once to its own LandingSettings. Centre: the descent profile (LandProfileView). Side: heat,
    /// Δv, touchdown, time to impact, the landing info table and the pilot's status. Inventory:
    /// docs/superpowers/notes/new-ui-inventory/land.md.
    /// </summary>
    public class LandTab : AppTab
    {
        public LandTab(AppTabContext context) : base(context) { }
        public override string Code => "land";
        public override string Label => "Land";

        // ---- pilot and settings ----

        LandingPilot _pilot;
        float _nextResolve;
        LandingSettings _atmo, _vac;
        TouchDown.TouchDownSettings _tdAtmo, _tdVac;
        SettingsFile _atmoFile, _vacFile;
        bool? _sampleAtmo;

        public LandingPilot Pilot => _pilot;
        public LandingSettings AtmoSettings => _atmo;
        public LandingSettings VacSettings => _vac;
        public bool Atmospheric => _sampleAtmo ?? LandingProfile.IsAtmospheric;

        public AppPilotStatus Status { get; } = new AppPilotStatus();

        // ---- plan ----

        public AppCard PlanCard { get; private set; }
        public VisualElement AtmoPlan { get; private set; }
        public VisualElement VacPlan { get; private set; }
        public AppSection AtmoAdvanced { get; private set; }
        public AppSection VacAdvanced { get; private set; }
        public LandTargets Targets { get; private set; }
        public AppSegmented ModeChoice { get; private set; }
        public AppDropdown StageAtmo { get; private set; }
        public AppDropdown StageVac { get; private set; }
        AppSwitch _precisionAtmo;
        AppNote _atmoPrecisionNote, _atmoGateNote, _orbitGate, _planNoteAtmo, _planNoteVac, _calibrationNote;
        VisualElement _calibration, _boostBack, _vacPrecisionAdvanced;
        List<LandingStageData> _stageChoices = new();

        // ---- view ----

        public AppCard ViewCard { get; private set; }
        public LandProfileView Profile { get; private set; }
        readonly List<LandGeoPoint> _track = new();
        float _nextTrack;

        // ---- side ----

        public LandHeatGauge Gauge { get; private set; }
        Label _hottest, _heatNote;
        VisualElement _heatBlock;
        AppNote _noHeat;
        public AppTile DvNeeded { get; private set; }
        public AppTile DvOnBoard { get; private set; }
        public AppTile Touchdown { get; private set; }
        public AppTile Impact { get; private set; }
        public AppInfoTable Collision { get; private set; }
        public AppInfoTable Info { get; private set; }
        public AppSection InfoSection { get; private set; }
        public AppPill Feasible { get; private set; }
        readonly HashSet<string> _infoKeys = new(), _infoSeen = new();
        Action<string, string> _addInfo;

        // ---- actions, timeline, hud ----

        public AppButton TouchDownButton { get; private set; }
        public AppButton AbortButton { get; private set; }
        public AppButton MainButton { get; private set; }
        int _timelineKey = -1;
        List<string> _steps = new();
        int _step = -1, _runDrops;
        bool _wasRunning;
        float _nextSlow, _nextHeat, _nextProfile;
        HudState _hud;
        double _engineRatio = double.NaN;
        string _engineName;

        public override bool IsRunning => _pilot != null && _pilot.isRunning;
        public override SkySpaceAgency.Controller.Pilot ShownPilot => _pilot;
        public override HudState Hud => IsRunning ? _hud : null;

        // ---------------------------------------------------------------- build

        void ResolveSettings()
        {
            if (_pilot != null)
            {
                _atmo = _pilot.settings_atmo;
                _vac = _pilot.settings_vac;
                _tdAtmo = _pilot.brake.AtmoSettings;
                _tdVac = _pilot.brake.VacSettings;
                _atmoFile = SettingsFile.Get("land_atmo");
                _vacFile = SettingsFile.Get("land_vac");
                return;
            }
            // Editor, or before the campaign loads: the same classes on files kept in memory.
            _atmoFile = SettingsFile.InMemory();
            _vacFile = SettingsFile.InMemory();
            _atmo = new LandingSettings(_atmoFile, atmospheric: true);
            _vac = new LandingSettings(_vacFile, atmospheric: false);
            _tdAtmo = new TouchDown.TouchDownSettings(_atmoFile);
            _tdVac = new TouchDown.TouchDownSettings(_vacFile);
        }

        protected override void OnBuild(AppPage page)
        {
            _pilot = Context.Pilot<LandingPilot>();
            _addInfo = AddInfo;
            BuildPlan(page);
            BuildView(page);
            BuildSide(page);
            BuildActions(page);
            ShowProfile(Atmospheric);
            UpdateTimeline();
        }

        void BuildPlan(AppPage page)
        {
            page.Plan.Clear();
            ResolveSettings();
            Targets = new LandTargets(() => _pilot, _vac, _atmo);
            PlanCard = new AppCard("Landing plan");
            AtmoPlan = BuildAtmoPlan();
            VacPlan = BuildVacPlan();
            PlanCard.Add(AtmoPlan);
            PlanCard.Add(VacPlan);
            page.Plan.Add(PlanCard);
        }

        VisualElement BuildAtmoPlan()
        {
            var box = Ui.Box("ssa-land-plan");
            box.name = "land-plan-atmo";
            ModeChoice = AppBind.Segmented(_atmo.mode, new[] { LandingMethod.Propulsive, LandingMethod.Parachute }, new[] { "Propulsive", "Parachutes" });
            ModeChoice.name = "land-mode";
            box.Add(ModeChoice);
            box.Add(Targets.Atmo);
            _planNoteAtmo = PlanNote(box);

            _calibration = Ui.Box("ssa-land-calibration");
            _calibrationNote = new AppNote();
            _calibration.Add(_calibrationNote);
            var apply = new AppButtonRow();
            apply.Add("Apply", () => _pilot?.ApplySuggestedDragScale());
            _calibration.Add(apply);
            _calibration.style.display = DisplayStyle.None;
            box.Add(_calibration);

            StageAtmo = StageChoice("land-stage-atmo");
            box.Add(StageAtmo);

            _precisionAtmo = AppBind.Switch("Precision landing", _atmo.precision_landing);
            _precisionAtmo.tooltip = "Parachute mode: deorbit with the engines, jettison the service module and land under the parachutes near the target";
            box.Add(_precisionAtmo);
            _atmoPrecisionNote = new AppNote("Precision: Parachute mode only on atmospheric bodies.");
            box.Add(_atmoPrecisionNote);
            _atmoGateNote = new AppNote();
            _atmoGateNote.Set("");
            box.Add(_atmoGateNote);
            // LandingSettings.setupBasicUI's rules: precision only where allowed, and kept off elsewhere.
            _atmo.mode.listen(m =>
            {
                _precisionAtmo.SetEnabled(LandingSettings.PrecisionAllowed(m));
                if (!LandingSettings.PrecisionAllowed(m) && _atmo.precision_landing.V) _atmo.precision_landing.V = false;
            });
            _atmo.precision_landing.listeners += v =>
            {
                if (v && !LandingSettings.PrecisionAllowed(_atmo.mode.V)) _atmo.precision_landing.V = false;
            };

            var legs = AppBind.Switch("Deploy legs early", _atmo.deploy_legs_early);
            legs.tooltip = "Propulsive landing: deploy the legs when braking starts instead of at Touch Down";
            box.Add(legs);
            box.Add(HeatGuardRow());

            AtmoAdvanced = Advanced(_atmo, _tdAtmo, atmospheric: true);
            box.Add(AtmoAdvanced);
            return box;
        }

        VisualElement BuildVacPlan()
        {
            var box = Ui.Box("ssa-land-plan");
            box.name = "land-plan-vac";
            var precision = AppBind.Switch("Precision landing", _vac.precision_landing);
            precision.tooltip = "Steer toward a chosen landing site instead of braking straight down wherever the current course lands";
            box.Add(precision);
            _orbitGate = new AppNote();
            _orbitGate.AddToClassList("ssa-land-alert");
            _orbitGate.Set("");
            box.Add(_orbitGate);
            box.Add(Targets.Vac);
            _planNoteVac = PlanNote(box);
            StageVac = StageChoice("land-stage-vac");
            box.Add(StageVac);
            var legs = AppBind.Switch("Deploy legs early", _vac.deploy_legs_early);
            legs.tooltip = "Deploy the landing legs when braking starts instead of at Touch Down";
            box.Add(legs);
            VacAdvanced = Advanced(_vac, _tdVac, atmospheric: false);
            box.Add(VacAdvanced);
            // LandingSettings.setupUI: the target and the precision steering only mean something with precision on.
            _vac.precision_landing.listen(v =>
            {
                Targets.Vac.style.display = v ? DisplayStyle.Flex : DisplayStyle.None;
                _vacPrecisionAdvanced.style.display = v ? DisplayStyle.Flex : DisplayStyle.None;
            });
            return box;
        }

        static AppNote PlanNote(VisualElement parent)
        {
            var n = new AppNote();
            n.AddToClassList("ssa-land-plan-note");
            n.Set("");
            parent.Add(n);
            return n;
        }

        static VisualElement HeatGuardRow()
        {
            var row = Ui.Box("ssa-land-guard");
            row.Add(Ui.Label("Heat guard", "ssa-switch__label"));
            row.Add(Ui.Label("automatic · on at " + Ui.Num((float)HeatGuard.On * 100, "0") + " %, off under " + Ui.Num((float)HeatGuard.Off * 100, "0") + " %", "ssa-land-guard__value"));
            row.tooltip = "Atmospheric descent: above 75 % of an engine's max temperature the pilot thrusts retrograde, until it is under 70 %. It alerts above 90 %. Always on.";
            return row;
        }

        AppDropdown StageChoice(string name)
        {
            var d = new AppDropdown("Land with stage", new List<string>(), -1);
            d.name = name;
            d.tooltip = "Brake with the lower stages first, drop them when they are empty or at the drop height, and land on the chosen stage. The current stage lands as before.";
            d.changed += _ =>
            {
                if (_pilot == null) return;
                int index = LandingStages.IndexOfLabel(_stageChoices, d.Value);
                if (index < 0 || _pilot.isRunning || index == _pilot.landing_stage_choice) return;
                _pilot.landing_stage_choice = index;
                _pilot.logger.LogInfo($"[LandingUI] land with stage {index + 1} ({d.Value})");
            };
            return d;
        }

        static Label SectionTitle(string text) => Ui.Label(text, "ssa-section-title", "ssa-land-subtitle");

        AppSection Advanced(LandingSettings s, TouchDown.TouchDownSettings td, bool atmospheric)
        {
            var sec = new AppSection("Advanced");
            sec.name = atmospheric ? "land-advanced-atmo" : "land-advanced-vac";
            string Deg(float v) => Ui.Num(v, "0") + "°";

            if (atmospheric)
            {
                // Boost-back: K2-D2 flavour only (ModIdentity.IsK2D2), never shown in this mod.
                _boostBack = Ui.Box("ssa-land-group");
                _boostBack.Add(SectionTitle("BOOST-BACK"));
                _boostBack.Add(AppBind.Slider("Entry angle of attack", s.entry_angle_of_attack, Deg));
                var steep = AppBind.Slider("Entry steepness", s.entry_flight_path_angle, Deg);
                steep.style.display = DisplayStyle.None;   // hidden in both flavours, as in the classic tab
                _boostBack.Add(steep);
                _boostBack.Add(Ui.Label("Boost-back flip", "ssa-field__label"));
                _boostBack.Add(AppBind.Segmented(s.boost_back_flip, new[] { FlipStyle.Powered, FlipStyle.Unpowered }, new[] { "Powered", "Unpowered" }));
                _boostBack.style.display = DisplayStyle.None;
                sec.Add(_boostBack);
            }
            else
            {
                _vacPrecisionAdvanced = Ui.Box("ssa-land-group");
                _vacPrecisionAdvanced.Add(SectionTitle("PRECISION LANDING"));
                var steer = AppBind.Slider("Max steering angle", td.steering_max_angle, Deg);
                steer.tooltip = "Largest tilt off vertical the terminal guidance may use below the Touch Down altitude; it also sizes the lateral Δv reserve. 0 refuses precision landing.";
                _vacPrecisionAdvanced.Add(steer);
                sec.Add(_vacPrecisionAdvanced);
            }

            sec.Add(SectionTitle("WARP"));
            var auto = AppBind.Switch("Auto warp", s.auto_warp);
            auto.tooltip = "Warp time before burn";
            sec.Add(auto);
            var warp = Ui.Box("ssa-land-group");
            var duration = AppBind.Field("Rotation warp duration", s.rotation_warp_duration, "s");
            duration.tooltip = "During Rotation Warp, Attitude is checked";
            warp.Add(duration);
            var rot = AppBind.Slider("Safe warp rotation", s.max_rotation, Deg);
            rot.tooltip = "Max angle (stop warp when reached)";
            warp.Add(rot);
            sec.Add(warp);
            s.auto_warp.listen(v => warp.style.display = v ? DisplayStyle.Flex : DisplayStyle.None);

            sec.Add(SectionTitle("BRAKE"));
            sec.Add(AppBind.Slider("Burn before", s.burn_before, v => Ui.Num(v, "0.#") + " s"));
            sec.Add(DropHeight(s));

            sec.Add(SectionTitle("TOUCH DOWN"));
            var alt = AppBind.Slider("Start touch-down altitude", s.start_touchdown_altitude, v => StrTool.DistanceToString(v));
            alt.tooltip = "Altitude for starting Touch-Down Phase";
            sec.Add(alt);
            var angle = AppBind.Slider("Max angle", td.touch_down_max_angle, Deg);
            angle.tooltip = "Max Angle for Final touch Down";
            sec.Add(angle);
            var ratio = AppBind.Slider("Altitude / speed ratio", s.touch_down_ratio,
                v => Ui.Num(v, "0.0#") + (v < 1.2f ? " · safe" : v > 2.3f ? " · danger" : ""));
            ratio.tooltip = "Speed is based on altitude (Safe - Danger)";
            sec.Add(ratio);
            sec.Add(AppBind.Slider("Touch-down speed", s.touch_down_speed, v => Ui.Num(v, "0.0#") + " m/s"));

            if (atmospheric)
            {
                sec.Add(SectionTitle("PRECISION RETURN"));
                var drag = AppBind.Slider("Reentry drag scale", s.reentry_drag_scale, v => Ui.Num(v, "0.00"));
                drag.tooltip = "Multiplies the capsule's drag in the reentry prediction. 1 = the game's drag data. After a precision return, this tab suggests a value from where the capsule really landed.";
                sec.Add(drag);
            }

            var reset = new AppButtonRow();
            reset.Add("Reset", ResetBoth).name = "land-reset" + (atmospheric ? "-atmo" : "-vac");
            reset.tooltip = "Resets both profiles (atmospheric and vacuum)";
            sec.Add(reset);
            return sec;
        }

        // Classic: one Reset for both profile files ("land" chapter).
        void ResetBoth()
        {
            _atmoFile?.Reset("land");
            _vacFile?.Reset("land");
        }

        // A text field so that empty means automatic; a value that is not a number of km shows red.
        static VisualElement DropHeight(LandingSettings s)
        {
            var t = AppBind.Text("Drop lower stage at the latest at (km)", s.drop_stage_at_km, "automatic");
            t.Input.isDelayed = true;
            t.tooltip = "Landing with a chosen stage: drop the lower stage at this height above the ground at the latest. Empty = automatic. A value raises the drop height, never below the safe one.";
            t.Input.RegisterValueChangedCallback(e => s.drop_stage_at_km.V = (e.newValue ?? "").Trim());
            void Show(string v) => t.EnableInClassList("ssa-land-field--invalid", double.IsNaN(LandingStages.ParseDropHeight(v)));
            Show(s.drop_stage_at_km.V);
            s.drop_stage_at_km.listeners += Show;
            return t;
        }

        void BuildView(AppPage page)
        {
            ViewCard = new AppCard("Descent profile", "predicted with drag and heating", sky: true);
            ViewCard.AddToClassList("ssa-land-view");
            SsaPalette.SetBackground(ViewCard, LandPalette.DuskGradient);
            Profile = new LandProfileView();
            ViewCard.Add(Profile);
            Profile.SetModel(new LandProfileModel(), "No descent prediction yet: it appears on a collision course, or once a planned descent starts.");
            page.View.Add(ViewCard);
        }

        void BuildSide(AppPage page)
        {
            var safety = new AppCard("Safety");
            _heatBlock = Ui.Box("ssa-land-heatblock");
            _hottest = Ui.Label("Hottest part · none read", "ssa-land-hottest");
            _heatBlock.Add(_hottest);
            Gauge = new LandHeatGauge((float)HeatGuard.On, (float)HeatGuard.Ceiling);
            _heatBlock.Add(Gauge);
            _heatNote = Ui.Label("", "ssa-land-heatnote");
            _heatBlock.Add(_heatNote);
            safety.Add(_heatBlock);
            _noHeat = new AppNote("No atmosphere here: no reentry heating to watch.");
            safety.Add(_noHeat);

            var tiles = Ui.Box("ssa-tiles", "ssa-land-tiles");
            DvNeeded = new AppTile("Δv needed", "—");
            DvOnBoard = new AppTile("Δv on board", "—", null, AppTile.Kind.Ok);
            Touchdown = new AppTile("Touchdown", "—", "m/s");
            Impact = new AppTile("Time to impact", "—");
            tiles.Add(DvNeeded); tiles.Add(DvOnBoard); tiles.Add(Touchdown); tiles.Add(Impact);
            safety.Add(tiles);

            Collision = new AppInfoTable();
            Collision.Set("Collision", "None detected");
            safety.Add(Collision);
            InfoSection = new AppSection("Landing info");
            Info = new AppInfoTable();
            InfoSection.Add(Info);
            safety.Add(InfoSection);

            safety.Add(Ui.Box("ssa-grow"));
            Feasible = new AppPill(StatusKind.Ok, "");
            Feasible.style.display = DisplayStyle.None;
            safety.Add(Feasible);
            safety.Add(Status.Pill);
            safety.Add(Status.Console);
            safety.Add(Status.Progress);
            page.Side.Add(safety);
            SetHeatVisible(Atmospheric);
        }

        void BuildActions(AppPage page)
        {
            page.Actions.SetNote("");
            TouchDownButton = page.Actions.AddButton("Touch Down", AppButton.Kind.Plain, OnTouchDown);
            AbortButton = page.Actions.AddButton("Abort", AppButton.Kind.Abort, () => { if (_pilot != null) _pilot.isRunning = false; });
            MainButton = page.Actions.AddButton("Brake", AppButton.Kind.Main, OnMain);
            UpdateButtons();
        }

        // ---------------------------------------------------------------- actions

        bool ParachuteLanding => _pilot != null && (_pilot.isRunning
            ? _pilot.mode == LandingPilot.Mode.Parachute || _pilot.reentry_run
            : _pilot.parachute_mode);

        string IdleLabel => _pilot != null ? (_pilot.parachute_mode ? "Land" : "Brake")
            : Atmospheric && _atmo.mode.V == LandingMethod.Parachute ? "Land" : "Brake";

        void OnMain()
        {
            if (_pilot == null) return;
            if (_pilot.isRunning)
            {
                _pilot.isRunning = false;
                return;
            }
            Status.Reset();
            _pilot.isRunning = true;   // refused: last_error shows in the status pill
        }

        void OnTouchDown()
        {
            if (_pilot == null || ParachuteLanding) return;
            _pilot.isRunning = true;
            if (_pilot.isRunning && _pilot.mode != LandingPilot.Mode.Parachute)
                _pilot.setMode(LandingPilot.Mode.TouchDown);
        }

        void UpdateButtons()
        {
            bool has = _pilot != null;
            bool running = IsRunning;
            MainButton.Text = running ? "Landing…" : IdleLabel;
            MainButton.SetEnabled(has);
            AbortButton.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;
            bool td = has && !ParachuteLanding && !(running && _pilot.mode == LandingPilot.Mode.TouchDown);
            TouchDownButton.style.display = td || !has ? DisplayStyle.Flex : DisplayStyle.None;
            TouchDownButton.SetEnabled(has);
        }

        // ---------------------------------------------------------------- tick

        bool? _shownAtmo;
        VisualElement _shownPlan;

        void ShowProfile(bool atmo)
        {
            if (_shownAtmo == atmo && ReferenceEquals(_shownPlan, AtmoPlan)) return;
            _shownAtmo = atmo;
            _shownPlan = AtmoPlan;
            AtmoPlan.style.display = atmo ? DisplayStyle.Flex : DisplayStyle.None;
            VacPlan.style.display = atmo ? DisplayStyle.None : DisplayStyle.Flex;
            PlanCard.Note = atmo ? "atmospheric profile" : "vacuum profile";
            SetHeatVisible(atmo);
        }

        void SetHeatVisible(bool atmo)
        {
            if (_heatBlock == null) return;
            _heatBlock.style.display = atmo ? DisplayStyle.Flex : DisplayStyle.None;
            _noHeat.style.display = atmo ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void ResolvePilot()
        {
            if (!Context.InGame) return;
            float now = Time.realtimeSinceStartup;
            if (_pilot != null && now < _nextResolve) return;
            _nextResolve = now + 2;
            var found = Context.Pilot<LandingPilot>();
            if (found == null || ReferenceEquals(found, _pilot)) return;
            // The pilot came after the build (or was replaced): bind the plan to its settings.
            _pilot = found;
            Targets?.CancelPick("landing pilot changed");
            BuildPlan(Page);
            _timelineKey = -1;
        }

        public override void OnWindowClosed() => Targets?.CancelPick("window closed");

        public override void Tick()
        {
            ResolvePilot();
            bool atmo = Atmospheric;
            ShowProfile(atmo);
            if (_pilot == null && _sampleAtmo != null) return;   // the sample stays as ShowSample left it
            if (_pilot == null)
            {
                UpdateButtons();
                UpdateTimeline();
                return;
            }
            GameTick(atmo);
        }

        void GameTick(bool atmo)
        {
            var visible = atmo ? Targets.Atmo : Targets.Vac;
            Targets.UpdatePick(visible);

            if (_atmo.precision_landing.V && !LandingSettings.PrecisionAllowed(_atmo.mode.V))
                _atmo.precision_landing.V = false;

            bool running = _pilot.isRunning;
            if (running && !_wasRunning) { _track.Clear(); _runDrops = 0; }
            _wasRunning = running;

            float now = Time.realtimeSinceStartup;
            if (now >= _nextSlow)
            {
                _nextSlow = now + 0.25f;
                Collision.Set("Collision", _pilot.collision_detected ? "Detected" : "None detected",
                    _pilot.collision_detected ? StatusKind.Alert : StatusKind.Neutral);
                UpdateInfo();
                Targets.Rebuild(visible);
                Targets.UpdatePickButton(visible);
                UpdateOrbitGate();
                UpdatePrecisionPlan();
                UpdateReentrySuggestion();
                UpdateBoostBackGate();
                UpdateStageChoices();
                UpdateTiles();
            }
            if (now >= _nextHeat)
            {
                _nextHeat = now + 0.5f;
                UpdateHeat(atmo);
            }
            RecordTrack(now);
            if (now >= _nextProfile)
            {
                _nextProfile = now + 1;
                UpdateProfileView();
            }

            UpdateStatus();
            Status.Refresh();
            UpdateButtons();
            UpdateTimeline();
            UpdateHud(now);
            UpdateNote();
        }

        // LandingUI.onUpdateUI's status, after status_bar.Reset().
        void UpdateStatus()
        {
            var st = Status.Status;
            st.Reset();
            GameState? state = null;
            try { state = GeneralTools.Game?.GlobalGameState?.GetState(); }
            catch (Exception) { }
            if (state != GameState.FlightView)
            {
                st.Console("Landing is only available in Flight View");
                return;
            }
            var p = _pilot;
            if (p.isRunning)
            {
                switch (p.mode)
                {
                    case LandingPilot.Mode.Calibrate:
                    case LandingPilot.Mode.OrbitPrep:
                    case LandingPilot.Mode.PlaneChange:
                    case LandingPilot.Mode.Deorbit:
                    case LandingPilot.Mode.MidCourse:
                    case LandingPilot.Mode.ReentryCoast:
                    case LandingPilot.Mode.Jettison:
                    case LandingPilot.Mode.PreBrake:
                    case LandingPilot.Mode.AeroCoast:
                    case LandingPilot.Mode.BoostBack:
                    case LandingPilot.Mode.AtmoReentry:
                        st.Status(PhaseText(p.mode));
                        break;
                    case LandingPilot.Mode.Pause: st.Status("Pause"); break;
                    case LandingPilot.Mode.QuickWarp: st.Status("Quick Warp"); break;
                    case LandingPilot.Mode.RotationWarp: st.Warning("Rotating Warp"); break;
                    case LandingPilot.Mode.Waiting:
                        st.Status($"Waiting : {StrTool.DurationToString(p.startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime)}");
                        break;
                    case LandingPilot.Mode.Brake:
                        if (p.brake_result_valid && p.brake_result.Status == BrakeStatus.Impossible) st.Error("Cannot stop before the ground!");
                        else st.Warning("Brake !");
                        break;
                    case LandingPilot.Mode.TouchDown:
                        if (p.brake.cannot_stop) st.Error("Cannot stop before the ground!");
                        else if (p.PrecisionActive && p.precision_drop_message != null) st.Warning(p.precision_drop_message);
                        else st.Warning("Touch Down...");
                        break;
                    case LandingPilot.Mode.Parachute:
                        if (p.parachute_descent.no_parachute_left) st.Error(ParachuteFeasibility.NoParachuteLeftMessage);
                        else if (p.parachute_descent.too_fast_under_canopy) st.Error(ParachuteFeasibility.TooFastMessage);
                        else st.Warning("Parachute descent...");
                        break;
                }
                if (p.current_executor != null && !string.IsNullOrEmpty(p.current_executor.status_line))
                    st.Console(p.current_executor.status_line);
                if (!string.IsNullOrEmpty(p.stage_message))
                {
                    if (p.stage_plan_alarm) st.Error(p.stage_message);
                    else st.Console(p.stage_message);
                }
                if (!string.IsNullOrEmpty(p.last_warning)) st.Console(p.last_warning);
                if (p.deorbit_run && !string.IsNullOrEmpty(p.descent_summary)) st.Console(p.descent_summary);
                if (!string.IsNullOrEmpty(p.heat_message)) st.Error(p.heat_message);
                if (p.PrecisionActive)
                {
                    if (!string.IsNullOrEmpty(p.precision_summary)) st.Console(p.precision_summary);
                    foreach (string w in p.precision_warnings) st.Console(w);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(p.last_error)) st.Error(p.last_error);
                else if (!string.IsNullOrEmpty(p.precision_final_message)) st.Status(p.precision_final_message);
                else st.Status("Landing autopilot not enabled");
            }
        }

        string PhaseText(LandingPilot.Mode mode)
        {
            if (mode == LandingPilot.Mode.BoostBack) return "Boost-back burn";
            if (mode == LandingPilot.Mode.AtmoReentry) return "Entry guidance";
            if (_pilot.deorbit_run)
            {
                switch (mode)
                {
                    case LandingPilot.Mode.Deorbit: return "Deorbit burn";
                    case LandingPilot.Mode.PreBrake: return "Pre-brake before the atmosphere";
                    case LandingPilot.Mode.AeroCoast: return "Atmospheric coast, surface retrograde";
                }
            }
            if (mode == LandingPilot.Mode.AeroCoast) return "Atmospheric coast: slowing down to cool";
            switch (mode)
            {
                case LandingPilot.Mode.Calibrate: return "Precision: measuring the body's rotation";
                case LandingPilot.Mode.OrbitPrep: return "Precision: orbit preparation";
                case LandingPilot.Mode.PlaneChange: return "Precision: plane change";
                case LandingPilot.Mode.Deorbit: return "Precision: deorbit burn";
                case LandingPilot.Mode.MidCourse: return "Precision: mid-course correction";
                case LandingPilot.Mode.ReentryCoast: return "Precision: coast to the service module jettison";
                case LandingPilot.Mode.Jettison: return "Precision: jettisoning the service module";
                default: return mode.ToString();
            }
        }

        // ---- LANDING INFO (LandingUI.updateContext), rows updated in place ----

        void AddInfo(string label, string value)
        {
            Info.Set(label, value);
            _infoSeen.Add(label);
            _infoKeys.Add(label);
        }

        void AddSafeInfo(string label, Func<string> value)
        {
            string text;
            try { text = value(); }
            catch (Exception) { text = "?"; }
            AddInfo(label, text);
        }

        void UpdateInfo()
        {
            var p = _pilot;
            _infoSeen.Clear();
            AddInfo("Fall Speed", $"{p.current_falling_speed:n2} m/s");
            AddInfo("Altitude", StrTool.DistanceToString(p.altitude));
            var vessel = p.current_vessel?.VesselComponent;
            if (vessel != null)
            {
                AddSafeInfo("Legs", () => LandingGear.Summary(vessel));
                if (LandingProfile.IsAtmospheric)
                    AddSafeInfo("Parachutes", () =>
                    {
                        ParachuteCounts chutes = Parachutes.Count(vessel);
                        return chutes.Total == 0 ? "None"
                            : $"{chutes.Deployed} deployed, {chutes.SemiDeployed} semi, {chutes.Armed} armed, {chutes.Stowed} stowed";
                    });
            }
            double ut = UniverseTime();
            if (p.collision_detected)
            {
                if (!ParachuteLanding)
                {
                    AddInfo("Collision In", StrTool.DurationToString(p.adjusted_collision_UT - ut));
                    AddInfo("Collision Speed", $"{p.speed_collision:n2} m/s");
                    AddInfo("Start Burn In", StrTool.DurationToString(p.startBurn_UT - ut));
                    AddInfo("Burn Duration", $"{p.burn_duration:n2} s");
                }
                AddInfo("Predicted Landing", $"{p.predicted_landing_lat:n2}, {p.predicted_landing_lon:n2}");
                if (p.settings_vac.precision_landing.V && !p.PrecisionActive)
                    AddInfo("Target Error", StrTool.DistanceToString((float)p.target_error_m));
            }
            if (p.PrecisionActive)
            {
                AddInfo("Precision Phase", PhaseText(p.mode));
                AddInfo(p.precision_distance_label, double.IsNaN(p.precision_distance_m)
                    ? "no prediction yet" : StrTool.DistanceToString((float)p.precision_distance_m));
            }
            if (p.isRunning)
            {
                p.current_executor?.UpdateInfoRows(_addInfo);
                if (!string.IsNullOrEmpty(p.heat_readout)) AddInfo("Hottest Engine", p.heat_readout);
                if (p.StagedBraking && p.stage_plan_valid)
                    AddInfo("Stage Drop", $"{p.staged_drops_left} left, at {StrTool.DistanceToString((float)p.stage_drop_height)} at the latest");
                if (p.burn_dV.burned_dV > 0) AddInfo("Burned", $"{p.burn_dV.burned_dV:n1} m/s");
            }
            if (_infoKeys.Count != _infoSeen.Count)
            {
                var gone = new List<string>();
                foreach (var k in _infoKeys) if (!_infoSeen.Contains(k)) gone.Add(k);
                foreach (var k in gone) { Info.Remove(k); _infoKeys.Remove(k); }
            }
        }

        static double UniverseTime()
        {
            try { return GeneralTools.Game?.UniverseModel?.UniverseTime ?? double.NaN; }
            catch (Exception) { return double.NaN; }
        }

        // ---- notes under the plan (LandingUI's per-tick labels) ----

        bool PeriapsisTooHigh()
        {
            var vessel = _pilot.current_vessel?.VesselComponent;
            if (vessel?.Orbit == null) return false;
            KSP.Sim.IKeplerPatch orbit = vessel.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            LandingTargeting.OrbitalElementsFromStateVectors(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter, out _, out _, out double rp);
            return rp - body.radius > PrecisionStart.MaxPeriapsisAltitude;
        }

        void UpdateOrbitGate()
        {
            bool high = false;
            try { high = !_pilot.isRunning && _vac.precision_landing.V && PeriapsisTooHigh(); }
            catch (Exception) { }
            _orbitGate.Set(high ? PrecisionStart.PeriapsisTooHighMessage : "");
        }

        void UpdatePrecisionPlan()
        {
            var p = _pilot;
            string text = "";
            if (p.precision_run || !string.IsNullOrEmpty(p.precision_final_message))
            {
                var lines = new List<string>();
                if (!string.IsNullOrEmpty(p.precision_final_message)) lines.Add(p.precision_final_message);
                if (!string.IsNullOrEmpty(p.precision_summary)) lines.Add(p.precision_summary);
                lines.AddRange(p.precision_warnings);
                text = string.Join("\n", lines);
            }
            _planNoteVac.Set(text);
            _planNoteAtmo.Set(text);
        }

        void UpdateReentrySuggestion()
        {
            var p = _pilot;
            _atmoPrecisionNote.Set(LandingSettings.PrecisionAllowed(_atmo.mode.V) ? "" : "Precision: Parachute mode only on atmospheric bodies.");
            bool suggestion = !double.IsNaN(p.suggested_drag_scale) && !p.isRunning;
            _calibration.style.display = suggestion ? DisplayStyle.Flex : DisplayStyle.None;
            if (suggestion) _calibrationNote.Set(DragCalibration.SuggestionMessage(p.suggested_drag_scale, _atmo.reentry_drag_scale.V));
        }

        void UpdateBoostBackGate()
        {
            bool boostBack = ModIdentity.IsK2D2 && _atmo.mode.V == LandingMethod.Propulsive;
            _boostBack.style.display = boostBack && _atmo.precision_landing.V ? DisplayStyle.Flex : DisplayStyle.None;
            string note = null;
            if (boostBack)
            {
                note = ReduxImpactPrediction.AtmoSettingsProblem();
                if (note == null && !_pilot.isRunning && _atmo.precision_landing.V && _pilot.LandsFromOrbit())
                    note = BoostBackStart.FromOrbitMessage;
            }
            _atmoGateNote.Set(note ?? "");
        }

        void UpdateStageChoices()
        {
            var p = _pilot;
            var vessel = p.current_vessel?.VesselComponent;
            List<LandingStageData> list = null;
            try { list = vessel == null ? null : LandingPilot.ReadStageList(vessel, out _); }
            catch (Exception) { }
            if (list != null && list.Count > 0) _stageChoices = list;
            else if (!p.isRunning) _stageChoices = new List<LandingStageData>();
            if (!p.isRunning && p.landing_stage_choice >= _stageChoices.Count) p.landing_stage_choice = 0;

            List<string> labels = LandingStages.Labels(_stageChoices);
            string shown;
            if (p.isRunning && p.staged_landing_stage >= 0)
                shown = $"Stage {p.staged_drops_left + 1} (landing stage, {p.staged_drops_left} drop(s) left)";
            else if (_stageChoices.Count == 0) shown = "No stage data";
            else shown = LandingStages.Label(p.landing_stage_choice, _stageChoices[p.landing_stage_choice]);
            foreach (var d in new[] { StageAtmo, StageVac })
            {
                d.Input.choices = labels;
                if (d.Value != shown) d.Input.SetValueWithoutNotify(shown);
                d.SetEnabled(!p.isRunning && labels.Count > 1);
            }
            StageAtmo.style.display = _atmo.mode.V == LandingMethod.Parachute ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ---- telemetry ----

        double DeltaVNeeded()
        {
            var p = _pilot;
            if (p.precision_run && p.precision_plan.Ok) return p.precision_plan.TotalDeltaV;
            if (p.deorbit_run && p.descent_plan.Ok) return p.descent_plan.TotalDeltaV;
            if (p.brake_result_valid) return p.brake_result.DeltaVNeeded;
            return double.NaN;
        }

        void UpdateTiles()
        {
            var p = _pilot;
            double needed = DeltaVNeeded();
            double onBoard = double.NaN;
            try { onBoard = p.current_vessel?.VesselComponent?.VesselDeltaV?.TotalDeltaVActual ?? double.NaN; }
            catch (Exception) { }
            DvNeeded.SetValue(double.IsNaN(needed) ? "—" : LandFormat.Thousands(needed));
            DvOnBoard.SetValue(double.IsNaN(onBoard) ? "—" : LandFormat.Thousands(onBoard));
            bool enough = !double.IsNaN(needed) && !double.IsNaN(onBoard) && onBoard >= LandingFeasibility.DeltaVMargin * needed;
            DvOnBoard.SetKind(double.IsNaN(needed) || enough ? AppTile.Kind.Ok : AppTile.Kind.Alert);
            Touchdown.SetValue(Ui.Num(p.settings.touch_down_speed.V, "0.0"));
            double ut = UniverseTime();
            Impact.SetValue(p.collision_detected && !double.IsNaN(ut) ? LandFormat.Clock(p.adjusted_collision_UT - ut) : "—");

            string feasibility = LandFormat.Feasibility(needed, onBoard, LandingFeasibility.DeltaVMargin, out bool ok);
            Feasible.style.display = feasibility == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (feasibility != null) Feasible.Set(ok ? StatusKind.Ok : StatusKind.Alert, feasibility);
        }

        void UpdateHeat(bool atmo)
        {
            if (!atmo) return;
            var vessel = _pilot.current_vessel?.VesselComponent;
            var hottest = new HottestParts();
            try
            {
                if (vessel != null)
                    foreach (PartComponent part in vessel.SimulationObject.PartOwner.Parts)
                        if (part != null)
                            hottest.Add(part.PartName, part.Temperature, part.MaxTemp, part.TryGetModule<PartComponentModule_Engine>(out _));
            }
            catch (Exception) { }
            _engineRatio = hottest.Engines > 0 ? hottest.EngineRatio : double.NaN;
            _engineName = hottest.Engine;
            _hottest.text = hottest.Engines > 0 ? "Hottest engine · " + hottest.Engine : "Hottest engine · none read";
            Gauge.SetFraction(double.IsNaN(_engineRatio) ? 0 : (float)_engineRatio);
            var p = _pilot;
            string predicted = p.deorbit_run && p.descent_plan.HeatModelled && !double.IsNaN(p.descent_plan.HeatPeak)
                ? "Predicted peak " + Ui.Num((float)(p.descent_plan.HeatPeak * 100), "0") + " % · " : "";
            string now = double.IsNaN(_engineRatio) ? "now —" : "now " + Ui.Num((float)(_engineRatio * 100), "0") + " %";
            _heatNote.text = predicted + now + (p.heat_guard.Active ? " · guard on" : "");
        }

        // ---- descent profile ----

        void RecordTrack(float now)
        {
            if (!_pilot.isRunning || now < _nextTrack) return;
            _nextTrack = now + 1;
            var v = _pilot.current_vessel?.VesselComponent;
            if (v == null) return;
            try { _track.Add(new LandGeoPoint(v.Latitude, v.Longitude, v.AltitudeFromTerrain / 1000)); }
            catch (Exception) { return; }
            if (_track.Count > 400)
                for (int i = _track.Count - 2; i > 0; i -= 2) _track.RemoveAt(i);
        }

        void UpdateProfileView()
        {
            var p = _pilot;
            var v = p.current_vessel?.VesselComponent;
            var m = new LandProfileModel();
            string empty = "No descent prediction yet: it appears on a collision course, or once a planned descent starts.";
            try
            {
                if (v?.Orbit == null) { Profile.SetModel(m, "No vessel."); return; }
                var body = v.Orbit.referenceBody;
                double radius = body.radius;
                var here = new LandGeoPoint(v.Latitude, v.Longitude, v.AltitudeFromTerrain / 1000);
                if (body.hasAtmosphere) m.AtmosphereKm = (float)(body.atmosphereDepth / 1000);
                double ut = UniverseTime();
                float xNow = float.NaN;
                List<double> uts = null;

                var samples = p.deorbit_run && p.descent_plan.HeatModelled ? p.descent_plan.Heat.Samples : null;
                if (samples != null && samples.Count > 1)
                {
                    var list = new List<LandSample>(samples.Count);
                    foreach (var s in samples)
                        list.Add(new LandSample { X = s.R.x, Y = s.R.y, Z = s.R.z, UT = s.UT, AltKm = s.Altitude / 1000 });
                    uts = m.SetPredictedSamples(list, radius, ut);
                    if (m.Predicted.Count > 1)
                    {
                        m.Source = "predicted with drag and heating";
                        xNow = m.Predicted[0].x;
                        HeatBand(m, p.descent_plan, uts, samples);
                        var brake = m.PointAtUT(uts, p.descent_plan.BrakeStartUT);
                        if (brake.HasValue) m.AddMarker("Brake", brake.Value, LandMarkerKind.Gold);
                    }
                }
                if (float.IsNaN(xNow) && p.collision_detected)
                {
                    xNow = (float)LandProfileModel.GreatCircleKm(here.Lat, here.Lon, p.predicted_landing_lat, p.predicted_landing_lon, radius);
                    m.SetPredictedCurve(new Vector2(xNow, (float)here.AltKm));
                    m.Source = "impact prediction · curve drawn between its ends";
                    double span = p.adjusted_collision_UT - ut;
                    if (span > 0 && p.startBurn_UT > ut && p.startBurn_UT < p.adjusted_collision_UT && !ParachuteLanding)
                        m.AddMarker("Brake · in " + StrTool.DurationToString(p.startBurn_UT - ut),
                            LandProfileModel.PointAtFraction(m.Predicted, (float)((p.startBurn_UT - ut) / span)), LandMarkerKind.Gold);
                }
                if (float.IsNaN(xNow)) { Profile.SetModel(m, empty); ViewCard.Note = "no prediction"; return; }

                m.Vessel = new Vector2(xNow, (float)here.AltKm);
                m.Flown.Clear();
                foreach (var t in _track)
                    m.Flown.Add(new Vector2(xNow + (float)LandProfileModel.GreatCircleKm(t.Lat, t.Lon, here.Lat, here.Lon, radius), (float)Math.Max(0, t.AltKm)));
                if (m.Flown.Count > 0) m.Flown.Add(m.Vessel.Value);

                if (p.isRunning && p.StagedBraking && !double.IsNaN(p.stage_drop_height))
                {
                    var drop = LandProfileModel.PointAtAltitude(m.Predicted, (float)(p.stage_drop_height / 1000));
                    if (drop.HasValue) m.AddMarker("Drop stage · " + StrTool.DistanceToString(p.stage_drop_height), drop.Value, LandMarkerKind.Muted);
                }
                bool precision = p.PrecisionActive || p.settings.precision_landing.V;
                double off = p.PrecisionActive ? p.precision_distance_m : p.target_error_m;
                m.TargetNote = precision && !double.IsNaN(off)
                    ? "target · predicted " + StrTool.DistanceToString(off) + " off"
                    : "predicted touchdown";
                ViewCard.Note = m.Source;
                Profile.SetModel(m, empty);
            }
            catch (Exception)
            {
                Profile.SetModel(new LandProfileModel(), "Descent prediction not readable.");
            }
        }

        static void HeatBand(LandProfileModel m, DescentPlan plan, List<double> uts, List<DescentSample> samples)
        {
            double peak = plan.HeatPeak;
            if (double.IsNaN(peak) || !(peak > 0.05)) return;
            int to = -1, from = -1;
            for (int i = 0; i < uts.Count; i++) if (uts[i] <= plan.Heat.PeakUT) to = i;
            // The kept samples are the last uts.Count ones of the prediction.
            int offset = samples.Count - uts.Count;
            for (int i = 0; i <= to; i++)
                if (samples[i + offset].PeakRatio >= 0.5 * peak) { from = i; break; }
            if (from < 0 || to <= from) return;
            m.HeatFrom = from;
            m.HeatTo = to;
            m.HeatLabel = "peak heating · " + Ui.Num((float)(peak * 100), "0") + " %";
        }

        // ---- timeline, hud, note ----

        void UpdateTimeline()
        {
            var p = _pilot;
            bool atmo = Atmospheric;
            bool running = p != null && p.isRunning;
            LandRunKind kind;
            int drops;
            LandingPilot.Mode mode = LandingPilot.Mode.Off;
            bool dropping = false;
            if (p == null)
            {
                bool chute = atmo && _atmo.mode.V == LandingMethod.Parachute;
                var s = atmo ? _atmo : _vac;
                kind = LandPhases.KindOf(false, false, false, false, false, chute, false, s.precision_landing.V, atmo);
                drops = 0;
            }
            else
            {
                kind = LandPhases.KindOf(running, p.boost_back_run, p.reentry_run, p.precision_run, p.deorbit_run,
                    p.parachute_mode, p.mode == LandingPilot.Mode.Parachute, p.settings.precision_landing.V, atmo);
                if (running)
                {
                    _runDrops = Math.Max(_runDrops, p.staged_drops_left);
                    drops = _runDrops;
                }
                else drops = p.parachute_mode ? 0 : p.landing_stage_choice;
                mode = p.mode;
                dropping = p.staged_drops_left > 0;
            }
            int key = (int)kind * 10000 + (atmo ? 5000 : 0) + Math.Min(drops, 4999);
            if (key != _timelineKey)
            {
                _timelineKey = key;
                _steps = LandPhases.Steps(kind, atmo, drops);
                _step = -1;
            }
            int step = running ? LandPhases.IndexOf(_steps, mode, dropping) : 0;
            if (step != _step)
            {
                _step = step;
                Page.SetTimeline(_steps, step);
            }
        }

        public IReadOnlyList<string> Steps => _steps;
        public int CurrentStep => _step;

        float _nextHud;

        void UpdateHud(float now)
        {
            if (!IsRunning) { _hud = null; return; }
            if (_hud != null && now < _nextHud) return;
            _nextHud = now + 0.25f;
            var p = _pilot;
            var hud = new HudState
            {
                Pilot = "LAND",
                Phase = _step >= 0 && _step < _steps.Count ? _steps[_step] : PhaseText(p.mode),
                Phases = _steps,
                CurrentPhase = Math.Max(0, _step),
                Stop = () => { if (_pilot != null) _pilot.isRunning = false; },
            };
            hud.Readouts.Add(("ALT", StrTool.DistanceToString(p.altitude)));
            hud.Readouts.Add(("SPEED", Ui.Num(p.current_falling_speed, "0") + " m/s"));
            double off = p.PrecisionActive ? p.precision_distance_m : p.settings.precision_landing.V ? p.target_error_m : double.NaN;
            if (!double.IsNaN(off)) hud.Readouts.Add(("TARGET", StrTool.DistanceToString(off)));
            if (Atmospheric && !double.IsNaN(_engineRatio))
            {
                hud.GaugeLabel = "HOTTEST PART";
                hud.Gauge = (float)_engineRatio;
            }
            if (!string.IsNullOrEmpty(p.heat_message))
            {
                hud.Alert = p.heat_message;
                hud.AlertDetail = p.heat_readout;
            }
            else if (p.heat_guard.Active)
            {
                hud.Alert = "Heat guard · thrusting retrograde";
                hud.AlertDetail = double.IsNaN(_engineRatio) ? p.heat_readout
                    : (_engineName ?? "engine") + " at " + Ui.Num((float)(_engineRatio * 100), "0") + " %";
            }
            _hud = hud;
        }

        void UpdateNote()
        {
            string note;
            if (IsRunning)
                note = LandPhases.Hint(_step >= 0 && _step < _steps.Count ? _steps[_step] : null) ?? PhaseText(_pilot.mode);
            else
                note = _pilot.parachute_mode ? "Land arms the parachutes, or flies the precision return when it is on"
                    : _pilot.collision_detected ? "Collision course detected: Brake plans the burn from here"
                    : "No collision course: Brake plans the descent (deorbit, then the brake)";
            Page.Actions.SetNote(note);
        }

        // ---------------------------------------------------------------- sample

        public override void ShowSample()
        {
            _sampleAtmo = true;
            ShowProfile(true);
            _atmo.mode.V = LandingMethod.Propulsive;
            _atmo.deploy_legs_early.V = false;
            LandTargets.SetStaticList(Targets.Atmo, new List<string> { "KSC", "Duna base site" }, "Duna base site");
            _atmo.target_latitude.V = 17.42f;
            _atmo.target_longitude.V = -88.10f;
            Targets.Atmo.Refresh();
            StageAtmo.Input.choices = new List<string> { "Stage 2 · brake with stage 1 first", "Stage 1 (current)" };
            StageAtmo.Input.SetValueWithoutNotify("Stage 2 · brake with stage 1 first");

            ViewCard.Note = "predicted with drag and heating";
            Profile.SetModel(LandProfileModel.Sample());

            _hottest.text = "Hottest part · LV-T45 engine";
            Gauge.SetFraction(205f / 336f);
            _heatNote.text = "Predicted peak 61 % · now 22 %";
            DvNeeded.SetValue("1 122");
            DvOnBoard.SetValue("1 640");
            DvOnBoard.SetKind(AppTile.Kind.Ok);
            Touchdown.SetValue("2.0", "m/s");
            Impact.SetValue("6:41");
            Collision.Set("Collision", "Detected", StatusKind.Alert);
            Feasible.style.display = DisplayStyle.Flex;
            Feasible.Set(StatusKind.Ok, "Landing feasible · 46 % Δv margin");

            _steps = LandPhases.Steps(LandRunKind.Descent, true, 1);
            _step = 1;
            _timelineKey = -2;
            Page.SetTimeline(_steps, _step);
            Page.Actions.SetNote(LandPhases.Hint(LandPhases.PreBrake));
            MainButton.Text = "Landing…";
            MainButton.SetEnabled(true);
            AbortButton.SetEnabled(true);
            AbortButton.style.display = DisplayStyle.Flex;
            TouchDownButton.style.display = DisplayStyle.None;
        }
    }

    /// <summary>The Land tab's number formats (pure, tested).</summary>
    public static class LandFormat
    {
        /// <summary>"1 640": rounded, thin groups of three as in the mockups.</summary>
        public static string Thousands(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
            return Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
        }

        /// <summary>"6:41", "1:02:05"; "—" for a time past or unknown.</summary>
        public static string Clock(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0 || double.IsInfinity(seconds)) return "—";
            long s = (long)Math.Round(seconds);
            long h = s / 3600, m = s / 60 % 60, r = s % 60;
            return h > 0 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, r)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, r);
        }

        /// <summary>
        /// The safety pill: "Landing feasible · 46 % Δv margin" when the Δv on board covers what is
        /// needed times the start checks' margin, else what is missing; null when either is unknown.
        /// An estimate: the start checks decide.
        /// </summary>
        public static string Feasibility(double needed, double onBoard, double margin, out bool ok)
        {
            ok = false;
            if (double.IsNaN(needed) || double.IsNaN(onBoard) || !(needed > 0)) return null;
            double extra = onBoard / needed - 1;
            ok = onBoard >= margin * needed;
            return ok
                ? "Landing feasible · " + Math.Round(extra * 100).ToString(CultureInfo.InvariantCulture) + " % Δv margin"
                : "Δv short: " + Thousands(margin * needed - onBoard) + " m/s more needed (with the " +
                  Math.Round((margin - 1) * 100).ToString(CultureInfo.InvariantCulture) + " % margin)";
        }
    }
}
