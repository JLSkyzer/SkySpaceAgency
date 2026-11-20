using System;
using System.Collections.Generic;
using System.Text;
using KSP.Sim;
using SkySpaceAgency.KSPService;
using SkySpaceAgency.Node;
using SkySpaceAgency.OrbitPlanning;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The ORBIT tab (docs/design/ui/orbit.png), migrated from the classic Orbit tab (Orbit.uxml,
    /// OrbitUI): targets Ap / Pe / inclination (OrbitSettings), a live preview of the plan from the
    /// vessel's orbit (OrbitPlanner) drawn in the centre view and listed on the right, "Create nodes"
    /// which creates the first node of the plan exactly as the classic "Create maneuvers" does (one
    /// node at a time, OrbitPlanRun), then Start / Stop which runs the Node pilot on it, as the Node tab's
    /// Start does. The tab has no pilot of its own: the Node pilot flies each step and plans the next.
    /// </summary>
    public class OrbitTab : AppTab
    {
        public const string Hint = "Tick the targets to change, then create the nodes. Unticked targets keep the " +
            "current value. Start flies the plan with the Node pilot, one node at a time.";

        const double Rad2Deg = 180 / Math.PI;
        const float RefreshSeconds = 0.5f;

        public OrbitTab(AppTabContext context) : base(context) { }
        public override string Code => "orbit";
        public override string Label => "Orbit";

        // ---- Controls ----
        public AppSegmented Source;
        public AppField ApField, PeField, IncField;
        public AppTextField NodeField;
        public AppSwitch ApSwitch, PeSwitch, IncSwitch, OneNodeSwitch;
        public AppCard ViewCard;
        public OrbitView View;
        public AppTile ApTile, PeTile, IncTile, PeriodTile;
        public AppInfoTable PlanRows, TotalRows;
        public AppNote CaptureNote, Console;
        public AppPill Pill;
        public AppButton PlanButton, MainButton;

        // ---- State ----
        readonly ManeuverCreatorHolder _creator = new ManeuverCreatorHolder();
        string _message;          // a refusal of the last Create click (the classic summary's error texts)
        string _createdSummary;   // the plan as made by Create (the progress line goes under it)
        List<string> _createdSteps;
        OrbitPlan _preview;
        string _previewError;
        string _previewSummary;
        bool _hasReading;
        Reading _reading;
        float _nextRefresh;
        bool _dirty = true;
        string _rowsKey = "";
        string _timelineKey = "";
        readonly List<string> _phases = new List<string>();
        int _phase;
        bool _sample;   // ShowSample's values stay (the harness may tick)
        readonly HudState _hud = new HudState { Pilot = "ORBIT" };

        /// <summary>The vessel's trajectory now, as the classic OrbitUI reads it.</summary>
        public struct Reading
        {
            public Vector3d R, V;
            public double UT;
            public BodyInfo Body;
            public OrbitElements El;
            public double GameInclinationRad;
            public string BodyName;
            public double ApKm => (El.ApoapsisRadius - Body.Radius) / 1000;
            public double PeKm => (El.PeriapsisRadius - Body.Radius) / 1000;
            public double IncDeg => El.InclinationRad * Rad2Deg;
        }

        // ManeuverCreator touches the game only when used: made at the first Create.
        sealed class ManeuverCreatorHolder
        {
            ManeuverCreator _c;
            public ManeuverCreator Get() => _c ??= new ManeuverCreator();
        }

        NodeExPilot NodePilot => Context.Pilot<NodeExPilot>();

        // ---------------------------------------------------------------- Build

        protected override void OnBuild(AppPage page)
        {
            page.AddToClassList("ssa-orbit");
            _hud.Stop = () => NodePilot?.Stop();

            // TARGET ORBIT
            var target = new AppCard("Target orbit");
            Source = new AppSegmented(new[] { "From orbit", "From flyby" }, 0);
            Source.tooltip = "Found from the trajectory: an open one (a flyby) is captured first, then shaped.";
            Source.changed += _ => Source.SetWithoutNotify(_reading.Body.Mu > 0 && IsFlyby(_reading) ? 1 : 0);
            target.Add(Source);

            var row1 = Ui.Box("ssa-fields", "ssa-orbit__fields");
            ApField = AppBind.Field("Apoapsis", OrbitSettings.ap_km, "km");
            PeField = AppBind.Field("Periapsis", OrbitSettings.pe_km, "km");
            PeField.AddToClassList("ssa-field--last");
            row1.Add(ApField);
            row1.Add(PeField);
            target.Add(row1);

            var row2 = Ui.Box("ssa-fields", "ssa-orbit__fields");
            IncField = AppBind.Field("Inclination", OrbitSettings.inc_deg, "°", "0.0#");
            NodeField = new AppTextField("Ascending node", "free");
            NodeField.AddToClassList("ssa-field--last");
            NodeField.tooltip = "Not chosen: the plane change is made at the farther of the two orbital nodes.";
            NodeField.SetEnabled(false);
            row2.Add(IncField);
            row2.Add(NodeField);
            target.Add(row2);

            var switches = Ui.Box("ssa-orbit__switches");
            ApSwitch = AppBind.Switch("Change Ap", OrbitSettings.ap_enabled);
            ApSwitch.tooltip = "Change the apoapsis altitude";
            PeSwitch = AppBind.Switch("Change Pe", OrbitSettings.pe_enabled);
            PeSwitch.tooltip = "Change the periapsis altitude";
            IncSwitch = AppBind.Switch("Change inclination", OrbitSettings.inc_enabled);
            IncSwitch.tooltip = "Change the inclination (0 equatorial, 90 polar)";
            OneNodeSwitch = new AppSwitch("One node at a time", true);
            OneNodeSwitch.tooltip = "Always on: the game keeps only one node of a plan, so each next node is made " +
                "after the previous burn, from the orbit actually reached.";
            OneNodeSwitch.SetEnabled(false);
            OneNodeSwitch.AddToClassList("ssa-orbit__locked");
            switches.Add(ApSwitch);
            switches.Add(PeSwitch);
            switches.Add(IncSwitch);
            switches.Add(OneNodeSwitch);
            target.Add(switches);
            page.Plan.Add(target);

            // Ticking a target starts it from the current value (classic OrbitUI, spec).
            OrbitSettings.ap_enabled.listeners += on => { if (on && TryReadClosed(out var c)) OrbitSettings.ap_km.V = (float)Math.Round(c.ApKm, 1); };
            OrbitSettings.pe_enabled.listeners += on => { if (on && TryReadClosed(out var c)) OrbitSettings.pe_km.V = (float)Math.Round(c.PeKm, 1); };
            OrbitSettings.inc_enabled.listeners += on => { if (on && TryReadClosed(out var c)) OrbitSettings.inc_deg.V = (float)Math.Round(c.IncDeg, 2); };
            // A changed target: the preview is planned again at the next tick, an old refusal is moot.
            OrbitSettings.ap_enabled.listeners += _ => Changed();
            OrbitSettings.pe_enabled.listeners += _ => Changed();
            OrbitSettings.inc_enabled.listeners += _ => Changed();
            OrbitSettings.ap_km.listeners += _ => Changed();
            OrbitSettings.pe_km.listeners += _ => Changed();
            OrbitSettings.inc_deg.listeners += _ => Changed();

            // ORBIT VIEW
            ViewCard = new AppCard("Orbit view", "top view", sky: true);
            View = new OrbitView();
            ViewCard.Add(View);
            page.View.Add(ViewCard);

            // CURRENT ORBIT
            var side = new AppCard("Current orbit");
            var tiles = Ui.Box("ssa-tiles");
            ApTile = new AppTile("Apoapsis", "—", "km");
            PeTile = new AppTile("Periapsis", "—", "km");
            IncTile = new AppTile("Inclination", "—", "°");
            PeriodTile = new AppTile("Period", "—");
            tiles.Add(ApTile);
            tiles.Add(PeTile);
            tiles.Add(IncTile);
            tiles.Add(PeriodTile);
            side.Add(tiles);
            side.Add(Ui.Label("PLAN", "ssa-section-title"));
            CaptureNote = new AppNote();
            CaptureNote.AddToClassList("ssa-orbit__capture");
            CaptureNote.Set("");
            side.Add(CaptureNote);
            PlanRows = new AppInfoTable();
            PlanRows.AddToClassList("ssa-orbit__plan");
            side.Add(PlanRows);
            TotalRows = new AppInfoTable();
            TotalRows.AddToClassList("ssa-orbit__total");
            side.Add(TotalRows);
            side.Add(Ui.Box("ssa-grow"));
            Pill = new AppPill(StatusKind.Neutral, "");
            side.Add(Pill);
            Console = new AppNote();
            Console.AddToClassList("ssa-console");
            Console.Set(Hint);
            side.Add(Console);
            page.Side.Add(side);

            SetPhases(null, 0);
            page.Actions.SetNote("Each step is planned again from the orbit actually reached");
            PlanButton = page.Actions.AddButton("Plan", AppButton.Kind.Plain, PlanNow);
            PlanButton.tooltip = "Plan again now from the current orbit (the preview also follows the orbit by itself).";
            MainButton = page.Actions.AddButton("Create nodes", AppButton.Kind.Main, MainClicked);

            ShowReading(false, default);
            ShowPreview();
            UpdateButtons();
        }

        void Changed()
        {
            _dirty = true;
            _message = null;
        }

        // ---------------------------------------------------------------- Tick

        public override void Tick()
        {
            if (_sample)
                return;
            UpdateButtons();
            if (!Context.InGame)
                return;
            float now = Time.realtimeSinceStartup;
            if (!_dirty && now < _nextRefresh)
                return;
            _nextRefresh = now + RefreshSeconds;
            _dirty = false;
            Refresh();
        }

        /// <summary>Reads the orbit, plans the preview and shows it all (in game, at most twice a second).</summary>
        void Refresh()
        {
            _hasReading = TryReadTrajectory(out _reading);
            Preview();
            ShowReading(_hasReading, _reading);
            ShowPreview();
        }

        public override bool IsRunning => OrbitPlanRun.Active && NodePilot != null && NodePilot.isRunning;

        public override HudState Hud
        {
            get
            {
                if (!IsRunning) return null;
                _hud.Phases = _phases;
                _hud.CurrentPhase = _phase;
                _hud.Phase = _phase >= 0 && _phase < _phases.Count ? _phases[_phase] : "";
                _hud.Readouts.Clear();
                if (_hasReading)
                {
                    _hud.Readouts.Add(("Ap", _reading.El.IsClosed ? OrbitText.Km(_reading.ApKm) + " km" : "open"));
                    _hud.Readouts.Add(("Pe", OrbitText.Km(_reading.PeKm) + " km"));
                    _hud.Readouts.Add(("Inc", _reading.IncDeg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°"));
                }
                return _hud;
            }
        }

        // ---------------------------------------------------------------- Reading the game

        public static bool IsFlyby(Reading c) => OrbitPlanner.NeedsCapture(c.R, c.V, c.Body);

        bool TryReadClosed(out Reading c) => TryReadTrajectory(out c) && c.El.IsClosed;

        // Any trajectory, open ones included (the planner captures them first). OrbitState.TryReadState is
        // the classic tab's reader (also used by the Node pilot); the game's inclination is read here
        // for the frame guard.
        bool TryReadTrajectory(out Reading c)
        {
            c = default;
            if (!Context.InGame)
                return false;
            try
            {
                if (!OrbitState.TryReadState(out c.R, out c.V, out c.UT, out c.Body))
                    return false;
                c.El = OrbitMath.Elements(c.R, c.V, c.Body.Mu);
                var orbit = Context.Plugin?.current_vessel?.VesselComponent?.Orbit;
                c.GameInclinationRad = orbit != null ? orbit.inclination : double.NaN;
                c.BodyName = orbit?.referenceBody?.bodyName ?? "body";
                return true;
            }
            catch (Exception e)
            {
                SkySpaceAgencyPlugin.logger?.LogWarning("[OrbitTab] reading the orbit failed: " + e.Message);
                return false;
            }
        }

        static OrbitTargets Targets() => new OrbitTargets
        {
            ApAltitude = OrbitSettings.ap_enabled.V ? OrbitSettings.ap_km.V * 1000.0 : (double?)null,
            PeAltitude = OrbitSettings.pe_enabled.V ? OrbitSettings.pe_km.V * 1000.0 : (double?)null,
            InclinationDeg = OrbitSettings.inc_enabled.V ? OrbitSettings.inc_deg.V : (double?)null
        };

        static bool AnyTarget => OrbitSettings.ap_enabled.V || OrbitSettings.pe_enabled.V || OrbitSettings.inc_enabled.V;

        // The vessel's total Δv (m/s), NaN when the game does not give it.
        double VesselDeltaV()
        {
            var dv = Context.Plugin?.current_vessel?.VesselComponent?.VesselDeltaV;
            return dv != null ? dv.TotalDeltaVActual : double.NaN;
        }

        // ---------------------------------------------------------------- Preview

        void Preview()
        {
            _preview = null;
            _previewError = null;
            _previewSummary = null;
            if (!_hasReading)
                return;
            bool capture = IsFlyby(_reading);
            if (!capture && !AnyTarget)
            {
                _previewError = "Tick at least one target (Ap, Pe or Inclination) first.";
                return;
            }
            var plan = OrbitPlanner.Plan(_reading.R, _reading.V, _reading.UT, _reading.Body, Targets());
            var budget = OrbitPlanner.CheckCaptureBudget(plan, VesselDeltaV());
            if (budget == OrbitPlanError.CaptureBeyondDeltaV)
                _previewError = $"The capture at {_reading.BodyName} needs {plan.Capture.DeltaV:n0} m/s, the vessel has {VesselDeltaV():n0} m/s.";
            else if (!plan.Ok)
                _previewError = OrbitPlanner.Describe(plan.Error, _reading.BodyName);
            _preview = plan;
            if (plan.Ok)
                _previewSummary = Summary(plan, _reading);
        }

        /// <summary>The classic tab's plan text: capture line, each burn with its T+ time, total, final orbit.</summary>
        public static string Summary(OrbitPlan plan, Reading c)
        {
            var text = new StringBuilder();
            if (plan.Capture != null)
                text.AppendLine(OrbitState.CaptureLine(plan.Capture, c.BodyName, c.Body.Radius));
            for (int i = 0; i < plan.Burns.Count; i++)
            {
                var burn = plan.Burns[i];
                text.AppendLine($"{i + 1}. T+{OrbitText.TPlus(burn.UT - c.UT)}  {burn.Label}: {burn.DeltaV.magnitude:n1} m/s");
            }
            text.AppendLine($"Total Δv: {plan.TotalDeltaV:n1} m/s");
            text.AppendLine($"Final orbit: Ap {(plan.Final.ApoapsisRadius - c.Body.Radius) / 1000:n1} km, " +
                $"Pe {(plan.Final.PeriapsisRadius - c.Body.Radius) / 1000:n1} km, " +
                $"inclination {plan.Final.InclinationRad * Rad2Deg:n2}°");
            return text.ToString();
        }

        // ---------------------------------------------------------------- Showing

        void ShowReading(bool has, Reading c)
        {
            bool closed = has && c.El.IsClosed;
            ApTile.SetValue(has ? (closed ? OrbitText.Km(c.ApKm) : "open") : "—", closed || !has ? "km" : "");
            PeTile.SetValue(has ? OrbitText.Km(c.PeKm) : "—");
            IncTile.SetValue(has ? c.IncDeg.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "—");
            PeriodTile.SetValue(closed ? OrbitText.Duration(c.El.Period) : "—");
            Source.SetWithoutNotify(has && IsFlyby(c) ? 1 : 0);
            ViewCard.Note = has ? "top view, " + c.BodyName : "top view";

            // Unticked targets show the live current value without touching the saved target.
            ShowTarget(ApField, OrbitSettings.ap_enabled.V, closed, closed ? Math.Round(c.ApKm, 1) : 0);
            ShowTarget(PeField, OrbitSettings.pe_enabled.V, closed, closed ? Math.Round(c.PeKm, 1) : 0);
            ShowTarget(IncField, OrbitSettings.inc_enabled.V, closed, closed ? Math.Round(c.IncDeg, 2) : 0);

            View.SetModel(has ? OrbitViewModel.Build(c.Body, c.R, c.V, c.UT, _preview) : null,
                Context.InGame ? "No orbit to show: be in flight." : "The orbit shows in flight.");
        }

        static void ShowTarget(AppField field, bool enabled, bool hasOrbit, double current)
        {
            field.Input.SetEnabled(enabled);
            if (!enabled && hasOrbit)
                field.Input.SetValueWithoutNotify((float)current);
        }

        void ShowPreview()
        {
            // Plan rows (rebuilt only when the plan's text changes).
            var plan = _preview != null && _preview.Ok ? _preview : null;
            string key = plan == null ? "" : PlanKey(plan);
            if (key != _rowsKey)
            {
                _rowsKey = key;
                PlanRows.ClearRows();
                TotalRows.ClearRows();
                if (plan != null && plan.Burns.Count > 0)
                {
                    for (int i = 0; i < plan.Burns.Count; i++)
                        PlanRows.Set($"{i + 1}  {plan.Burns[i].Label}", OrbitText.Dv(plan.Burns[i].DeltaV.magnitude), StatusKind.Info);
                    TotalRows.Set("Total Δv", OrbitText.Dv(plan.TotalDeltaV), StatusKind.Ok);
                }
                CaptureNote.Set(plan?.Capture != null && _hasReading
                    ? OrbitState.CaptureLine(plan.Capture, _reading.BodyName, _reading.Body.Radius) : "");
            }

            var (kind, text) = PillFor(_message, OrbitPlanRun.Status, Context.InGame, _hasReading, _preview, _previewError);
            Pill.Set(kind, text);
            Pill.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            Console.Set(ConsoleText(_message, _createdSummary, OrbitPlanRun.Status, _previewSummary));

            UpdateTimeline(plan);
        }

        static string PlanKey(OrbitPlan plan)
        {
            var b = new StringBuilder();
            foreach (var burn in plan.Burns) b.Append(burn.Label).Append('|').Append(Math.Round(burn.DeltaV.magnitude)).Append(';');
            return b.ToString();
        }

        /// <summary>
        /// The status pill: a refusal of the last click first, then the plan in progress (Node pilot),
        /// then the preview. Pure, tested.
        /// </summary>
        public static (StatusKind kind, string text) PillFor(string message, string runStatus, bool inGame, bool hasOrbit,
            OrbitPlan preview, string previewError)
        {
            if (!string.IsNullOrEmpty(message))
                return (StatusKind.Alert, OrbitText.FirstSentence(message));
            if (!string.IsNullOrEmpty(runStatus))
            {
                var kind = runStatus.StartsWith("Plan complete") ? StatusKind.Ok
                    : runStatus.StartsWith("Plan stopped") || runStatus.StartsWith("Warning") ? StatusKind.Alert
                    : StatusKind.Info;
                return (kind, OrbitText.FirstSentence(runStatus));
            }
            if (!inGame)
                return (StatusKind.Neutral, "The plan shows in flight");
            if (!hasOrbit)
                return (StatusKind.Neutral, "No trajectory to plan from: be in flight.");
            if (!string.IsNullOrEmpty(previewError))
                return (previewError.StartsWith("Tick") ? StatusKind.Neutral : StatusKind.Alert, OrbitText.FirstSentence(previewError));
            if (preview == null)
                return (StatusKind.Neutral, "");
            if (preview.Burns.Count == 0)
                return (StatusKind.Ok, "Already on the target orbit: nothing to do.");
            return (StatusKind.Ok, $"Plan ready: {preview.Burns.Count} burn{(preview.Burns.Count > 1 ? "s" : "")}, {OrbitText.Dv(preview.TotalDeltaV)}");
        }

        /// <summary>The console: as the classic summary label (refusal, or created plan + progress), else the preview's plan text.</summary>
        public static string ConsoleText(string message, string createdSummary, string runStatus, string previewSummary)
        {
            if (!string.IsNullOrEmpty(message)) return message;
            if (createdSummary != null) return createdSummary + runStatus;
            if (!string.IsNullOrEmpty(runStatus)) return runStatus;
            if (!string.IsNullOrEmpty(previewSummary)) return previewSummary;
            return Hint;
        }

        // ---------------------------------------------------------------- Timeline

        /// <summary>Plan, one step per burn (short labels), Done.</summary>
        public static List<string> PhasesFor(IReadOnlyList<string> burnLabels)
        {
            var phases = new List<string> { "Plan" };
            if (burnLabels != null)
                foreach (var l in burnLabels) phases.Add(OrbitText.Short(l));
            phases.Add("Done");
            return phases;
        }

        /// <summary>The lit phase: Plan before a run, the step being flown, Done once complete.</summary>
        public static int PhaseIndex(int phaseCount, bool runActive, int stepsStarted, string runStatus)
        {
            if (phaseCount <= 0) return 0;
            if (runActive) return Mathf.Clamp(stepsStarted, 0, phaseCount - 2 < 0 ? 0 : phaseCount - 2);
            if (!string.IsNullOrEmpty(runStatus) && runStatus.StartsWith("Plan complete")) return phaseCount - 1;
            return 0;
        }

        void UpdateTimeline(OrbitPlan preview)
        {
            List<string> labels = null;
            if (OrbitPlanRun.Active && _createdSteps != null)
                labels = _createdSteps;
            else if (preview != null && preview.Burns.Count > 0)
            {
                labels = new List<string>();
                foreach (var b in preview.Burns) labels.Add(b.Label);
            }
            else if (_createdSteps != null && OrbitPlanRun.Status.StartsWith("Plan complete"))
                labels = _createdSteps;
            var phases = PhasesFor(labels);
            int current = PhaseIndex(phases.Count, OrbitPlanRun.Active, OrbitPlanRun.Sequence?.StepsStarted ?? 0, OrbitPlanRun.Status);
            SetPhases(phases, current);
        }

        void SetPhases(List<string> phases, int current)
        {
            phases ??= PhasesFor(null);
            string key = string.Join("|", phases) + "#" + current;
            if (key == _timelineKey) return;
            _timelineKey = key;
            _phases.Clear();
            _phases.AddRange(phases);
            _phase = current;
            Page.SetTimeline(_phases, current);
        }

        // ---------------------------------------------------------------- Actions

        void UpdateButtons()
        {
            var node = NodePilot;
            if (OrbitPlanRun.Active && node != null)
            {
                MainButton.Text = node.isRunning ? "Stop" : "Start";
                MainButton.tooltip = node.isRunning ? "Stop the Node pilot (the plan stays: Start goes on)."
                    : "Fly the plan's node with the Node pilot; each next node is made after the burn.";
            }
            else
            {
                MainButton.Text = "Create nodes";
                MainButton.tooltip = "Create the node of the plan's first step (the classic \"Create maneuvers\").";
            }
            MainButton.SetEnabled(Context.InGame);
            PlanButton.SetEnabled(Context.InGame);
        }

        void PlanNow()
        {
            _message = null;
            _dirty = true;
            if (Context.InGame) Refresh();
        }

        void MainClicked()
        {
            var node = NodePilot;
            if (OrbitPlanRun.Active && node != null)
            {
                // Same call as the Node tab's Start / Stop.
                node.isRunning = !node.isRunning;
                UpdateButtons();
                return;
            }
            CreateNodes();
            _dirty = true;
        }

        // The classic OrbitUI.CreateManeuvers, unchanged in what it checks, logs and creates; its texts
        // go to the console (the pill shows their first sentence).
        void CreateNodes()
        {
            _createdSummary = null;
            _message = null;
            if (!TryReadTrajectory(out var c))
            {
                _message = "No trajectory to plan from: be in flight.";
                return;
            }
            bool capture = IsFlyby(c);

            // Frame guard (spec): our inclination from the state vectors must match the game's,
            // in either unit (Redux documents radians, stock KSP stores degrees).
            const double FrameGuardDeg = 0.5;
            double gapRadAsDeg = Math.Abs(c.El.InclinationRad - c.GameInclinationRad) * Rad2Deg;
            double gapDegAsDeg = Math.Abs(c.IncDeg - c.GameInclinationRad);
            if (gapRadAsDeg <= FrameGuardDeg || gapDegAsDeg <= FrameGuardDeg)
            {
                string unit = gapRadAsDeg <= FrameGuardDeg ? "radians" : "degrees";
                SkySpaceAgencyPlugin.logger?.LogInfo($"[OrbitUI] frame guard: game inclination {c.GameInclinationRad} " +
                    $"matched as {unit} (computed {c.IncDeg:n3}°)");
            }
            else
            {
                _message = $"Unexpected reference frame: computed inclination {c.IncDeg:n2}°, " +
                    $"game reports {c.GameInclinationRad:n4} (raw value). No node created.";
                SkySpaceAgencyPlugin.logger?.LogWarning($"[OrbitUI] frame guard refused: computed inclination {c.IncDeg:n3}°, " +
                    $"game reports {c.GameInclinationRad} (raw value, matches neither radians nor degrees)");
                return;
            }

            if (!capture && !AnyTarget)
            {
                _message = "Tick at least one target (Ap, Pe or Inclination) first.";
                return;
            }

            var targets = Targets();
            var plan = OrbitPlanner.Plan(c.R, c.V, c.UT, c.Body, targets);
            double vesselDv = VesselDeltaV();
            var budget = OrbitPlanner.CheckCaptureBudget(plan, vesselDv);
            if (plan.Capture != null)
                SkySpaceAgencyPlugin.logger?.LogInfo($"[OrbitUI] capture: {plan.Burns[0].Label} at {c.BodyName}, " +
                    $"{plan.Capture.DeltaV:n1} m/s (vessel {vesselDv:n1} m/s), then {plan.Burns.Count - 1} burn(s), " +
                    $"trajectory e {c.El.Eccentricity:n4} Pe {(c.El.PeriapsisRadius - c.Body.Radius) / 1000:n1} km, SOI {c.Body.SoiRadius / 1000:n0} km");
            else if (capture)
                SkySpaceAgencyPlugin.logger?.LogInfo($"[OrbitUI] capture refused: {plan.Error}, trajectory e {c.El.Eccentricity:n4} " +
                    $"Pe {(c.El.PeriapsisRadius - c.Body.Radius) / 1000:n1} km at {c.BodyName}");
            if (budget != OrbitPlanError.None)
            {
                _message = budget == OrbitPlanError.CaptureBeyondDeltaV
                    ? $"The capture at {c.BodyName} needs {plan.Capture.DeltaV:n0} m/s, the vessel has {vesselDv:n0} m/s. No node created."
                    : OrbitPlanner.Describe(plan.Error, c.BodyName);
                return;
            }
            if (plan.Burns.Count == 0)
            {
                _message = "Already on the target orbit: nothing to do.";
                return;
            }

            // Only the first burn gets a node (the game dropped the later nodes of a multi-node plan,
            // 2026-10-18); the Node pilot plans each next step from the orbit actually reached.
            var sequence = new OrbitPlanSequence(targets, c.Body, plan.Burns.Count);
            var first = sequence.Next(c.R, c.V, c.UT, c.Body);
            if (first.Outcome != OrbitPlanStepOutcome.Burn)
            {
                _message = $"Could not plan the first step ({first.Outcome}).";
                return;
            }

            _createdSummary = Summary(plan, c);
            _createdSteps = new List<string>();
            foreach (var b in plan.Burns) _createdSteps.Add(b.Label);
            SkySpaceAgencyPlugin.ResetControllers(); // CreateNodes wipes every node: stop any pilot flying one
            OrbitPlanRun.Start(sequence, $"Step 1/{plan.Burns.Count}: creating the node for '{first.Burn.Label}'...");
            var creator = _creator.Get();
            creator.Update();
            creator.CreateNodes(new[] { first.Burn }, count =>
            {
                if (count == 1)
                    OrbitPlanRun.Status = $"Step 1/{plan.Burns.Count}: node created for '{first.Burn.Label}'. " +
                        "Start flies the whole plan: each next step is planned from the orbit actually reached.";
                else
                    OrbitPlanRun.End($"Warning: the game holds {count} node(s), 1 expected. Plan cancelled.");
                _dirty = true;
            });
        }

        // ---------------------------------------------------------------- Sample

        public override void ShowSample()
        {
            _sample = true;
            OrbitViewModel.Sample(out var r, out var v, out var ut, out var plan);
            var body = OrbitViewModel.SampleBody;
            var model = OrbitViewModel.Build(body, r, v, ut, plan);
            // The mockup's texts on the real geometry.
            if (model.Markers.Count == 2)
            {
                model.Markers[0] = new OrbitViewModel.Marker { At = model.Markers[0].At, Label = "1 · raise Ap · 412 m/s" };
                model.Markers[1] = new OrbitViewModel.Marker { At = model.Markers[1].At, Label = "2 · circularize + plane · 690 m/s" };
            }
            model.CurrentText = "current 100 x 100 km, 0°";
            model.TargetText = "target 250 x 250 km, 45°";
            View.SetModel(model);
            ViewCard.Note = "top view, Kerbin";

            Source.SetWithoutNotify(0);
            ApField.Input.SetValueWithoutNotify(250);
            PeField.Input.SetValueWithoutNotify(250);
            IncField.Input.SetValueWithoutNotify(45);
            ApSwitch.SetValueWithoutNotify(true);
            PeSwitch.SetValueWithoutNotify(true);
            IncSwitch.SetValueWithoutNotify(true);

            ApTile.SetValue("100.2", "km");
            PeTile.SetValue("99.8", "km");
            IncTile.SetValue("0.1", "°");
            PeriodTile.SetValue("32:10");

            _rowsKey = "sample";
            PlanRows.ClearRows();
            TotalRows.ClearRows();
            PlanRows.Set("1  Raise Ap to 250 km", "412 m/s", StatusKind.Info);
            PlanRows.Set("2  Circularize, tilt to 45°", "690 m/s", StatusKind.Info);
            TotalRows.Set("Total Δv", "1 102 m/s", StatusKind.Ok);
            CaptureNote.Set("");
            Pill.Set(StatusKind.Ok, "Plan ready: 2 burns, 1 102 m/s");
            Pill.style.display = DisplayStyle.Flex;
            Console.Set("");

            SetPhases(new List<string> { "Plan", "Raise Ap", "Coast to Ap", "Circularize + plane", "Check orbit", "Done" }, 0);
            MainButton.Text = "Start";
            MainButton.SetEnabled(true);   // as the mockup: the sample shows a plan ready to fly
            PlanButton.SetEnabled(true);
        }
    }
}
