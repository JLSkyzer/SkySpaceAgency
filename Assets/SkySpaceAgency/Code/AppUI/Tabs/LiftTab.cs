using System;
using System.Collections.Generic;
using KTools;
using SkySpaceAgency.Lift;
using SkySpaceAgency.Controller;
using SkySpaceAgency.Rendezvous;
using SkySpaceAgency.Transfer;
using SsaUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// LIFT (docs/design/ui/lift.png), wired to the real LiftPilot and its LiftSettings (the classic
    /// Lift.uxml + LiftUI + LiftSettings.setupUI, inventory docs/superpowers/notes/new-ui-inventory/lift.md).
    /// Plan: the goal, its target, the ascent profile and its settings, the switches, the roll program
    /// and the ADVANCED group. View: the ascent profile (planned Guided curve, flown path, atmosphere of
    /// the real body). Side: telemetry tiles, the stages' Δv, the pilot's status, the LIFT INFO rows.
    /// </summary>
    public class LiftTab : AppTab
    {
        public LiftTab(AppTabContext context) : base(context) { }
        public override string Code => "lift";
        public override string Label => "Lift";

        static readonly LiftGoal[] GoalValues = { LiftGoal.Orbit, LiftGoal.PrepareTransfer, LiftGoal.Rendezvous };
        static readonly string[] GoalLabels = { "Orbit", "Transfer", "Rendezvous" };
        static readonly AscentProfile[] ProfileValues = { AscentProfile.Guided, AscentProfile.GravityTurn };
        static readonly string[] ProfileLabels = { "Guided", "Gravity turn (exp.)" };
        const string NoBodyLabel = "Choose a body";
        const int MaxStages = 4;
        const int MaxFlownSamples = 1500;

        LiftPilot _pilot;
        float _nextPilotTry;
        LiftSettings _settings;
        readonly AppPilotStatus _status = new AppPilotStatus();

        /// <summary>The settings the controls are bound to: the pilot's, or the tab's own in the editor.</summary>
        public LiftSettings Settings => _settings;
        public LiftPilot Pilot => _pilot;
        public AppPilotStatus Status => _status;

        // ---- controls ----
        AppSegmented _goal;
        VisualElement _targetGroup, _guided, _gravityTurn, _gtKick, _gtHold, _rollBox, _adjustBox, _headingField;
        AppDropdown _target;
        AppSegmented _profile;
        AppSwitch _dock, _autoCircularize, _pause;
        AppSlider _startAlt, _midAlt, _endAlt;
        AppNote _hint, _endAscentNote, _endAdjustNote, _stagesNote;
        AppInfoTable _planInfo, _liftInfo;
        VisualElement _roll;
        AppSection _advanced, _infoSection;
        AppButton _main;
        LiftProfileView _profileView;
        AppTile _altitude, _speed, _apoapsis, _timeToAp;
        readonly AppStageBar[] _stages = new AppStageBar[MaxStages];
        readonly Label[] _stageLabels = new Label[MaxStages];

        public LiftProfileView ProfileView => _profileView;
        public AppButton MainButton => _main;
        public AppTile AltitudeTile => _altitude;
        public AppTile ApoapsisTile => _apoapsis;
        public AppInfoTable LiftInfo => _liftInfo;
        public VisualElement GravityTurnSettings => _gravityTurn;
        public VisualElement GuidedSettings => _guided;
        public VisualElement TargetGroup => _targetGroup;

        // ---- state kept between ticks ----
        bool _sample;
        bool? _shownRunning;
        bool _shownHasPilot;
        string _timelineKey;
        int _timelineIndex = -1;
        List<string> _steps = new();
        string _gtPhase;
        readonly List<string> _infoKeys = new();
        readonly HashSet<string> _infoSeen = new();
        Action<string, string> _addInfoRow;
        readonly HudState _hud = new HudState { Pilot = "LIFT" };

        // targets (as LiftUI)
        List<RendezvousReadout.Choice> _vesselChoices = new();
        List<BodyChoices.Entry> _bodies = new();
        BodySystem _bodySystem;
        bool _dropListsVessels;
        float _nextVesselTry, _nextBodyTry;
        string _lastGameTarget;

        // telemetry and flown path
        readonly List<Vector2> _flown = new();
        bool _recording;
        double _launchLat, _launchLon;
        float _altKm = float.NaN;
        bool _landed;
        float _drawnTarget = -1, _drawnStart = -1, _drawnMid = -1, _drawnEnd = -1, _drawnAtmo = -2;
        int _drawnFlown = -1;
        bool _drawnGuided;
        Vector2? _drawnVessel;
        string _drawnSite;
        string _site = "";
        float _atmoKm = -1;

        // ------------------------------------------------------------------ build

        protected override void OnBuild(AppPage page)
        {
            _addInfoRow = AddInfoRow;
            _hud.Stop = () => { if (_pilot != null) _pilot.isRunning = false; };
            _pilot = Context.Pilot<LiftPilot>();
            _settings = _pilot?.settings ?? OwnSettings(Context.InGame);
            BuildContent(page);

            page.Actions.SetNote("");
            _main = page.Actions.AddButton("Start", AppButton.Kind.Main, ToggleRun);
            UpdateRunState(force: true);
            UpdateTimeline(force: true);
        }

        // No pilot (the editor, or a page built before the campaign): the tab's own LiftSettings, on
        // the same keys. In the editor the shared settings file is never loaded, and a setting made on
        // an unloaded file reads 0 until the load (never): the file is then marked loaded first, with
        // no data, so each setting starts at its default and the instances share their values as they
        // would share the file. LiftSettings cannot take an in-memory file (its fields use the shared
        // one), hence this instead of SettingsFile.InMemory.
        static LiftSettings OwnSettings(bool inGame)
        {
            if (!inGame && !SettingsFile.Instance.loaded)
                SettingsFile.Instance.loaded = true;
            return new LiftSettings();
        }

        void BuildContent(AppPage page)
        {
            page.Plan.Clear();
            page.View.Clear();
            page.Side.Clear();
            var s = _settings;

            // ---- MISSION PLAN ----
            var plan = new AppCard("Mission plan");
            _goal = Tip(AppBind.Segmented(s.goal, GoalValues, GoalLabels),
                "Orbit: the ascent to the Ap below. Transfer: wait on the pad for the transfer's parking-orbit plane, lift into it and trim it. Rendezvous: wait on the pad for the launch pass with the cheapest rendezvous, lift under the target vessel and hand over to the Rendezvous pilot");
            plan.Add(_goal);

            _targetGroup = Ui.Box("ssa-lift-target");
            _target = Tip(new AppDropdown("Target", new List<string>(), 0),
                "The body of the transfer, every body grouped by parent; for a rendezvous, a vessel around the launch body");
            AppBind.Record(s.transfer_target.key);
            AppBind.Record(s.rendezvous_target.key);
            _target.changed += OnTargetPicked;
            _targetGroup.Add(_target);
            _dock = Tip(AppBind.Switch("Dock after the rendezvous", s.rendezvous_dock),
                "After the rendezvous, dock with a free compatible port (Rendezvous tab rules)");
            _targetGroup.Add(_dock);
            _hint = new AppNote();
            _targetGroup.Add(_hint);
            _planInfo = new AppInfoTable();
            _targetGroup.Add(_planInfo);
            plan.Add(_targetGroup);

            var fields = Ui.Box("ssa-fields");
            fields.Add(AppBind.Field("Target altitude", s.destination_Ap_km, "km"));
            var heading = AppBind.Field("Heading", s.heading, "°", "0.0");
            heading.AddToClassList("ssa-field--last");
            _headingField = heading;
            fields.Add(heading);
            plan.Add(fields);

            _profile = Tip(AppBind.Segmented(s.ascent_profile, ProfileValues, ProfileLabels),
                "Guided: fixed pitch-over curve (the original K2-D2 ascent). Gravity Turn (Experimental): rises straight up, kicks over, then holds prograde and modulates the throttle to hold a target time to apoapsis. Orbit launches only; not yet flown in game");
            _profile.AddToClassList("ssa-lift-gap");
            plan.Add(_profile);

            // Guided: the classic "90° Alt.", "45° Alt" and "5° Alt" (the two ratios show their altitude).
            _guided = Ui.Box("ssa-lift-group");
            _startAlt = AppBind.Slider("Vertical climb to", s.start_altitude_km, 0, 20, v => Ui.Num(v, "0") + " km");
            _midAlt = AppBind.Slider("45° pitch at", s.mid_rotate_ratio, _ => Ui.Num(_settings.mid_rotate_altitude_km, "0") + " km");
            _endAlt = AppBind.Slider("5° pitch at", s.end_rotate_ratio, _ => Ui.Num(_settings.end_rotate_altitude_km, "0") + " km");
            _guided.Add(_startAlt);
            _guided.Add(_midAlt);
            _guided.Add(_endAlt);
            plan.Add(_guided);

            // Gravity Turn (K2-D2 1.4.0 names).
            _gravityTurn = Ui.Box("ssa-lift-group");
            _gravityTurn.Add(Tip(AppBind.Switch("Auto kick & hold", s.gravity_turn_auto),
                "Works out the best Pitch Kick and Hold Time for this rocket just before the kick, from its measured drag and its stages. Turn this off to set Pitch Kick and Hold Time yourself. Their last values are also used if it can't make a plan"));
            _gravityTurn.Add(Tip(AppBind.Slider("Kick speed", s.gravity_turn_kick_speed_mps, v => Ui.Num(v, "0") + " m/s"),
                "Vertical speed the rocket needs to reach during the initial straight-up rise before it performs its pitch kick"));
            _gtKick = Tip(AppBind.Slider("Pitch kick", s.gravity_turn_pitch_kick_deg, v => Ui.Num(v, "0.0") + "°"),
                "Tilt after the vertical rise, held until the velocity follows - prograde straight up has nothing to fall over from, so the turn needs this seed");
            _gtHold = Tip(AppBind.Slider("Hold time to apoapsis", s.gravity_turn_hold_time_s, v => Ui.Num(v, "0") + " s"),
                "Seconds to apoapsis the throttle holds during the turn. Lower makes a flatter climb (usually cheaper, but more time in thick air), higher a steeper one");
            _gravityTurn.Add(_gtKick);
            _gravityTurn.Add(_gtHold);
            _gravityTurn.Add(Tip(AppBind.Slider("Min throttle", s.gravity_turn_min_throttle, Percent),
                "Throttle never goes below this during the turn. Near the end it keeps the rocket raising its apoapsis instead of levelling off in the air"));
            _gravityTurn.Add(Tip(AppBind.Slider("Max TWR", s.gravity_turn_max_twr, v => Ui.Num(v, "0.0")),
                "The throttle is held down so thrust never exceeds this many times the rocket's weight - keeps powerful rockets from climbing too steeply and fast through thick air"));
            _gravityTurn.Add(Tip(AppBind.Slider("Max dynamic pressure", s.gravity_turn_max_q_kpa, v => Ui.Num(v, "0") + " kPa"),
                "The throttle eases off as the air pressure on the rocket nears this, to limit drag and aerodynamic stress"));
            plan.Add(_gravityTurn);

            var switches = Ui.Box("ssa-lift-switches");
            switches.Add(AppBind.Switch("Auto staging", StagingSettings.auto_staging));
            switches.Add(Tip(AppBind.Switch("Coasting warp", s.coasting_warp), "Auto warp while waiting to leave the atmosphere"));
            _autoCircularize = Tip(AppBind.Switch("Circularize once ascent complete", s.auto_circularize),
                "Finish the ascent with a circularization burn at apoapsis. Off: the ascent ends once apoapsis is set, and you circularize yourself");
            switches.Add(_autoCircularize);
            _pause = Tip(AppBind.Switch("Pause leaving atmosphere", s.pause_on_final),
                "When Circularize Once Ascent Complete is off: pause the game once the vessel is out of the atmosphere and apoapsis is set, so you can plan your circularization");
            plan.Add(switches);

            // ADVANCED: max throttle, the pause, the roll program (Guided only), then the classic sections
            // ASCENT and ADJUST (folded: the plan column keeps the mockup's height).
            _advanced = new AppSection("Advanced");
            _advanced.Add(AppBind.Slider("Max throttle", s.max_throttle, Percent));
            _advanced.Add(_pause);
            _roll = Ui.Box("ssa-lift-group");
            _roll.Add(Tip(AppBind.Switch("Roll program", s.roll_program),
                "Roll the vessel to a chosen offset from its launch orientation once it clears the altitude below"));
            _rollBox = Ui.Box("ssa-lift-group");
            _rollBox.Add(Tip(AppBind.Slider("Roll altitude", s.roll_program_altitude_km, v => Ui.Num(v, "0.0") + " km"),
                "Altitude at which the roll program executes"));
            _rollBox.Add(Tip(AppBind.Slider("Roll angle (from launch)", s.roll_program_angle_deg, v => Ui.Num(v, "0") + "°"),
                "How many degrees to roll from the vessel's launch orientation (not an absolute compass heading)"));
            _rollBox.Add(Tip(AppBind.Slider("Roll rate", s.roll_program_rate_deg_s, v => Ui.Num(v, "0.0") + " °/s"),
                "How fast the roll program is allowed to roll the vessel toward its target angle - lower is gentler but takes longer, higher settles faster but risks overshoot"));
            _roll.Add(_rollBox);
            _advanced.Add(_roll);
            _advanced.Add(Tip(AppBind.Switch("Heading correction", s.heading_correction), "Correct the speed direction to fit the wanted heading"));
            _advanced.Add(AppBind.Slider("Ap alt error", s.end_ascent_pc, v => Ui.Num(v, "0.##") + " %", 0.01f));
            _endAscentNote = new AppNote();
            _advanced.Add(_endAscentNote);
            _advanced.Add(AppBind.Switch("Fine tuning", s.adjust));
            _adjustBox = Ui.Box("ssa-lift-group");
            _adjustBox.Add(AppBind.Slider("Final alt error", s.end_adjust_pc, v => Ui.Num(v, "0.###") + " %", 0.001f));
            _endAdjustNote = new AppNote();
            _adjustBox.Add(_endAdjustNote);
            _advanced.Add(_adjustBox);
            var reset = new AppButtonRow();
            reset.Add("Reset Lift settings", () => SettingsFile.Instance.Reset("lift"));
            _advanced.Add(reset);
            plan.Add(_advanced);
            page.Plan.Add(plan);

            // ---- ASCENT PROFILE ----
            var profile = new AppCard("Ascent profile", "altitude / downrange", sky: true);
            _profileView = new LiftProfileView();
            profile.Add(_profileView);
            page.View.Add(profile);

            // ---- TELEMETRY ----
            var telemetry = new AppCard("Telemetry");
            var tiles = Ui.Box("ssa-tiles");
            _altitude = new AppTile("Altitude", "—", "km");
            _speed = new AppTile("Speed", "—", "m/s");
            _apoapsis = new AppTile("Apoapsis", "—", "km", AppTile.Kind.Gold);
            _timeToAp = new AppTile("Time to Ap", "—");
            tiles.Add(_altitude);
            tiles.Add(_speed);
            tiles.Add(_apoapsis);
            tiles.Add(_timeToAp);
            telemetry.Add(tiles);
            telemetry.Add(Ui.Label("STAGES · Δv", "ssa-section-title"));
            for (int i = 0; i < MaxStages; i++)
            {
                _stages[i] = new AppStageBar("S" + (i + 1), "", 0, active: i == 0);
                _stageLabels[i] = _stages[i].Q<Label>(className: "ssa-stage__label");
                _stages[i].style.display = DisplayStyle.None;
                telemetry.Add(_stages[i]);
            }
            _stagesNote = new AppNote();
            _stagesNote.Set("No stage data yet.");
            telemetry.Add(_stagesNote);
            telemetry.Add(Ui.Box("ssa-grow"));
            telemetry.Add(_status.Pill);
            telemetry.Add(_status.Console);
            telemetry.Add(_status.Progress);
            _infoSection = new AppSection("Lift info");
            _liftInfo = new AppInfoTable();
            _infoSection.Add(_liftInfo);
            telemetry.Add(_infoSection);
            page.Side.Add(telemetry);

            // ---- what follows the settings ----
            s.goal.listeners += _ => UpdateVisibility();
            s.ascent_profile.listeners += _ => UpdateVisibility();
            s.gravity_turn_auto.listeners += _ => UpdateVisibility();
            s.roll_program.listeners += _ => UpdateVisibility();
            s.adjust.listeners += _ => UpdateVisibility();
            s.auto_circularize.listeners += _ => UpdateVisibility();
            // The ratios' altitudes follow the 90° altitude and the target (LiftSettings.update_altitudes
            // runs first: its listeners were added by its constructor).
            s.start_altitude_km.listeners += _ => RefreshAltitudes();
            s.destination_Ap_km.listeners += _ => RefreshAltitudes();
            s.mid_rotate_ratio.listeners += _ => RefreshAltitudes();
            s.end_rotate_ratio.listeners += _ => RefreshAltitudes();
            s.end_ascent_pc.listeners += _ => RefreshAltitudes();
            s.end_adjust_pc.listeners += _ => RefreshAltitudes();
            // 45° never above 5° (the classic tab's rule).
            s.mid_rotate_ratio.listeners += v => { if (_settings.end_rotate_ratio.V < v) _settings.end_rotate_ratio.V = v; };
            s.end_rotate_ratio.listeners += v => { if (_settings.mid_rotate_ratio.V > v) _settings.mid_rotate_ratio.V = v; };
            UpdateVisibility();
            RefreshAltitudes();
            UpdateGoal();
        }

        static string Percent(float v) => Ui.Num(v * 100, "0") + " %";

        static T Tip<T>(T e, string tooltip) where T : VisualElement
        {
            e.tooltip = tooltip;
            return e;
        }

        static void Show(VisualElement e, bool show)
        {
            if (e != null) e.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>What shows for the goal and the profile (LiftSettings.UpdateProfileSettings, UpdateCircularizeToggles).</summary>
        void UpdateVisibility()
        {
            var s = _settings;
            bool orbit = s.goal.V == LiftGoal.Orbit;
            AscentProfile flown = LiftGoalRules.ProfileFor(s.goal.V, s.ascent_profile.V);
            Show(_targetGroup, !orbit);
            Show(_dock, s.goal.V == LiftGoal.Rendezvous);
            Show(_headingField, orbit);
            Show(_profile, orbit);
            Show(_guided, flown == AscentProfile.Guided);
            Show(_roll, flown == AscentProfile.Guided);
            Show(_gravityTurn, flown == AscentProfile.GravityTurn);
            Show(_gtKick, !s.gravity_turn_auto.V);
            Show(_gtHold, !s.gravity_turn_auto.V);
            Show(_rollBox, s.roll_program.V);
            Show(_adjustBox, s.adjust.V);
            bool forced = LiftGoalRules.CircularizationForced(s.goal.V);
            Show(_autoCircularize, !forced);
            _pause?.SetEnabled(!forced && !s.auto_circularize.V);
            _timelineKey = null;
        }

        void RefreshAltitudes()
        {
            var s = _settings;
            _midAlt?.SetValueWithoutNotify(s.mid_rotate_ratio.V);
            _endAlt?.SetValueWithoutNotify(s.end_rotate_ratio.V);
            _endAscentNote?.Set("End ascent alt: " + Ui.Num(s.target_Ap_km - s.end_ascent_error, "0.00") + " km");
            _endAdjustNote?.Set("Final adjust alt: " + Ui.Num(s.target_Ap_km - s.end_adjust_error, "0.00") + " km");
        }

        // ------------------------------------------------------------------ run

        public override bool IsRunning => _pilot != null && _pilot.isRunning;

        void ToggleRun()
        {
            var pilot = _pilot;
            if (pilot == null) return;
            bool start = !pilot.isRunning;
            if (start) _status.Reset();
            pilot.isRunning = start;
            UpdateRunState(force: true);
        }

        void UpdateRunState(bool force = false)
        {
            bool running = IsRunning;
            bool hasPilot = _pilot != null;
            if (!force && _shownRunning == running && _shownHasPilot == hasPilot) return;
            _shownRunning = running;
            _shownHasPilot = hasPilot;
            if (_main != null)
            {
                _main.Text = running ? "Stop" : "Start";
                _main.SetEnabled(hasPilot);
            }
            // The goal and its target are frozen while the pilot flies (LiftUI.UpdateTransferGoal).
            _goal?.SetEnabled(!running);
            _target?.SetEnabled(!running);
            _dock?.SetEnabled(!running);
        }

        public override HudState Hud
        {
            get
            {
                if (!IsRunning) return null;
                _hud.Phases = _steps;
                _hud.CurrentPhase = Mathf.Max(0, _timelineIndex);
                _hud.Phase = _timelineIndex >= 0 && _timelineIndex < _steps.Count ? _steps[_timelineIndex] : "";
                _hud.Readouts.Clear();
                _hud.Readouts.Add(("ALT", _altitude.Value + " km"));
                _hud.Readouts.Add(("AP", _apoapsis.Value + " km"));
                _hud.Readouts.Add(("TIME TO AP", _timeToAp.Value));
                _hud.Alert = _status.Pill.Kind == StatusKind.Alert ? _status.Pill.Text : null;
                return _hud;
            }
        }

        // ------------------------------------------------------------------ tick

        public override void Tick()
        {
            if (_sample) return;
            ResolvePilot();
            UpdateRunState();
            UpdateGoal();
            ReadVessel();
            UpdateStatus();
            UpdateInfo();
            UpdateTimeline();
            UpdateProfile();
            UpdateNote();
        }

        void ResolvePilot()
        {
            if (_pilot != null || !Context.InGame || Time.realtimeSinceStartup < _nextPilotTry) return;
            _nextPilotTry = Time.realtimeSinceStartup + 1;
            _pilot = Context.Pilot<LiftPilot>();
            // The pilot came after the page: bind the controls to its settings, not the tab's own.
            if (_pilot != null && _pilot.settings != _settings)
            {
                _settings = _pilot.settings;
                BuildContent(Page);
                _timelineKey = null;
                _drawnTarget = -1;
            }
        }

        // ---- goal and target (LiftUI.UpdateTransferGoal, UpdateBodies, UpdateVessels) ----

        void OnTargetPicked(int i)
        {
            var s = _settings;
            if (s.goal.V == LiftGoal.Rendezvous)
            {
                // A body header cannot be picked (its Id is null).
                if (i >= 0 && i < _vesselChoices.Count && _vesselChoices[i].Id != null)
                    s.rendezvous_target.V = _vesselChoices[i].Id;
                return;
            }
            if (i >= 0 && i < _bodies.Count)
                s.transfer_target.V = _bodies[i].Name;
        }

        void UpdateGoal()
        {
            var s = _settings;
            if (_target == null) return;
            if (s.goal.V == LiftGoal.Rendezvous) UpdateRendezvousGoal();
            else if (s.goal.V == LiftGoal.PrepareTransfer) UpdateTransferGoal();
        }

        void UpdateTransferGoal()
        {
            var s = _settings;
            if (Context.InGame && _bodySystem == null && Time.realtimeSinceStartup >= _nextBodyTry)
            {
                _nextBodyTry = Time.realtimeSinceStartup + 2;
                try
                {
                    BodySystem system = GameBodySystem.Build(GeneralTools.Current_UT, out string error);
                    if (system == null)
                        ModIdentity.Logger("Lift").LogInfo($"[Lift] new tab: no body list yet ({error})");
                    else
                    {
                        _bodySystem = system;
                        _bodies = BodyChoices.Targets(system);
                        _target.SetChoices(BodyChoices.Labels(_bodies), 0);
                    }
                }
                catch (Exception e)
                {
                    ModIdentity.Logger("Lift").LogInfo($"[Lift] new tab: no body list yet ({e.GetType().Name}: {e.Message})");
                }
            }
            if (_dropListsVessels)
            {
                _dropListsVessels = false;
                _target.SetChoices(BodyChoices.Labels(_bodies), 0);
            }
            string target = s.transfer_target.V;
            int i = BodyChoices.FindName(_bodies, target);
            _target.Input.SetValueWithoutNotify(i >= 0 ? _bodies[i].Label : NoBodyLabel);

            var plan = _pilot?.transfer;
            if (plan != null && plan.Goal == LiftGoal.PrepareTransfer && plan.TargetName == target)
            {
                _hint.Set("");
                ShowReadout(plan);
            }
            else
            {
                _planInfo.ClearRows();
                _hint.Set(string.IsNullOrEmpty(target)
                    ? "Choose the target body."
                    : $"Start plans the parking orbit for {target} and the launch time, then waits on the pad and fires the first stage at T-0.");
            }
        }

        void UpdateRendezvousGoal()
        {
            var s = _settings;
            if (Context.InGame && (!_dropListsVessels || Time.realtimeSinceStartup >= _nextVesselTry))
            {
                _dropListsVessels = true;
                _nextVesselTry = Time.realtimeSinceStartup + 2;
                try
                {
                    var ours = RendezvousSnapshot.Ours;
                    string body = ours?.Orbit?.referenceBody?.bodyName;
                    var groups = new List<(string body, List<VesselEntry> vessels)>();
                    foreach (var g in RendezvousSnapshot.VesselsByBody(ours))
                        if (g.body == body)
                            groups.Add(g);
                    _vesselChoices = RendezvousReadout.Targets(groups);
                    _target.SetChoices(RendezvousReadout.Labels(_vesselChoices), 0);

                    // A new game target is selected once (as the classic tab); the pick stands otherwise.
                    string gameTarget = RendezvousSnapshot.TargetVessel(ours)?.GlobalId.Guid.ToString("D");
                    if (!IsRunning && gameTarget != null && gameTarget != _lastGameTarget
                        && RendezvousReadout.FindId(_vesselChoices, gameTarget) >= 0)
                        s.rendezvous_target.V = gameTarget;
                    _lastGameTarget = gameTarget;
                }
                catch (Exception e)
                {
                    ModIdentity.Logger("Lift").LogInfo($"[Lift] new tab: no vessel list yet ({e.GetType().Name}: {e.Message})");
                }
            }
            string id = s.rendezvous_target.V;
            int i = RendezvousReadout.FindId(_vesselChoices, id);
            _target.Input.SetValueWithoutNotify(i >= 0 ? _vesselChoices[i].Label : RendezvousReadout.NoTargetLabel);
            string name = i >= 0 ? _vesselChoices[i].Label.Trim() : null;

            var plan = _pilot?.transfer;
            if (plan != null && plan.Goal == LiftGoal.Rendezvous && plan.TargetId == id)
            {
                _hint.Set("");
                ShowReadout(plan);
            }
            else
            {
                _planInfo.ClearRows();
                _hint.Set(name == null
                    ? "Choose the target vessel."
                    : $"Start picks the launch pass for the cheapest rendezvous with {name}, waits on the pad, lifts into an orbit 10 km under it (at most the low orbit + 150 km) and hands over to the Rendezvous pilot.");
            }
        }

        void ShowReadout(LiftTransferPlan plan)
        {
            List<(string, string)> rows;
            try { rows = LiftGoalRules.Readout(plan, GeneralTools.Current_UT); }
            catch (Exception) { return; }
            if (rows.Count != _planInfo.Count) _planInfo.ClearRows();
            foreach (var (label, value) in rows) _planInfo.Set(label, value);
        }

        // ---- telemetry ----

        void ReadVessel()
        {
            var cv = Context.Plugin?.current_vessel;
            var vc = cv?.VesselComponent;
            if (vc == null)
            {
                _altKm = float.NaN;
                return;
            }
            try
            {
                var orbit = vc.Orbit;
                var body = cv.currentBody();
                double radius = body != null ? body.radius : orbit.referenceBody.radius;
                _altKm = (float)(cv.GetSeaAltitude() / 1000);
                _landed = vc.Landed;
                _altitude.SetValue(Ui.Num(_altKm, _altKm < 100 ? "0.0" : "0"));
                double speed = cv.VesselVehicle != null ? cv.VesselVehicle.SurfaceSpeed : double.NaN;
                _speed.SetValue(double.IsNaN(speed) ? "—" : Ui.Num((float)speed, "0"));
                float ap = (float)((orbit.Apoapsis - orbit.referenceBody.radius) / 1000);
                _apoapsis.SetValue(Ui.Num(ap, Mathf.Abs(ap) < 100 ? "0.0" : "0"));
                _timeToAp.SetValue(Duration(orbit.TimeToAp));
                _atmoKm = body != null && body.hasAtmosphere ? (float)(body.atmosphereDepth / 1000) : -1;
                _site = body != null ? body.Name : "";
                ReadStages(vc);
                RecordFlown(vc, radius);
            }
            catch (Exception)
            {
                // A vessel between two states (scene change, staging): the next frame reads it again.
            }
        }

        static string Duration(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s) || s < 0) return "—";
            if (s >= 3600) return $"{(int)(s / 3600)}:{(int)(s % 3600 / 60):00}:{(int)(s % 60):00}";
            return $"{(int)(s / 60)}:{(int)(s % 60):00}";
        }

        // The game's stage list (VesselDeltaV.StageInfo): the stages with Δv, in firing order (the
        // active one, the highest number, first and gold).
        readonly List<(int stage, double dv)> _stageRows = new();
        static readonly Comparison<(int stage, double dv)> ByStageDown = (a, b) => b.stage.CompareTo(a.stage);

        void ReadStages(KSP.Sim.impl.VesselComponent vc)
        {
            var info = vc.VesselDeltaV?.StageInfo;
            _stageRows.Clear();
            if (info != null)
                foreach (var st in info)
                {
                    if (st == null) continue;
                    double dv = st.DeltaVActual;
                    if (double.IsNaN(dv) || double.IsInfinity(dv)) dv = st.DeltaVinVac;
                    if (double.IsNaN(dv) || double.IsInfinity(dv) || dv < 0.5) continue;
                    _stageRows.Add((st.Stage, dv));
                }
            _stageRows.Sort(ByStageDown);
            double max = 1;
            foreach (var r in _stageRows) max = Math.Max(max, r.dv);
            for (int i = 0; i < MaxStages; i++)
            {
                bool shown = i < _stageRows.Count;
                Show(_stages[i], shown);
                if (!shown) continue;
                _stageLabels[i].text = "S" + (i + 1) + " · stage " + _stageRows[i].stage;
                _stages[i].SetValue(Ui.Num((float)_stageRows[i].dv, "#,0").Replace(',', ' ') + " m/s");
                _stages[i].SetFraction((float)(_stageRows[i].dv / max));
            }
            _stagesNote.Set(_stageRows.Count == 0 ? "No stage with Δv in the game's stage data." : "");
        }

        void RecordFlown(KSP.Sim.impl.VesselComponent vc, double radius)
        {
            var pilot = _pilot;
            bool flying = pilot != null && pilot.isRunning && pilot.status != LiftPilot.LiftStatus.WaitLaunch;
            if (!flying)
            {
                _recording = false;
                return;
            }
            if (!_recording)
            {
                _recording = true;
                _flown.Clear();
                _launchLat = vc.Latitude;
                _launchLon = vc.Longitude;
            }
            var pt = new Vector2(LiftProfileModel.DownrangeKm(_launchLat, _launchLon, vc.Latitude, vc.Longitude, radius), Mathf.Max(0, _altKm));
            float minStep = Mathf.Max(0.05f, _settings.target_Ap_km * 0.004f);
            if (_flown.Count == 0 || Vector2.Distance(pt, _flown[_flown.Count - 1]) >= minStep)
            {
                _flown.Add(pt);
                if (_flown.Count > MaxFlownSamples)
                    for (int i = _flown.Count - 2; i > 0; i -= 2) _flown.RemoveAt(i);
            }
        }

        // ---- pilot status (LiftUI.onUpdateUI) ----

        void UpdateStatus()
        {
            var pilot = _pilot;
            var st = _status.Status;
            st.Reset();
            bool running = pilot != null && pilot.isRunning;
            if (pilot == null)
                st.Status(Context.InGame ? "The Lift pilot is not loaded yet" : "Lift autopilot not enabled");
            else if (running)
            {
                st.Status(string.IsNullOrEmpty(pilot.start_note)
                    ? $"Status : {pilot.status}"
                    : $"Status : {pilot.status} ({pilot.start_note})");
                pilot.current_subpilot?.updateUI(_status.Root, st);
            }
            else if (!string.IsNullOrEmpty(pilot.end_status))
                st.Status("Final status : " + pilot.end_status, pilot.result_ok ? StatusLine.Level.Normal : StatusLine.Level.Warning);
            else
                st.Status("Lift autopilot not enabled");
            _status.Refresh();
            // A run on track reads OK (prograde), as the mockup's pill; warnings and errors stay coral.
            if (running && _status.StatusLevel == StatusLine.Level.Normal && !string.IsNullOrEmpty(_status.StatusText))
                _status.Pill.Set(StatusKind.Ok, _status.StatusText);
            else if (!running && pilot != null && pilot.result_ok && !string.IsNullOrEmpty(pilot.end_status))
                _status.Pill.Set(StatusKind.Ok, _status.StatusText);
        }

        // ---- LIFT INFO (LiftUI.UpdateLiftInfos) ----

        void UpdateInfo()
        {
            _infoSeen.Clear();
            _gtPhase = null;
            var sub = _pilot?.current_subpilot;
            if (sub == null) AddInfoRow("Status", "Not running");
            else sub.UpdateInfoRows(_addInfoRow);
            for (int i = _infoKeys.Count - 1; i >= 0; i--)
                if (!_infoSeen.Contains(_infoKeys[i]))
                {
                    _liftInfo.Remove(_infoKeys[i]);
                    _infoKeys.RemoveAt(i);
                }
        }

        void AddInfoRow(string label, string value)
        {
            if (label == "Phase") _gtPhase = value;
            if (!_infoSeen.Add(label)) return;
            if (_liftInfo.Get(label) == null) _infoKeys.Add(label);
            _liftInfo.Set(label, value);
        }

        // ---- timeline ----

        void UpdateTimeline(bool force = false)
        {
            var s = _settings;
            var pilot = _pilot;
            bool running = pilot != null && pilot.isRunning;
            LiftGoal goal = running && pilot.transfer != null ? pilot.transfer.Goal : s.goal.V;
            string key = goal + "|" + s.adjust.V + "|" + s.auto_circularize.V;
            if (force || key != _timelineKey)
            {
                _timelineKey = key;
                _steps = LiftPhases.Steps(goal, s.adjust.V, s.auto_circularize.V);
                _timelineIndex = -1;
            }
            var status = pilot != null ? pilot.status : LiftPilot.LiftStatus.Off;
            AscentProfile profile = running ? pilot.run_profile : LiftGoalRules.ProfileFor(s.goal.V, s.ascent_profile.V);
            bool vertical = LiftPhases.IsVerticalClimb(profile, float.IsNaN(_altKm) ? 0 : _altKm, s.start_altitude_km.V, _gtPhase);
            bool endedOk = !running && pilot != null && pilot.result_ok && !string.IsNullOrEmpty(pilot.end_status);
            int index = LiftPhases.Index(_steps, status, vertical, endedOk);
            if (index == _timelineIndex) return;
            _timelineIndex = index;
            Page?.SetTimeline(_steps, index);
        }

        // ---- the profile view: redrawn when its data changed ----

        void UpdateProfile()
        {
            var s = _settings;
            bool guided = (IsRunning ? _pilot.run_profile : LiftGoalRules.ProfileFor(s.goal.V, s.ascent_profile.V)) == AscentProfile.Guided;
            Vector2? vessel = null;
            if (_flown.Count > 0) vessel = _flown[_flown.Count - 1];
            else if (_landed && !float.IsNaN(_altKm)) vessel = new Vector2(0, Mathf.Max(0, _altKm));
            float target = s.target_Ap_km;
            bool moved = vessel.HasValue != _drawnVessel.HasValue
                         || (vessel.HasValue && Vector2.Distance(vessel.Value, _drawnVessel.Value) > Mathf.Max(0.05f, target * 0.003f));
            if (!moved && guided == _drawnGuided && target == _drawnTarget && s.start_altitude_km.V == _drawnStart
                && s.mid_rotate_altitude_km == _drawnMid && s.end_rotate_altitude_km == _drawnEnd
                && _atmoKm == _drawnAtmo && _flown.Count == _drawnFlown && _site == _drawnSite)
                return;
            _drawnGuided = guided;
            _drawnTarget = target;
            _drawnStart = s.start_altitude_km.V;
            _drawnMid = s.mid_rotate_altitude_km;
            _drawnEnd = s.end_rotate_altitude_km;
            _drawnAtmo = _atmoKm;
            _drawnFlown = _flown.Count;
            _drawnVessel = vessel;
            _drawnSite = _site;
            _profileView.SetData(guided, s.start_altitude_km.V, s.mid_rotate_altitude_km, s.end_rotate_altitude_km,
                target, _atmoKm, _flown, vessel, _site);
        }

        void UpdateNote()
        {
            string note;
            if (_pilot == null)
                note = Context.InGame ? "The Lift pilot loads with the flight" : "Start flies from the pad, in flight";
            else if (IsRunning)
                note = _timelineIndex >= 0 && _timelineIndex + 1 < _steps.Count ? "Next: " + _steps[_timelineIndex + 1] : "";
            else
                note = "Ready: " + GoalLabels[(int)_settings.goal.V] + " · Ap " + _settings.target_Ap_km + " km";
            if (note != Page.Actions.Note) Page.Actions.SetNote(note);
        }

        // ------------------------------------------------------------------ sample

        public override void ShowSample()
        {
            _sample = true;
            var s = _settings;
            s.goal.V = LiftGoal.Orbit;
            s.ascent_profile.V = AscentProfile.Guided;
            s.destination_Ap_km.V = 100;
            s.heading.V = 90;
            s.start_altitude_km.V = 1;
            // A curve that climbs to about 94 km at the plot's edge, as the mockup's (45 deg at 41 km).
            s.mid_rotate_ratio.V = 0.4f;
            s.end_rotate_ratio.V = 1f;
            s.coasting_warp.V = true;
            s.auto_circularize.V = true;
            StagingSettings.auto_staging.V = true;

            _altitude.SetValue("18.4");
            _speed.SetValue("742");
            _apoapsis.SetValue("41.2");
            _timeToAp.SetValue("0:52");
            var stages = new[] { ("S1 booster", 1950f), ("S2 upper", 2300f), ("S3 lander", 562f) };
            for (int i = 0; i < MaxStages; i++)
            {
                bool shown = i < stages.Length;
                Show(_stages[i], shown);
                if (!shown) continue;
                _stageLabels[i].text = stages[i].Item1;
                _stages[i].SetValue(Ui.Num(stages[i].Item2, "#,0").Replace(',', ' ') + " m/s");
                _stages[i].SetFraction(stages[i].Item2 / 2300f);
            }
            _stagesNote.Set("");
            _status.Pill.Set(StatusKind.Ok, "Gravity turn on track · AoA 2.1°");
            _status.Pill.style.display = DisplayStyle.Flex;
            _status.Console.Set("");
            _status.Progress.style.display = DisplayStyle.None;

            // A flown path along the first part of the planned curve, the vessel at 18.4 km.
            var planned = LiftProfileModel.PlannedPath(s.start_altitude_km.V, s.mid_rotate_altitude_km, s.end_rotate_altitude_km, 100, 168);
            _flown.Clear();
            foreach (var p in planned)
            {
                if (p.y > 18.4f) break;
                _flown.Add(p);
            }
            _profileView.SetData(true, s.start_altitude_km.V, s.mid_rotate_altitude_km, s.end_rotate_altitude_km, 100, 70,
                _flown, _flown.Count > 0 ? _flown[_flown.Count - 1] : (Vector2?)null, "Kerbin · KSC Pad 1");

            _steps = LiftPhases.Steps(LiftGoal.Orbit, true, true);
            _timelineIndex = 1;
            Page.SetTimeline(_steps, 1);
            Page.Actions.SetNote("Next: coast at 41 km · stage 1 dry in 0:38");
            _main.Text = "Running…";
            _main.SetEnabled(true);
        }
    }
}
