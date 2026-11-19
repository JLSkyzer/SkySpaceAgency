using System;
using System.Collections.Generic;
using System.Globalization;
using KTools;
using SkySpaceAgency.KSPService;
using SkySpaceAgency.OrbitPlanning;
using SkySpaceAgency.Transfer;
using SsaUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// TRANSFER (docs/design/ui/transfer.png): the classic Transfer tab (TransferUI) in the new window.
    /// Plan: the destination tiles, the arrival, the orbit at arrival, the inclination, the max trip,
    /// Prepare only and the gravity assist. View: the transfer map of the current plan (TransferView).
    /// Side: the assist choice lines, the Δv budget (ring, Δv per step, margin, per-stage check),
    /// warnings, the pilot status, and the plan's details, steps and stages. Timeline: the plan's
    /// steps, lit at the one flying. Same settings (TransferSettings) and the same pilot calls as the
    /// classic tab.
    /// </summary>
    public class TransferTab : AppTab
    {
        public const float RefreshSeconds = 0.5f;
        public const string BudgetOkText = "Every stage can pay its burns";
        public const string Tagline = "Every step is explained before it flies · resumes after save / load";

        public static readonly string[] IdlePhases = { "Wait for window", "Injection", "Leave SOI", "Correction", "Capture" };
        public static readonly string[] SamplePhases =
            { "Wait for window", "Ejection · 1 061 m/s", "Leave Kerbin SOI", "Mid-course correction", "Enter Duna SOI", "Capture · 317 m/s" };

        public TransferTab(AppTabContext context) : base(context) { }
        public override string Code => "transfer";
        public override string Label => "Transfer";

        TransferPilot Pilot => Context.Pilot<TransferPilot>();

        public override bool IsRunning => Pilot?.isRunning ?? false;
        public override HudState Hud => IsRunning ? _hud : null;

        // ---------------------------------------------------------------- elements

        readonly AppPilotStatus _status = new AppPilotStatus();
        readonly HudState _hud = new HudState { Pilot = "TRANSFER" };

        AppCard _dest, _mapCard;
        ScrollView _bodyScroll;
        VisualElement _bodies;
        AppNote _bodiesNote, _lowHint;
        readonly List<(string name, VisualElement tile)> _tiles = new();
        AppSegmented _arrival;
        AppSwitch _low, _incOn, _prepare, _assist;
        VisualElement _orbitFields;
        AppField _pe, _ap, _inc;
        AppTextField _maxTrip;

        TransferView _map;
        Label _factPhase, _factTrip, _factEncounter;

        VisualElement _choice;
        AppButton _chooseDirect, _chooseAssist;
        TransferBudgetRing _ring;
        AppInfoTable _rows, _left, _planInfo;
        AppPill _budgetPill;
        VisualElement _warnings, _stepsBox, _stagesBox;
        AppNote _stageNote;
        AppSection _details, _stepsSection, _stagesSection;
        readonly List<Label> _stepWhen = new();

        AppButton _cancelSaved, _resume, _plan, _main;

        // ---------------------------------------------------------------- state

        bool _sample;
        BodySystem _listSystem;
        List<BodyChoices.Entry> _entries = new();
        float _nextBodyTry, _nextRefresh;
        string _uiError, _shownTarget, _timelineKey, _rowsKey, _stagesKey, _warningsKey;
        TransferPlan _flownPlan, _estimatedPlan;
        List<string> _estimatedWarnings = new();
        BurndV _burnDv;

        // For the tests.
        public AppSegmented ArrivalControl => _arrival;
        public TransferView Map => _map;
        public TransferBudgetRing Ring => _ring;
        public AppInfoTable BudgetRows => _rows;
        public int BodyTiles => _tiles.Count;
        public AppButton MainButton => _main;
        public AppButton PlanButton => _plan;
        public AppPilotStatus Status => _status;

        // ---------------------------------------------------------------- build

        protected override void OnBuild(AppPage page)
        {
            BuildPlan(page.Plan);
            BuildView(page.View);
            BuildSide(page.Side);

            _cancelSaved = page.Actions.AddButton("Cancel saved plan", AppButton.Kind.Plain, OnCancelSaved);
            _resume = page.Actions.AddButton("Resume", AppButton.Kind.Plain, OnResume);
            _plan = page.Actions.AddButton("Plan", AppButton.Kind.Plain, OnPlan);
            _main = page.Actions.AddButton("Start", AppButton.Kind.Main, OnMain);
            page.Actions.SetNote(Tagline);
            page.SetTimeline(IdlePhases, 0);
            _timelineKey = Key(IdlePhases, 0);

            UpdateFields();
            TickIdle();
        }

        void BuildPlan(VisualElement col)
        {
            _dest = new AppCard("Destination", "choose a body");
            _bodyScroll = new ScrollView(ScrollViewMode.Vertical);
            _bodyScroll.AddToClassList("ssa-transfer-bodies");
            _bodyScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _bodies = Ui.Box("ssa-transfer-bodies__grid");
            _bodyScroll.Add(_bodies);
            _dest.Add(_bodyScroll);
            _bodiesNote = new AppNote();
            _dest.Add(_bodiesNote);
            // The target has no AppBind control (tiles): its key is recorded here, the tiles follow it in Tick.
            AppBind.Record(TransferSettings.target_body.key);
            TransferSettings.target_body.listeners -= OnTargetChanged;
            TransferSettings.target_body.listeners += OnTargetChanged;

            _dest.Add(Ui.Label("Arrival", "ssa-field__label", "ssa-transfer-label"));
            _arrival = AppBind.Segmented(TransferSettings.arrival,
                new[] { ArrivalKind.Orbit, ArrivalKind.Encounter }, new[] { "Capture into orbit", "Flyby" });
            _arrival.name = "arrival";
            _dest.Add(_arrival);

            _low = AppBind.Switch("Low circular orbit", TransferSettings.low_circular);
            _low.name = "low_circular";
            _dest.Add(_low);
            TransferSettings.low_circular.listeners -= OnLowCircular; // once per tab, even after a re-build
            TransferSettings.low_circular.listeners += OnLowCircular;
            _lowHint = new AppNote();
            _lowHint.AddToClassList("ssa-transfer-hint");
            _dest.Add(_lowHint);

            _orbitFields = Ui.Box("ssa-fields");
            _pe = AppBind.Field("Capture periapsis", TransferSettings.pe_km, "km", "0.#");
            _pe.name = "pe_km";
            _ap = AppBind.Field("Capture apoapsis", TransferSettings.ap_km, "km", "0.#");
            _ap.name = "ap_km";
            _ap.AddToClassList("ssa-field--last");
            _orbitFields.Add(_pe);
            _orbitFields.Add(_ap);
            _dest.Add(_orbitFields);

            _prepare = AppBind.Switch("Prepare only (I fly the nodes)", TransferSettings.prepare_only);
            _prepare.name = "prepare_only";
            _dest.Add(_prepare);

            // Less used choices, folded (the mockup's plan column has no room for them).
            var more = new AppSection("Options");
            more.name = "transfer-options";
            _assist = AppBind.Switch("Use gravity assist", TransferSettings.gravity_assist);
            _assist.name = "gravity_assist";
            more.Add(_assist);
            _incOn = AppBind.Switch("Set the arrival inclination", TransferSettings.inc_enabled);
            _incOn.name = "inc_enabled";
            more.Add(_incOn);
            var row = Ui.Box("ssa-fields");
            _inc = AppBind.Field("Inclination", TransferSettings.inc_deg, "°", "0.#");
            _inc.name = "inc_deg";
            _maxTrip = AppBind.Text("Max trip (days of 6 h)", TransferSettings.max_trip_days, "no limit");
            _maxTrip.name = "max_trip_days";
            _maxTrip.Input.isDelayed = true;
            _maxTrip.AddToClassList("ssa-field--last");
            _maxTrip.AddToClassList("ssa-transfer-maxtrip");
            row.Add(_inc);
            row.Add(_maxTrip);
            more.Add(row);
            _dest.Add(more);
            col.Add(_dest);
        }

        void BuildView(VisualElement col)
        {
            _mapCard = new AppCard("Transfer map", "not to scale");
            _mapCard.AddToClassList("ssa-transfer-mapcard");
            _map = new TransferView();
            _mapCard.Add(_map);
            var facts = Ui.Box("ssa-transfer-facts");
            _factPhase = Fact(facts, "Phase angle", false);
            _factTrip = Fact(facts, "Trip time", false);
            _factEncounter = Fact(facts, "Encounter", true);
            _mapCard.Add(facts);
            col.Add(_mapCard);
        }

        static Label Fact(VisualElement row, string title, bool ok)
        {
            var box = Ui.Box("ssa-transfer-fact");
            box.Add(Ui.Label(title.ToUpperInvariant(), "ssa-transfer-fact__label"));
            var v = Ui.Label("-", "ssa-transfer-fact__value");
            if (ok) v.AddToClassList("ssa-transfer-fact__value--ok");
            box.Add(v);
            row.Add(box);
            return v;
        }

        void BuildSide(VisualElement col)
        {
            var card = new AppCard("Δv budget");
            _choice = Ui.Box("ssa-transfer-choice");
            _chooseDirect = new AppButton("", AppButton.Kind.Plain, () => Choose(false));
            _chooseAssist = new AppButton("", AppButton.Kind.Plain, () => Choose(true));
            foreach (var b in new[] { _chooseDirect, _chooseAssist })
            {
                b.AddToClassList("ssa-button--small");
                b.AddToClassList("ssa-transfer-choice__line");
                _choice.Add(b);
            }
            _choice.style.display = DisplayStyle.None;
            card.Add(_choice);

            _ring = new TransferBudgetRing();
            card.Add(_ring);
            _rows = new AppInfoTable();
            _rows.AddToClassList("ssa-transfer-rows");
            card.Add(_rows);
            _left = new AppInfoTable();
            _left.AddToClassList("ssa-transfer-left");
            card.Add(_left);
            _budgetPill = new AppPill(StatusKind.Neutral, "");
            _budgetPill.style.display = DisplayStyle.None;
            card.Add(_budgetPill);
            _warnings = Ui.Box("ssa-transfer-warnings");
            card.Add(_warnings);

            card.Add(_status.Pill);
            card.Add(_status.Progress);
            card.Add(_status.Console);

            _details = new AppSection("Plan details");
            _planInfo = new AppInfoTable();
            _details.Add(_planInfo);
            card.Add(_details);
            _stepsSection = new AppSection("Steps");
            _stepsBox = Ui.Box("ssa-transfer-steps");
            _stepsSection.Add(_stepsBox);
            card.Add(_stepsSection);
            _stagesSection = new AppSection("Stages");
            _stagesBox = Ui.Box("ssa-transfer-stages");
            _stagesSection.Add(_stagesBox);
            _stageNote = new AppNote();
            _stageNote.AddToClassList("ssa-transfer-warning");
            _stagesSection.Add(_stageNote);
            card.Add(_stagesSection);
            col.Add(card);
            ClearPlan();
        }

        // ---------------------------------------------------------------- tick

        public override void Tick()
        {
            var pilot = Pilot;
            if (pilot == null)
            {
                if (!_sample)
                    TickIdle();
                return;
            }
            _sample = false;
            if (pilot.isRunning)
                _flownPlan = pilot.Current;
            UpdateBodies(pilot);
            UpdateFields();
            UpdateActions(pilot);
            UpdateStatus(pilot);
            UpdateTargetTiles();
            float now = Time.realtimeSinceStartup;
            if (now >= _nextRefresh || pilot.Current != _estimatedPlan)
            {
                _nextRefresh = now + RefreshSeconds;
                UpdatePlan(pilot);
                UpdateTimeline(pilot);
                UpdateHud(pilot);
            }
        }

        // Without the game (editor, before the campaign): the settings, no action.
        void TickIdle()
        {
            UpdateFields();
            UpdateTargetTiles();
            foreach (var b in new[] { _cancelSaved, _resume })
                b.style.display = DisplayStyle.None;
            _plan.SetEnabled(false);
            _main.SetEnabled(false);
            _bodiesNote.Set(_tiles.Count == 0 ? "The bodies show here in flight." : "");
            _status.Status.Reset();
            _status.Status.Status("Choose a target, then Plan.");
            _status.Refresh();
        }

        // ---------------------------------------------------------------- bodies

        void UpdateBodies(TransferPilot pilot)
        {
            // The pilot's latest snapshot when there is one; else a body list read from the game.
            BodySystem s = pilot.System;
            if (s == null && _listSystem == null && Time.realtimeSinceStartup >= _nextBodyTry)
            {
                _nextBodyTry = Time.realtimeSinceStartup + 2;
                try
                {
                    s = GameBodySystem.Build(GeneralTools.Current_UT, out _);
                }
                catch (Exception)
                {
                    s = null;
                }
            }
            if (s == null || s == _listSystem)
                return;
            _listSystem = s;
            _entries = BodyChoices.Targets(s);
            var tiles = new List<(string name, int depth)>();
            foreach (var e in _entries)
                tiles.Add((e.Name, e.Depth));
            RebuildTiles(tiles);
            _bodiesNote.Set(_tiles.Count == 0 ? "No body to aim at yet." : "");
        }

        void RebuildTiles(List<(string name, int depth)> bodies)
        {
            _bodies.Clear();
            _tiles.Clear();
            foreach (var (name, depth) in bodies)
            {
                string n = name;
                var tile = Ui.Box("ssa-transfer-body");
                tile.name = "body-" + n;
                var dot = Ui.Box("ssa-transfer-body__dot", depth <= 1 ? "ssa-transfer-body__dot--planet" : "ssa-transfer-body__dot--moon");
                dot.style.backgroundColor = TransferBodyColors.For(n);
                tile.Add(dot);
                tile.Add(Ui.Label(n, "ssa-transfer-body__label"));
                tile.AddManipulator(new Clickable(() =>
                {
                    if (_sample || !tile.enabledInHierarchy) return;
                    TransferSettings.target_body.V = n;
                }));
                _bodies.Add(tile);
                _tiles.Add((n, tile));
            }
            _shownTarget = null;
            UpdateTargetTiles();
        }

        void OnTargetChanged(string _) => _shownTarget = null; // the tiles follow at the next Tick

        void UpdateTargetTiles()
        {
            if (_sample)
                return;
            string target = TransferSettings.target_body.V ?? "";
            if (target == _shownTarget)
                return;
            _shownTarget = target;
            ShowSelected(target);
        }

        void ShowSelected(string target)
        {
            foreach (var (name, tile) in _tiles)
                tile.EnableInClassList("ssa-transfer-body--selected", name == target);
            _dest.Note = string.IsNullOrEmpty(target) ? "choose a body" : target;
        }

        bool TryTarget(out BodyNode body)
        {
            body = null;
            int i = BodyChoices.FindName(_entries, TransferSettings.target_body.V);
            if (i < 0 || _listSystem == null)
                return false;
            body = _listSystem[_entries[i].Index];
            return true;
        }

        // ---------------------------------------------------------------- controls

        void UpdateFields()
        {
            bool low = TransferSettings.low_circular.V;
            bool orbit = TransferSettings.arrival.V == ArrivalKind.Orbit;
            _orbitFields.style.display = low ? DisplayStyle.None : DisplayStyle.Flex;
            _ap.style.display = orbit ? DisplayStyle.Flex : DisplayStyle.None;
            _pe.EnableInClassList("ssa-field--last", !orbit);
            SetFieldLabel(_pe, orbit ? "Capture periapsis" : "Flyby periapsis");
            _inc.SetEnabled(TransferSettings.inc_enabled.V);
            _lowHint.Set(TransferTabLogic.LowOrbitHint(TryTarget(out var body) ? body : null, low, orbit));
        }

        static void SetFieldLabel(VisualElement field, string text)
        {
            var l = field.Q<Label>(className: "ssa-field__label");
            if (l != null && l.text != text) l.text = text;
        }

        void UpdateActions(TransferPilot pilot)
        {
            bool running = pilot.isRunning, searching = pilot.Searching;
            _plan.SetEnabled(!running);
            SetText(_plan, searching ? "Cancel search" : pilot.Current != null && pilot.Current.Ok ? "Re-plan" : "Plan");
            SetText(_main, running ? "Stop" : "Start");
            _main.SetEnabled(running || pilot.CanStart);
            _resume.style.display = pilot.HasSavedPlan ? DisplayStyle.Flex : DisplayStyle.None;
            _resume.SetEnabled(pilot.CanResume);
            _cancelSaved.style.display = pilot.HasSavedPlan || running ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var (_, tile) in _tiles)
                tile.SetEnabled(!running);
        }

        static void SetText(AppButton b, string text)
        {
            if (b.Text != text) b.Text = text;
        }

        // Unticking Low circular starts the fields from the target's low orbit (as the classic tab).
        void OnLowCircular(bool on)
        {
            if (!on && TryTarget(out var body))
            {
                float km = (float)Math.Round((body.LowOrbitRadius - body.Radius) / 1000, 1);
                TransferSettings.pe_km.V = km;
                TransferSettings.ap_km.V = km;
            }
        }

        // The target goes by name and the orbit as altitudes: the pilot resolves both against its own snapshot.
        void OnPlan()
        {
            var pilot = Pilot;
            if (pilot == null)
                return;
            _uiError = null;
            if (pilot.Searching)
            {
                pilot.CancelSearch();
                return;
            }
            if (!TransferReadout.TryInput(TransferSettings.target_body.V, TransferSettings.arrival.V, TransferSettings.low_circular.V,
                    TransferSettings.pe_km.V, TransferSettings.ap_km.V, TransferSettings.inc_enabled.V, TransferSettings.inc_deg.V,
                    TransferSettings.max_trip_days.V, out var input, out string error))
            {
                _uiError = error;
                return;
            }
            input.UseGravityAssist = TransferSettings.gravity_assist.V;
            pilot.Plan(input);
            _nextRefresh = 0;
        }

        void OnMain()
        {
            var pilot = Pilot;
            if (pilot == null)
                return;
            _uiError = null;
            if (pilot.isRunning)
                pilot.Stop();
            else
                pilot.Start(TransferSettings.prepare_only.V);
            _nextRefresh = 0;
        }

        void OnResume()
        {
            var pilot = Pilot;
            if (pilot == null) return;
            _uiError = null;
            pilot.Resume();
            _nextRefresh = 0;
        }

        void OnCancelSaved()
        {
            var pilot = Pilot;
            if (pilot == null) return;
            _uiError = null;
            pilot.CancelSaved();
            _nextRefresh = 0;
        }

        void Choose(bool assist)
        {
            var pilot = Pilot;
            if (pilot == null || _sample) return;
            _uiError = null;
            pilot.ChoosePlan(assist);
            _nextRefresh = 0;
        }

        // ---------------------------------------------------------------- status (classic UpdateStatus)

        void UpdateStatus(TransferPilot pilot)
        {
            var bar = _status.Status;
            bar.Reset();
            if (!string.IsNullOrEmpty(_uiError))
                bar.Warning(_uiError);
            else if (!string.IsNullOrEmpty(pilot.Status))
                bar.Status(pilot.Status, pilot.Phase == TransferPhase.Failed || pilot.Status.StartsWith("Refused")
                    ? StatusLine.Level.Warning : StatusLine.Level.Normal);
            else
                bar.Status("Choose a target, then Plan.");
            if (pilot.Searching)
                bar.Progress(pilot.SearchProgress, "Searching");
            _status.Refresh();

            string offer = pilot.isRunning ? null : pilot.ResumeOffer;
            Page.Actions.SetNote(!string.IsNullOrEmpty(offer) ? offer : pilot.isRunning ? _nextText : Tagline);
        }

        string _nextText = "";

        // ---------------------------------------------------------------- plan

        void ClearPlan()
        {
            _ring.Set("-", "no plan yet", 0, false);
            _rows.ClearRows();
            _left.ClearRows();
            _planInfo.ClearRows();
            _stepsBox.Clear();
            _stepWhen.Clear();
            _stagesBox.Clear();
            _stageNote.Set("");
            _budgetPill.style.display = DisplayStyle.None;
            _choice.style.display = DisplayStyle.None;
            foreach (var s in new[] { _details, _stepsSection, _stagesSection })
                s.style.display = DisplayStyle.None;
            _rowsKey = _stagesKey = null;
            if (_map.Model != null) _map.SetModel(null);
            _factPhase.text = _factTrip.text = _factEncounter.text = "-";
        }

        void UpdatePlan(TransferPilot pilot)
        {
            var plan = pilot.Current;
            if (plan != _estimatedPlan)
            {
                _estimatedPlan = plan;
                _estimatedWarnings = plan != null && plan.Ok && !pilot.isRunning
                    ? TransferReadout.LongBurnWarnings(plan, pilot.System, EstimateBurnSeconds)
                    : new List<string>();
            }
            var warnings = new List<string>();
            if (plan != null && plan.Ok)
            {
                warnings.AddRange(plan.Warnings);
                foreach (string w in _estimatedWarnings)
                    warnings.Add("Estimate: " + w);
            }
            SetWarnings(warnings);

            if (plan == null || !plan.Ok)
            {
                if (_rowsKey != null || _map.Model != null) ClearPlan();
                _nextText = "";
                return;
            }
            UpdateAssistChoice(pilot, plan);
            double now = GeneralTools.Current_UT;
            var system = pilot.System;
            bool flown = plan == _flownPlan;
            int from = flown ? pilot.StepIndex : 0;

            foreach (var s in new[] { _details, _stepsSection, _stagesSection })
                s.style.display = DisplayStyle.Flex;
            _planInfo.Set("Case", plan.CaseName);
            _planInfo.Set("Departure", TransferTabLogic.AfterColon(TransferReadout.Departure(plan, now)));
            _planInfo.Set("Trip", TransferTabLogic.AfterColon(TransferReadout.Trip(plan)));

            // Steps: the budget rows and the steps section are rebuilt when the plan or its next step change.
            var steps = TransferReadout.Steps(plan, system, now, from);
            string key = TransferTabLogic.PlanKey(plan) + "#" + from;
            if (key != _rowsKey)
            {
                _rowsKey = key;
                _rows.ClearRows();
                foreach (var (label, value, done) in TransferTabLogic.BudgetRows(steps, plan.ReserveDeltaV))
                    _rows.Set(label, value, done ? StatusKind.Neutral : StatusKind.Info);
                _stepsBox.Clear();
                _stepWhen.Clear();
                foreach (var row in steps)
                    _stepsBox.Add(StepElement(row));
            }
            for (int i = 0; i < steps.Count && i < _stepWhen.Count; i++)
                if (_stepWhen[i].text != steps[i].When) _stepWhen[i].text = steps[i].When;
            _nextText = TransferTabLogic.NextLine(steps);

            var budget = TransferFlight.RemainingBudget(plan, from, pilot.Stages);
            _planInfo.Set("Total", TransferReadout.Totals(plan, budget));
            _ring.Set(TransferTabLogic.Ms(budget.Needed), "of " + TransferTabLogic.Ms(budget.Available) + " m/s",
                budget.Available > 0 ? (float)(budget.Needed / budget.Available) : 1, budget.Available < budget.Needed);
            double margin = budget.Available - budget.Needed;
            _left.Set(margin >= 0 ? "Left after arrival" : "Short by", TransferTabLogic.Ms(Math.Abs(margin)) + " m/s",
                margin >= 0 ? StatusKind.Ok : StatusKind.Alert);
            _left.Remove(margin >= 0 ? "Short by" : "Left after arrival");
            _budgetPill.style.display = DisplayStyle.Flex;
            _budgetPill.Set(budget.Ok ? StatusKind.Ok : StatusKind.Alert, budget.Ok ? BudgetOkText : budget.Refusal ?? "The stages cannot pay every burn.");

            var stageRows = TransferReadout.Stages(plan, from, pilot.Stages, budget, NextStage(), out string note);
            var texts = new List<string>();
            foreach (var r in stageRows) texts.Add((r.ByHand ? "!" : "") + r.Text);
            string stagesKey = string.Join("\n", texts) + "\n" + note;
            if (stagesKey != _stagesKey)
            {
                _stagesKey = stagesKey;
                _stagesBox.Clear();
                foreach (var r in stageRows)
                {
                    var l = Ui.Label(r.Text, "ssa-transfer-stage");
                    if (r.ByHand) l.AddToClassList("ssa-transfer-stage--hand");
                    _stagesBox.Add(l);
                }
                _stageNote.Set(note);
            }

            double fraction = flown && pilot.isRunning ? TransferTabLogic.FlownFraction(plan, now) : -1;
            var model = TransferViewModel.Build(system, plan, now, fraction);
            _map.SetModel(model);
            _mapCard.Note = model != null ? model.CentreName + " system · not to scale" : "not to scale";
            _factPhase.text = model?.PhaseText ?? "-";
            _factTrip.text = model?.TripText ?? KerbinCalendar.Duration(plan.TripSeconds);
            _factEncounter.text = model?.EncounterText ?? "-";
        }

        // The two plans of a search with gravity assists (4.6): "Direct" and the offered assist, the
        // shown one marked; with no assist offered, the direct line and why, disabled (as the classic tab).
        void UpdateAssistChoice(TransferPilot pilot, TransferPlan shown)
        {
            var choice = pilot.Choice;
            bool mine = choice != null && (shown == choice.Direct || shown == choice.Assist);
            _choice.style.display = mine ? DisplayStyle.Flex : DisplayStyle.None;
            if (!mine)
                return;
            bool idle = !pilot.isRunning && !pilot.Searching;
            var system = pilot.System;
            string direct = TransferReadout.AssistLine(TransferReadout.AssistLabel(choice.Direct, system), choice.Direct, shown == choice.Direct);
            _chooseDirect.EnableInClassList("ssa-transfer-choice__line--active", shown == choice.Direct);
            _chooseAssist.EnableInClassList("ssa-transfer-choice__line--active", shown == choice.Assist);
            if (choice.Offered)
            {
                _chooseAssist.style.display = DisplayStyle.Flex;
                SetText(_chooseAssist, TransferReadout.AssistLine(TransferReadout.AssistLabel(choice.Assist, system), choice.Assist, shown == choice.Assist));
                SetText(_chooseDirect, direct);
                _chooseDirect.SetEnabled(idle && choice.Direct != null && choice.Direct.Ok);
                _chooseAssist.SetEnabled(idle);
            }
            else
            {
                _chooseAssist.style.display = DisplayStyle.None;
                SetText(_chooseDirect, direct + " — " + choice.NotOffered);
                _chooseDirect.SetEnabled(false);
            }
        }

        VisualElement StepElement(TransferReadout.StepRow row)
        {
            var block = Ui.Box("ssa-transfer-step");
            if (row.Done) block.AddToClassList("ssa-transfer-step--done");
            var line = Ui.Box("ssa-row");
            var kind = Ui.Label(row.Kind, "ssa-transfer-step__kind");
            kind.AddToClassList("ssa-grow");
            line.Add(kind);
            line.Add(Ui.Label(row.DeltaV, "ssa-transfer-step__dv"));
            block.Add(line);
            var when = Ui.Label(row.When, "ssa-transfer-step__when");
            _stepWhen.Add(when);
            block.Add(when);
            if (!string.IsNullOrEmpty(row.Explanation))
                block.Add(Ui.Label(row.Explanation, "ssa-transfer-step__why"));
            return block;
        }

        void SetWarnings(List<string> warnings)
        {
            string key = string.Join("\n", warnings);
            if (key == _warningsKey)
                return;
            _warningsKey = key;
            _warnings.Clear();
            foreach (string w in warnings)
            {
                var n = new AppNote("Warning: " + w);
                n.AddToClassList("ssa-transfer-warning");
                _warnings.Add(n);
            }
        }

        // ---------------------------------------------------------------- timeline and flight bar

        void UpdateTimeline(TransferPilot pilot)
        {
            var plan = pilot.isRunning && _flownPlan != null ? _flownPlan : pilot.Current;
            IReadOnlyList<string> steps;
            int current;
            if (plan != null && plan.Ok && plan.Steps.Count > 0)
            {
                steps = TransferTabLogic.Timeline(plan, pilot.System);
                bool flown = plan == _flownPlan;
                current = TransferTabLogic.TimelineIndex(pilot.isRunning && flown, flown ? pilot.Phase : TransferPhase.Planned,
                    flown ? pilot.StepIndex : 0, plan.Steps.Count);
            }
            else
            {
                steps = IdlePhases;
                current = 0;
            }
            string key = Key(steps, current);
            if (key == _timelineKey)
                return;
            _timelineKey = key;
            Page.SetTimeline(steps, current);
        }

        static string Key(IReadOnlyList<string> steps, int current) => string.Join("|", steps) + "#" + current;

        void UpdateHud(TransferPilot pilot)
        {
            if (!pilot.isRunning)
                return;
            var model = Page.Timeline.Model;
            _hud.Phases = model.Steps;
            _hud.CurrentPhase = model.Current;
            _hud.Phase = model.Steps.Count > 0 ? model.Steps[model.Current] : "";
            _hud.Stop = () => Pilot?.Stop();
            _hud.Readouts.Clear();
            var plan = pilot.Current;
            var s = pilot.System;
            if (plan?.Request != null && s != null && plan.Request.ToBody >= 0 && plan.Request.ToBody < s.Count)
                _hud.Readouts.Add(("Target", s[plan.Request.ToBody].Name));
            if (!string.IsNullOrEmpty(_nextText))
                _hud.Readouts.Add(("Next", _nextText.StartsWith("Next: ") ? _nextText.Substring(6) : _nextText));
            string left = _left.Get("Left after arrival");
            if (left != null)
                _hud.Readouts.Add(("Δv left", left));
            _hud.Alert = null;
            _hud.AlertDetail = null;
        }

        // ---------------------------------------------------------------- game reads (as the classic tab)

        // The next stage the game would fire (above it: staged already), null when unknown.
        static int? NextStage()
        {
            try
            {
                var staging = SkySpaceAgencyPlugin.Instance?.current_vessel?.VesselComponent?.SimulationObject?.Staging;
                if (staging == null)
                    return null;
                int count = staging.StageCount;
                bool lastActive = count > 0 && staging.AvailableStages[count - 1].IsActive;
                return SkySpaceAgency.Landing.Precision.JettisonPlan.NextStage(count, lastActive);
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
                _burnDv ??= new BurndV();
                _burnDv.Compute_Thrust();
                double thrust = _burnDv.active_thrust;
                if (thrust <= 0)
                    thrust = _burnDv.full_thrust.magnitude;
                return BurnTiming.Duration(dv, thrust, _burnDv.mass, _burnDv.active_isp);
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        // ---------------------------------------------------------------- sample (transfer.png)

        public override void ShowSample()
        {
            _sample = true;
            RebuildTiles(new List<(string, int)> { ("Mun", 2), ("Minmus", 2), ("Duna", 1), ("Eve", 1), ("Moho", 1), ("Jool", 1) });
            ShowSelected("Duna");
            _bodiesNote.Set("");
            _arrival.SetWithoutNotify(0);
            _orbitFields.style.display = DisplayStyle.Flex;
            _ap.style.display = DisplayStyle.None;
            SetFieldLabel(_pe, "Capture periapsis");
            _pe.Input.SetValueWithoutNotify(60);
            _lowHint.Set("");
            _low.SetValueWithoutNotify(false);
            _prepare.SetValueWithoutNotify(false);

            _map.SetModel(TransferViewModel.Sample());
            _mapCard.Note = "Kerbol system · not to scale";
            var m = _map.Model;
            _factPhase.text = m.PhaseText;
            _factTrip.text = m.TripText;
            _factEncounter.text = m.EncounterText;

            _ring.Set("1 398", "of 2 070 m/s", 1398f / 2070f, false);
            _rows.ClearRows();
            _rows.Set("Ejection", "1 061 m/s", StatusKind.Info);
            _rows.Set("Corrections (est.)", "20 m/s", StatusKind.Info);
            _rows.Set("Capture", "317 m/s", StatusKind.Info);
            _left.ClearRows();
            _left.Set("Left after arrival", "672 m/s", StatusKind.Ok);
            _budgetPill.style.display = DisplayStyle.Flex;
            _budgetPill.Set(StatusKind.Ok, BudgetOkText);
            _status.Pill.style.display = DisplayStyle.None;
            _status.Progress.style.display = DisplayStyle.None;
            _status.Console.Set("");

            Page.SetTimeline(SamplePhases, 0);
            _timelineKey = Key(SamplePhases, 0);
            Page.Actions.SetNote(Tagline);
            _cancelSaved.style.display = DisplayStyle.None;
            _resume.style.display = DisplayStyle.None;
            _plan.Text = "Re-plan";
            _main.Text = "Start";
            _plan.SetEnabled(true);
            _main.SetEnabled(true);
        }
    }

    /// <summary>The Transfer tab's pure texts and decisions (tested in the editor).</summary>
    public static class TransferTabLogic
    {
        static readonly NumberFormatInfo Spaced = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "." };

        /// <summary>"1 398" (m/s, rounded, grouped with spaces as in the mockups).</summary>
        public static string Ms(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "-" : Math.Round(v).ToString("#,0", Spaced);

        /// <summary>"Departure: Y1, D3, …" → "Y1, D3, …".</summary>
        public static string AfterColon(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int i = text.IndexOf(": ", StringComparison.Ordinal);
            return i >= 0 ? text.Substring(i + 2) : text;
        }

        /// <summary>The classic tab's hint under Low circular.</summary>
        public static string LowOrbitHint(BodyNode body, bool low, bool orbit)
        {
            if (body == null)
                return "Choose a target body.";
            double km = (body.LowOrbitRadius - body.Radius) / 1000;
            string kmText = km.ToString("F0", CultureInfo.InvariantCulture);
            return low
                ? $"{(orbit ? "Capture into" : "Fly past at")} {kmText} km above {body.Name}: 10 km above its " +
                  (body.AtmosphereDepth >= body.MaxTerrainHeight ? "atmosphere." : "highest terrain.")
                : $"Lowest safe altitude at {body.Name}: {kmText} km.";
        }

        /// <summary>Identifies a plan for the rebuild checks (same object, same steps).</summary>
        public static string PlanKey(TransferPlan plan) =>
            plan == null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(plan) + ":" + plan.Steps.Count;

        /// <summary>The budget rows: one per step ("Injection burn (Kerbin)", "1 061.0 m/s"), then the reserve.</summary>
        public static List<(string label, string value, bool done)> BudgetRows(IReadOnlyList<TransferReadout.StepRow> steps, double reserve)
        {
            var rows = new List<(string, string, bool)>();
            var seen = new HashSet<string>();
            for (int i = 0; i < steps.Count; i++)
            {
                string label = steps[i].Kind ?? "";
                if (!seen.Add(label))
                {
                    int n = 2;
                    while (!seen.Add(label + " " + n)) n++;
                    label = label + " " + n;
                }
                rows.Add((label, steps[i].Done ? steps[i].DeltaV + " · done" : steps[i].DeltaV, steps[i].Done));
            }
            rows.Add(("Reserve", Ms(reserve) + " m/s", false));
            return rows;
        }

        /// <summary>"Next: injection burn (Kerbin), Y1, D3, 2h 00m, in 4d 2h" from the first step not flown; "" when all are.</summary>
        public static string NextLine(IReadOnlyList<TransferReadout.StepRow> steps)
        {
            foreach (var s in steps)
                if (!s.Done)
                    return "Next: " + s.Kind + " · " + s.When;
            return "";
        }

        static string Capitalize(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        /// <summary>A timeline label for step i: "Injection · 1 061 m/s", "Leave Kerbin SOI", "Flyby Eve", "Mid-course correction".</summary>
        public static string StepLabel(TransferPlan plan, int i, BodySystem s)
        {
            var st = plan.Steps[i];
            string body = s != null && st.Body >= 0 && st.Body < s.Count ? s[st.Body].Name : "";
            string label;
            switch (st.Kind)
            {
                case StepKind.Injection: label = "Injection"; break;
                case StepKind.PlaneChange: label = "Plane change"; break;
                case StepKind.OrbitChange: label = "Orbit change"; break;
                case StepKind.Escape: return string.IsNullOrEmpty(body) ? "Leave SOI" : $"Leave {body} SOI";
                case StepKind.Capture: label = "Capture"; break;
                case StepKind.Flyby: label = string.IsNullOrEmpty(body) ? "Flyby" : "Flyby " + body; break;
                default: label = Capitalize(TransferFlight.StepName(plan, i)); break;
            }
            bool burn = TransferFlight.IsBurn(st) && st.DeltaVMagnitude >= 0.5
                && !(st.Kind == StepKind.Correction && !(st.DeltaV.sqrMagnitude > 0));
            return burn ? label + " · " + Ms(st.DeltaVMagnitude) + " m/s" : label;
        }

        /// <summary>"Wait for window", then one label per step of the plan.</summary>
        public static List<string> Timeline(TransferPlan plan, BodySystem s)
        {
            var steps = new List<string> { "Wait for window" };
            for (int i = 0; i < plan.Steps.Count; i++)
                steps.Add(StepLabel(plan, i, s));
            return steps;
        }

        /// <summary>
        /// The lit timeline entry: 0 (wait for window) until the first burn, then 1 + the step flying
        /// or coasted to; the last one when the transfer is done.
        /// </summary>
        public static int TimelineIndex(bool running, TransferPhase phase, int stepIndex, int stepCount)
        {
            if (phase == TransferPhase.Done)
                return stepCount;
            if (!running)
                return 0;
            if ((phase == TransferPhase.WaitWindow || phase == TransferPhase.Planned) && stepIndex <= 0)
                return 0;
            return Mathf.Clamp(1 + stepIndex, 0, stepCount);
        }

        /// <summary>How far along the trip the vessel is (0 at the departure, 1 at the arrival), -1 before the departure.</summary>
        public static double FlownFraction(TransferPlan plan, double now)
        {
            if (plan == null || double.IsNaN(plan.DepartureUT) || double.IsNaN(plan.ArrivalUT) || now < plan.DepartureUT)
                return -1;
            double trip = plan.ArrivalUT - plan.DepartureUT;
            return trip > 0 ? Math.Min(1, (now - plan.DepartureUT) / trip) : 1;
        }
    }
}
