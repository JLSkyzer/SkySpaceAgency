using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.Maneuver;
using KTools;
using SkySpaceAgency.Controller;
using SkySpaceAgency.Node;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// NODES (docs/design/ui/nodes.png): the classic Node tab (node executor, Circularize at AP/PE,
    /// node info, ADVANCED turn / warp / burn / start settings, Reset) and the classic Attitude tab
    /// (card "Hold attitude"). Plan: the vessel's maneuver nodes and the main settings; view: the
    /// burn (vessel nose against the burn vector) and the attitude hold; side: burn data and the
    /// pilot's status. Inventory: docs/superpowers/notes/new-ui-inventory/nodes.md.
    /// </summary>
    public class NodesTab : AppTab
    {
        public NodesTab(AppTabContext context) : base(context) { }
        public override string Code => "nodes";
        public override string Label => "Nodes";

        const int MaxListed = 6;
        static readonly BurnWarpMode[] WarpValues = { BurnWarpMode.Off, BurnWarpMode.Physics, BurnWarpMode.Rails };
        static readonly string[] WarpLabels = { "Off", "Physics x4", "Rails (native)" };
        static readonly NodeExSettings.StartMode[] StartValues =
            { NodeExSettings.StartMode.precise, NodeExSettings.StartMode.constant, NodeExSettings.StartMode.half_duration };
        static readonly string[] StartLabels = { "T0", "Before T0", "Mid-duration" };

        /// <summary>The settings the controls are bound to: the pilot's, or a copy built in the editor.</summary>
        public NodeExSettings Settings { get; private set; }
        public AppPilotStatus Status { get; } = new AppPilotStatus();
        public NodesNodeList Nodes { get; private set; }
        public NodesBurnView Burn { get; private set; }
        public AppInfoTable NodeInfo { get; private set; }
        public AppSegmented WarpDuringBurns { get; private set; }
        public AppTile DvLeft { get; private set; }
        public AppTile BurnTime { get; private set; }
        public AppTile Throttle { get; private set; }
        public AppTile Warp { get; private set; }
        public AppNote AppliedNote { get; private set; }
        public AppButton MainButton { get; private set; }
        public AppButton AbortButton { get; private set; }
        public AppButton CircularizeAp { get; private set; }
        public AppButton CircularizePe { get; private set; }
        public AppButton HoldButton { get; private set; }
        public AppCard HoldCard { get; private set; }
        UnityEngine.UIElements.Label _holdNote;

        AppField _leadTime, _maxAngle;
        AppSlider _warpSpeed;
        VisualElement _startBefore, _appliedFill;
        bool _sample;
        string[] _steps;
        int _phase = -1;
        bool? _infoHasNode;
        bool? _holding;
        string _hudPhase = "";
        string _hudDv = "—", _hudTime = "—", _hudThrottle = "—";
        readonly HudState _hud = new HudState { Pilot = "NODES" };
        static bool _tickErrorLogged;

        // ---- build ----

        protected override void OnBuild(AppPage page)
        {
            var pilot = Context.Pilot<NodeExPilot>();
            Settings = pilot?.settings ?? new NodeExSettings();

            BuildPlan(page);
            BuildView(page);
            BuildSide(page);

            SetPhase(NodesLogic.NodeSteps, 0);
            page.Actions.SetNote(pilot == null ? "The node executor flies in flight, with a vessel" : "");
            // Circularize at AP / PE: in the action bar (where the mockup has "Skip node"), so the plan
            // column keeps the mockup's height.
            CircularizeAp = page.Actions.AddButton("Circularize at AP", AppButton.Kind.Plain, () => Context.Pilot<NodeExPilot>()?.StartCircularize(true));
            CircularizePe = page.Actions.AddButton("Circularize at PE", AppButton.Kind.Plain, () => Context.Pilot<NodeExPilot>()?.StartCircularize(false));
            AbortButton = page.Actions.AddButton("Abort", AppButton.Kind.Abort, () => Context.Pilot<NodeExPilot>()?.Stop());
            MainButton = page.Actions.AddButton("Start", AppButton.Kind.Main, ToggleRun);
            var att = Context.Pilot<AttitudePilot>();
            UpdateButtons(pilot, att);
            UpdateHold(att);
        }

        void BuildPlan(AppPage page)
        {
            var plan = new AppCard("Maneuver nodes");
            Nodes = new NodesNodeList();
            plan.Add(Nodes);

            plan.Add(Ui.Label("Warp during burns", "ssa-field__label", "ssa-nodes-label"));
            WarpDuringBurns = AppBind.Segmented(BurnWarpSettings.mode, WarpValues, WarpLabels);
            plan.Add(WarpDuringBurns);

            var fields = Ui.Box("ssa-fields");
            _leadTime = AppBind.Field("Lead time", WarpToSettings.warp_safe_duration, "s");
            _maxAngle = AppBind.Field("Max angle", TurnToSettings.max_angle_maneuver, "°", "0.0");
            _maxAngle.AddToClassList("ssa-field--last");
            fields.Add(_leadTime);
            fields.Add(_maxAngle);
            plan.Add(fields);

            plan.Add(AppBind.Switch("Warp to the node", Settings.auto_warp));
            plan.Add(AppBind.Switch("Pause the game when done", Settings.pause_on_end));

            var adv = new AppSection("Advanced");
            adv.Add(Ui.Label("TURN", "ssa-section-title"));
            adv.Add(AppBind.Slider("Max angular speed", TurnToSettings.max_angular_speed, v => Ui.Num(v, "0.00"), 0.01f));
            adv.Add(Ui.Label("WARP", "ssa-section-title"));
            _warpSpeed = AppBind.Slider("Warp speed (safe - quick)", WarpToSettings.warp_speed, v => Ui.Num(v, "0.0"), 0.1f);
            adv.Add(_warpSpeed);
            adv.Add(Ui.Label("BURN", "ssa-section-title"));
            adv.Add(AppBind.Slider("Adjusting rate (precise - quick)", BurnManeuverSettings.burn_adjust, 0.5f, 2f, v => Ui.Num(v, "0.00") + " m/s", 0.01f));
            adv.Add(AppBind.Slider("Precision", BurnManeuverSettings.max_dv_error, 0.001f, 0.1f, v => Ui.Num(v, "0.000") + " m/s", 0.001f));
            adv.Add(AppBind.Switch("Steer during the burn", BurnManeuverSettings.rotate_during_burn));
            adv.Add(Ui.Label("START THE BURN AT", "ssa-section-title"));
            adv.Add(AppBind.Segmented(Settings.start_mode, StartValues, StartLabels));
            _startBefore = AppBind.Slider("Start before T0", Settings.start_before, 0, 10, v => Ui.Num(v, "0.0") + " s", 0.1f);
            adv.Add(_startBefore);
            var reset = new AppButtonRow();
            reset.Add("Reset node settings", () => SettingsFile.Instance.Reset("node_ex"));
            adv.Add(reset);
            plan.Add(adv);

            // As the classic tab: the warp settings only matter with the warp to the node, the
            // start offset only with "Before T0".
            Settings.auto_warp.listeners += OnAutoWarp;
            OnAutoWarp(Settings.auto_warp.V);
            Settings.start_mode.listeners += OnStartMode;
            OnStartMode(Settings.start_mode.V);

            page.Plan.Add(plan);
        }

        void OnAutoWarp(bool on)
        {
            _leadTime.SetEnabled(on);
            _warpSpeed.SetEnabled(on);
        }

        void OnStartMode(NodeExSettings.StartMode mode) =>
            _startBefore.style.display = mode == NodeExSettings.StartMode.constant ? DisplayStyle.Flex : DisplayStyle.None;

        void BuildView(AppPage page)
        {
            var burn = new AppCard("Burn", "attitude and remaining Δv", sky: true);
            Burn = new NodesBurnView();
            burn.Add(Burn);
            page.View.Add(burn);

            // One row (the view keeps the page's height): elevation, heading, Hold; the classic
            // Attitude tab's status line is the card's note.
            HoldCard = new AppCard("Hold attitude", "");
            HoldCard.AddToClassList("ssa-nodes-attitude");
            _holdNote = HoldCard.Q<UnityEngine.UIElements.Label>(className: "ssa-card__note");
            var row = Ui.Box("ssa-fields", "ssa-nodes-hold");
            row.Add(AppBind.Field("Elevation (−90 to 90)", AttitudeSettings.elevation, "°", "0.0"));
            var headField = AppBind.Field("Heading (0 to 360)", AttitudeSettings.heading, "°", "0.0");
            row.Add(headField);
            HoldButton = new AppButton("Hold", AppButton.Kind.Plain, ToggleHold);
            HoldButton.AddToClassList("ssa-nodes-hold__button");
            row.Add(HoldButton);
            HoldCard.Add(row);
            page.View.Add(HoldCard);
        }

        void BuildSide(AppPage page)
        {
            var data = new AppCard("Burn data");
            var tiles = Ui.Box("ssa-tiles");
            DvLeft = new AppTile("Δv left", "—", "m/s", AppTile.Kind.Gold);
            BurnTime = new AppTile("Burn time", "—");
            Throttle = new AppTile("Throttle", "—", "%");
            Warp = new AppTile("Warp", "—", "", AppTile.Kind.Data);
            tiles.Add(DvLeft);
            tiles.Add(BurnTime);
            tiles.Add(Throttle);
            tiles.Add(Warp);
            data.Add(tiles);
            data.Add(Ui.Label("Δv APPLIED", "ssa-section-title"));
            var bar = Ui.Box("ssa-nodes-applied");
            _appliedFill = Ui.Box("ssa-nodes-applied__fill");
            SsaPalette.SetBackground(_appliedFill, SsaPalette.GoldGradient);
            _appliedFill.style.width = Length.Percent(0);
            bar.Add(_appliedFill);
            data.Add(bar);
            AppliedNote = new AppNote("");
            AppliedNote.AddToClassList("ssa-nodes-applied__note");
            AppliedNote.Set("no node");
            data.Add(AppliedNote);
            // The classic NODE INFO foldout, in the room the mockup leaves under Δv applied.
            data.Add(Ui.Label("NODE INFO", "ssa-section-title"));
            NodeInfo = new AppInfoTable();
            NodeInfo.Set("Node", "none");
            data.Add(NodeInfo);
            data.Add(Ui.Box("ssa-grow"));
            data.Add(Status.Pill);
            data.Add(Status.Console);
            data.Add(Status.Progress);
            page.Side.Add(data);
        }

        // ---- actions (the classic tabs' calls) ----

        void ToggleRun()
        {
            var pilot = Context.Pilot<NodeExPilot>();
            if (pilot == null) return;
            bool start = !pilot.isRunning;
            if (start) Status.Reset();
            pilot.isRunning = start;
        }

        void ToggleHold()
        {
            var att = Context.Pilot<AttitudePilot>();
            if (att == null) return;
            att.isRunning = !att.isRunning;
        }

        // ---- every frame while shown ----

        public override void Tick()
        {
            if (_sample) return;
            var pilot = Context.Pilot<NodeExPilot>();
            var att = Context.Pilot<AttitudePilot>();
            UpdateButtons(pilot, att);
            UpdateHold(att);
            if (pilot == null || !Context.InGame) return;
            // Two guarded parts: a failing status must not stop the burn data, nor the reverse.
            try
            {
                pilot.UpdateStatus(Status.Root, Status.Status);
                Status.Refresh();
            }
            catch (Exception e) { LogOnce("status", e); }
            try { ReadGame(pilot); }
            catch (Exception e) { LogOnce("burn data", e); }
        }

        static void LogOnce(string what, Exception e)
        {
            if (_tickErrorLogged) return;
            _tickErrorLogged = true;
            ModIdentity.Logger("AppUI.Nodes").LogError($"[NodesTab] {what}: {e}");
        }

        void UpdateButtons(NodeExPilot pilot, AttitudePilot att)
        {
            bool running = pilot != null && pilot.isRunning;
            MainButton.SetEnabled(pilot != null);
            string main = running ? "Running…" : "Start";
            if (MainButton.Text != main) MainButton.Text = main;
            AbortButton.SetEnabled(running);
            CircularizeAp.SetEnabled(pilot != null && !running);
            CircularizePe.SetEnabled(pilot != null && !running);
            HoldButton.SetEnabled(att != null);
        }

        void UpdateHold(AttitudePilot att)
        {
            bool holding = att != null && att.isRunning;
            if (_holding == holding) return;
            _holding = holding;
            HoldButton.Text = holding ? "Release" : "Hold";
            // The classic Attitude tab's two lines.
            SetHoldNote(holding);
        }

        void SetHoldNote(bool holding)
        {
            HoldCard.Note = holding ? "Holding attitude" : "Attitude autopilot not enabled";
            _holdNote?.EnableInClassList("ssa-nodes-hold-note--on", holding);
        }

        void ReadGame(NodeExPilot pilot)
        {
            var vessel = Context.Plugin.current_vessel;
            var game = GameManager.Instance?.Game;
            if (vessel == null || game?.UniverseModel == null)
                return;
            double now = game.UniverseModel.UniverseTime;

            List<ManeuverNodeData> nodes = game.SpaceSimulation?.Maneuvers != null && vessel.VesselVehicle != null
                ? vessel.GetManeuverNodes() : null;
            int count = nodes?.Count ?? 0;
            Nodes.SetCount(Math.Min(count, MaxListed));
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = nodes[i];
                Vector3d bv = n.BurnVector;
                Nodes.ItemAt(i).Set(i + 1, NodesLogic.Direction(bv.x, bv.y, bv.z), n.BurnRequiredDV, n.Time - now, i == 0);
            }

            // Node info, as the classic tab: the node in flight while running, the next one otherwise.
            UpdateNodeInfo(pilot.isRunning ? pilot.execute_node : pilot.next_maneuver_node, now);

            // Burn data of the next node: Δv left as BurnManeuver measures it (velocity after the
            // first maneuver minus the current orbit's velocity at its time).
            var first = count > 0 ? nodes[0] : null;
            double left = double.NaN, required = first?.BurnRequiredDV ?? 0;
            if (first != null)
            {
                var solver = vessel.GetPlanSolver();
                var orbit = vessel.VesselComponent?.Orbit;
                if (solver != null && orbit != null)
                {
                    Vector after = solver.GetVelocityAfterFirstManeuver(out double ut);
                    if (ut != 0)
                        left = (after.vector - orbit.GetOrbitalVelocityAtUTZup(ut)).magnitude;
                }
            }
            double shownLeft = double.IsNaN(left) ? required : left;
            _hudDv = first != null ? NodesLogic.Speed(shownLeft) + " m/s" : "—";
            DvLeft.SetValue(first != null ? NodesLogic.Speed(shownLeft) : "—");
            _hudTime = first != null ? NodesLogic.Clock(NodesLogic.BurnTimeLeft(first.BurnDuration, required, left)) : "—";
            BurnTime.SetValue(_hudTime);
            _hudThrottle = vessel.VesselComponent != null ? (vessel.GetThrottle() * 100).ToString("0", CultureInfo.InvariantCulture) : "—";
            Throttle.SetValue(_hudThrottle);
            float rate = TimeWarpTools.CurrentRate;
            bool physics = game.ViewController?.TimeWarp != null && game.ViewController.TimeWarp.IsPhysicsTimeWarp;
            Warp.SetValue("x" + rate.ToString("0.##", CultureInfo.InvariantCulture), rate > 1.001f ? (physics ? "phys" : "rails") : "");
            float applied = first != null ? NodesLogic.Applied(required, left) : 0;
            _appliedFill.style.width = Length.Percent(applied * 100);
            AppliedNote.Set(first != null
                ? $"{NodesLogic.Speed(Math.Max(0, required - shownLeft))} of {NodesLogic.Speed(required)} m/s"
                : "no node");

            ReadAttitude(vessel);

            // Phase of the run.
            string[] steps = NodesLogic.NodeSteps;
            int phase = 0;
            if (pilot.isRunning)
            {
                var exec = pilot.execute_node;
                bool before = pilot.mode == NodeExPilot.Mode.Burn && exec != null &&
                    now < NodesLogic.IgnitionUT(Settings.start_mode.V, exec.Time, exec.BurnDuration, Settings.start_before.V);
                var closed = (pilot.current_executor.sub_controler as TargetOrbitBurn)?.mode ?? TargetOrbitBurn.Mode.Warp;
                phase = NodesLogic.Phase(pilot.mode, before, closed, out steps);
            }
            SetPhase(steps, phase);
            _hudPhase = pilot.isRunning ? steps[phase] : "Ready";

            Page.Actions.SetNote(pilot.isRunning
                ? $"{steps[phase]} · Ctrl+O stops the run"
                : first != null
                    ? $"Next: node 1 {NodesLogic.When(first.Time - now)} · {NodesLogic.Speed(first.BurnRequiredDV)} m/s"
                    : "Plot a node on the map, or circularize at Ap / Pe");
        }

        void UpdateNodeInfo(ManeuverNodeData node, double now)
        {
            bool has = node != null;
            if (_infoHasNode != has)
            {
                NodeInfo.ClearRows();
                _infoHasNode = has;
            }
            if (!has)
            {
                NodeInfo.Set("Node", "none");
                return;
            }
            double dt = node.Time - now;
            NodeInfo.Set("Node in", StrTool.DurationToString(dt) + (dt < 0 ? "  (in the past)" : ""), dt < 0 ? StatusKind.Alert : StatusKind.Neutral);
            NodeInfo.Set("dV", $"{node.BurnRequiredDV:n2} m/s");
            NodeInfo.Set("Duration", StrTool.DurationToString(node.BurnDuration));
        }

        // The nose against the burn vector (as TurnTo measures it) and the pitch over the horizon.
        void ReadAttitude(SkySpaceAgency.KSPService.KSPVessel vessel)
        {
            var tel = SASTool.getTelemetry();
            bool has = false;
            float angle = float.NaN, pitch = 0;
            Vector2 dir = Vector2.zero;
            if (tel != null && vessel.VesselVehicle != null)
            {
                Rotation rot = vessel.GetRotation();
                if (tel.HasManeuver)
                {
                    Vector md = tel.ManeuverDirection;
                    Rotation r = Rotation.Reframed(rot, md.coordinateSystem);
                    Vector3d fwd = (r.localRotation * Vector3.up).normalized;
                    angle = (float)Vector3d.Angle(md.vector, fwd);
                    Vector3d local = QuaternionD.Inverse(r.localRotation) * md.vector;
                    dir = new Vector2((float)local.x, (float)-local.z);
                    has = true;
                }
                Vector up = tel.HorizonUp;
                Rotation ru = Rotation.Reframed(rot, up.coordinateSystem);
                Vector3d nose = (ru.localRotation * Vector3.up).normalized;
                pitch = 90 - (float)Vector3d.Angle(up.vector, nose);
            }
            Burn.Set(has, angle, dir, pitch, TurnToSettings.max_angle_maneuver.V);
        }

        void SetPhase(string[] steps, int phase)
        {
            if (ReferenceEquals(steps, _steps) && phase == _phase) return;
            _steps = steps;
            _phase = phase;
            Page.SetTimeline(steps, phase);
        }

        // ---- running state, compact bar ----

        public override bool IsRunning =>
            (Context.Pilot<NodeExPilot>()?.isRunning ?? false) || (Context.Pilot<AttitudePilot>()?.isRunning ?? false);

        public override HudState Hud
        {
            get
            {
                var pilot = Context.Pilot<NodeExPilot>();
                if (pilot != null && pilot.isRunning)
                {
                    _hud.Pilot = "NODES";
                    _hud.Phase = _hudPhase;
                    _hud.Phases = _steps;
                    _hud.CurrentPhase = Math.Max(0, _phase);
                    _hud.Readouts.Clear();
                    _hud.Readouts.Add(("Δv left", _hudDv));
                    _hud.Readouts.Add(("Burn", _hudTime));
                    _hud.Readouts.Add(("Throttle", _hudThrottle + " %"));
                    _hud.Stop = pilot.Stop;
                    return _hud;
                }
                var att = Context.Pilot<AttitudePilot>();
                if (att != null && att.isRunning)
                {
                    _hud.Pilot = "ATTITUDE";
                    _hud.Phase = "Holding attitude";
                    _hud.Phases = null;
                    _hud.CurrentPhase = 0;
                    _hud.Readouts.Clear();
                    _hud.Readouts.Add(("Elevation", Ui.Num(AttitudeSettings.elevation.V, "0.0") + "°"));
                    _hud.Readouts.Add(("Heading", Ui.Num(AttitudeSettings.heading.V, "0.0") + "°"));
                    _hud.Stop = () => att.isRunning = false;
                    return _hud;
                }
                return null;
            }
        }

        // ---- sample (docs/design/ui/nodes.png) ----

        public override void ShowSample()
        {
            _sample = true;
            Nodes.SetCount(2);
            Nodes.ItemAt(0).Set(1, "prograde", 412, 4 * 60 + 12, true);
            Nodes.ItemAt(1).Set(2, "normal", 38, 21 * 60 + 40, false);
            WarpDuringBurns.SetWithoutNotify(1);
            _leadTime.Input.SetValueWithoutNotify(60);
            _maxAngle.Input.SetValueWithoutNotify(2.0f);
            NodeInfo.ClearRows();
            NodeInfo.Set("Node in", "00:04:12.0");
            NodeInfo.Set("dV", "412.00 m/s");
            NodeInfo.Set("Duration", "00:00:59.0");

            Burn.Set(true, 0.8f, new Vector2(0, -1), 0, 2.0f);

            DvLeft.SetValue("286");
            BurnTime.SetValue("0:41");
            Throttle.SetValue("100");
            Warp.SetValue("x4", "phys");
            _appliedFill.style.width = Length.Percent(126f / 412f * 100);
            AppliedNote.Set("126 of 412 m/s");
            Status.Pill.Set(StatusKind.Ok, "Burning, target orbit converging");
            Status.Pill.style.display = DisplayStyle.Flex;
            Status.Console.Set("");
            Status.Progress.style.display = DisplayStyle.None;

            SetHoldNote(false);
            HoldButton.Text = "Hold";

            SetPhase(NodesLogic.NodeSteps, 3);
            Page.Actions.SetNote("The engine is cut before any rails warp, never during a burn");
            MainButton.Text = "Running…";
            MainButton.SetEnabled(true);
            AbortButton.SetEnabled(true);
            CircularizeAp.SetEnabled(true);
            CircularizePe.SetEnabled(true);
        }
    }
}
