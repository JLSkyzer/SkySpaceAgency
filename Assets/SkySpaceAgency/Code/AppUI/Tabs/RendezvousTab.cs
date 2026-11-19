using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Sim.impl;
using KTools;
using SkySpaceAgency.Rendezvous;
using SkySpaceAgency.Transfer;
using SsaUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// RENDEZVOUS (docs/design/ui/rendezvous.png), with the classic Dock tab's docking: the target
    /// list, the mode (Rendezvous / + Dock / Dock only), the port pair, Prepare only, Start / Abort /
    /// Resume / Cancel saved plan, the 4.5 chain for a target around another body (the Transfer
    /// pilot), the plan and the stages; the APPROACH view, the RELATIVE tiles, the CHECKS and the
    /// pilot's status. Same calls as the classic page (RendezvousUI); RendezvousSettings is the same.
    /// </summary>
    public class RendezvousTab : AppTab
    {
        public RendezvousTab(AppTabContext context) : base(context) { }
        public override string Code => "rendezvous";
        public override string Label => "Rendezvous";

        const float RefreshSeconds = 0.5f;
        const float ListSeconds = 2f;
        public const string IdleNote = "A vessel switch stops the pilot; our throttle and RCS are cut";
        public const string NoPilotStatus = "The Rendezvous pilot loads with the campaign, in flight.";

        static readonly RvMode[] Modes = { RvMode.RendezvousOnly, RvMode.RendezvousAndDock, RvMode.DockOnly };
        static readonly string[] ModeLabels = { "Rendezvous", "+ Dock", "Dock only" };

        RendezvousPilot Pilot => Context.Pilot<RendezvousPilot>();
        TransferPilot Transfer => Context.Pilot<TransferPilot>();

        // ---- controls
        RendezvousTargetList _targets;
        AppSegmented _mode;
        Label _portOurs, _portTheirs, _portChange;
        VisualElement _portPicker;
        AppDropdown _ourDrop, _theirDrop;
        AppNote _portHint, _translationNote, _relativeNote, _dockLine, _totals, _offer;
        AppSwitch _prepare;
        AppSection _planSection, _stagesSection;
        VisualElement _steps, _stagesBox;
        AppCard _viewCard;
        ApproachView _view;
        AppTile _distTile, _closingTile, _lateralTile, _rcsTile;
        RendezvousCheckList _checks;
        readonly AppPilotStatus _status = new AppPilotStatus();
        AppButton _cancel, _resume, _abort, _main;

        // ---- state (as RendezvousUI)
        List<RendezvousReadout.Choice> _choices = new();
        List<RendezvousReadout.Choice> _ourPorts = new() { new RendezvousReadout.Choice { Label = RendezvousReadout.AutoLabel } };
        List<RendezvousReadout.Choice> _theirPorts = new() { new RendezvousReadout.Choice { Label = RendezvousReadout.AutoLabel } };
        readonly List<RendezvousTargetList.Item> _items = new();
        string _selected, _lastGameTarget, _portsFor, _ourPick, _theirPick;
        double _distance = double.NaN, _relSpeed = double.NaN, _relInc = double.NaN, _closing = double.NaN, _angle;
        bool _far, _portsOk, _sample, _noPilotShown, _planShown;
        int _rcsTotal = -1, _rcsUsable;
        float _rcsLeft = float.NaN;
        List<StageDv> _liveStages = new();
        string _uiError, _budgetLine, _planKey, _stagesKey, _timelineKey;
        bool _budgetOk = true;
        float _nextRefresh, _nextLists;
        int _lastIndex;
        IReadOnlyList<string> _steps_ = RendezvousTabPhases.Full;
        readonly HudState _hud = new HudState { Pilot = "RENDEZVOUS" };

        public RvMode SelectedMode => Modes[Mathf.Clamp(_mode?.Selected ?? 1, 0, Modes.Length - 1)];
        public ApproachView View => _view;
        public RendezvousTargetList Targets => _targets;
        public RendezvousCheckList Checks => _checks;
        public AppPilotStatus Status => _status;
        public AppTile DistanceTile => _distTile;
        public AppTile RcsTile => _rcsTile;
        public AppButton MainButton => _main;

        // ================================================================ build

        protected override void OnBuild(AppPage page)
        {
            // ---- TARGET
            var target = new AppCard("Target");
            _targets = new RendezvousTargetList();
            _targets.picked += OnTargetPicked;
            AppBind.Record(RendezvousSettings.target_id.key);   // the list writes it (the classic drop-down did)
            target.Add(_targets);

            _mode = new AppSegmented(ModeLabels, 1);
            _mode.AddToClassList("ssa-rendezvous-mode");
            _mode.changed += _ => { _uiError = null; _nextRefresh = 0; };
            target.Add(_mode);

            target.Add(Ui.Label("Docking ports", "ssa-rendezvous-label"));
            var ports = Ui.Box("ssa-rendezvous-ports");
            var names = Ui.Box("ssa-rendezvous-ports__names");
            _portOurs = Ui.Label("Ours: Auto", "ssa-rendezvous-ports__line");
            _portTheirs = Ui.Label("Theirs: Auto", "ssa-rendezvous-ports__line");
            names.Add(_portOurs);
            names.Add(_portTheirs);
            ports.Add(names);
            _portChange = Ui.Label("Change", "ssa-rendezvous-ports__change");
            _portChange.AddManipulator(new Clickable(() => { if (_portChange.enabledInHierarchy) TogglePicker(); }));
            ports.Add(_portChange);
            target.Add(ports);

            _portPicker = Ui.Box("ssa-rendezvous-ports__picker");
            _ourDrop = new AppDropdown("Ours", RendezvousReadout.Labels(_ourPorts), 0);
            _ourDrop.changed += i => { _ourPick = i >= 0 && i < _ourPorts.Count ? _ourPorts[i].Id : null; PortSummary(); };
            _theirDrop = new AppDropdown("Target's", RendezvousReadout.Labels(_theirPorts), 0);
            _theirDrop.changed += i => { _theirPick = i >= 0 && i < _theirPorts.Count ? _theirPorts[i].Id : null; PortSummary(); };
            _ourDrop.Input.tooltip = _theirDrop.Input.tooltip = "Auto: the free compatible pair that needs the least rotation";
            _portPicker.Add(_ourDrop);
            _portPicker.Add(_theirDrop);
            _portPicker.style.display = DisplayStyle.None;
            target.Add(_portPicker);
            _portHint = new AppNote();
            _portHint.Set("");
            target.Add(_portHint);

            _prepare = AppBind.Switch("Prepare only", RendezvousSettings.prepare_only);
            _prepare.tooltip = "Make one game node at a time for the rendezvous burns; no warp. The docking is always flown";
            target.Add(_prepare);
            _translationNote = new AppNote("RCS for the approach, the main engine if there is no RCS: chosen by a thruster probe when the docking starts.");
            target.Add(_translationNote);

            _planSection = new AppSection("Plan");
            _steps = Ui.Box("ssa-rendezvous-steps");
            _planSection.Add(_steps);
            _totals = new AppNote();
            _totals.Set("");
            _planSection.Add(_totals);
            _planSection.style.display = DisplayStyle.None;
            target.Add(_planSection);
            _stagesSection = new AppSection("Stages · Δv");
            _stagesBox = Ui.Box("ssa-rendezvous-stages");
            _stagesSection.Add(_stagesBox);
            target.Add(_stagesSection);
            page.Plan.Add(target);

            // ---- APPROACH
            _viewCard = new AppCard("Approach", "target frame, orbit axes", sky: true);
            _view = new ApproachView();
            _viewCard.Add(_view);
            page.View.Add(_viewCard);

            // ---- RELATIVE
            var side = new AppCard("Relative");
            var tiles = Ui.Box("ssa-tiles");
            _distTile = new AppTile("Distance", "-", "m");
            _closingTile = new AppTile("Closing", "-", "m/s");
            _lateralTile = new AppTile("Lateral err", "-", "m");
            _rcsTile = new AppTile("RCS left", "-", "%", AppTile.Kind.Ok);
            tiles.Add(_distTile);
            tiles.Add(_closingTile);
            tiles.Add(_lateralTile);
            tiles.Add(_rcsTile);
            side.Add(tiles);
            _relativeNote = new AppNote();
            _relativeNote.Set("");
            side.Add(_relativeNote);
            _dockLine = new AppNote();
            _dockLine.Set("");
            side.Add(_dockLine);
            side.Add(Ui.Label("CHECKS", "ssa-section-title"));
            _checks = new RendezvousCheckList();
            _checks.Set(RendezvousChecks.Build(false, false, null, false, -1, 0, null, true, null, null));
            side.Add(_checks);
            side.Add(Ui.Box("ssa-grow"));
            _offer = new AppNote();
            _offer.AddToClassList("ssa-rendezvous-offer");
            _offer.Set("");
            side.Add(_offer);
            side.Add(_status.Progress);
            side.Add(_status.Console);
            side.Add(_status.Pill);
            page.Side.Add(side);

            // ---- timeline, actions
            page.SetTimeline(_steps_, 0);
            page.Actions.SetNote(IdleNote);
            _cancel = page.Actions.AddButton("Cancel saved plan", AppButton.Kind.Plain, OnCancelSaved);
            _resume = page.Actions.AddButton("Resume", AppButton.Kind.Plain, OnResume);
            _abort = page.Actions.AddButton("Abort", AppButton.Kind.Abort, OnStop);
            _main = page.Actions.AddButton("Start", AppButton.Kind.Main, OnMain);
            Show(_cancel, false);
            Show(_resume, false);
            _abort.SetEnabled(false);
            _main.SetEnabled(false);
        }

        void TogglePicker()
        {
            bool open = _portPicker.style.display == DisplayStyle.None;
            _portPicker.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            _portChange.text = open ? "Done" : "Change";
        }

        static void Show(VisualElement e, bool shown)
        {
            var d = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (e.style.display != d) e.style.display = d;
        }

        static void Enable(VisualElement e, bool on)
        {
            if (e.enabledSelf != on) e.SetEnabled(on);
        }

        static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);

        // ================================================================ actions (as RendezvousUI)

        bool ChainLeg()
        {
            var t = Transfer;
            return t != null && t.ChainStatusShown && (t.isRunning || t.Searching);
        }

        bool ChainResume()
        {
            var p = Pilot;
            var t = Transfer;
            return p != null && !p.HasSavedPlan && t != null && t.SavedIsChain && !t.isRunning;
        }

        bool ShowsChain()
        {
            var p = Pilot;
            var t = Transfer;
            return p != null && t != null && t.ChainStatusShown && !p.isRunning && !p.Planning;
        }

        bool Busy()
        {
            var p = Pilot;
            return p != null && (p.isRunning || p.Planning || ChainLeg());
        }

        void OnMain()
        {
            if (Pilot == null || Busy()) return;
            if (_far) StartChain(SelectedMode);
            else StartPilot(SelectedMode);
        }

        void StartPilot(RvMode mode)
        {
            _uiError = null;
            if (_selected == null)
            {
                _uiError = "Choose a target vessel first.";
                return;
            }
            Transfer?.ForgetChainStatus();
            Pilot.Start(mode, _selected, RendezvousSettings.prepare_only.V, _ourPick, _theirPick);
        }

        void StartChain(RvMode mode)
        {
            _uiError = null;
            if (_selected == null)
            {
                _uiError = "Choose a target vessel first.";
                return;
            }
            if (Transfer == null)
            {
                _uiError = "The Transfer pilot is not loaded.";
                return;
            }
            Transfer.PlanChain(_selected, mode, RendezvousSettings.prepare_only.V);
        }

        void OnStop()
        {
            if (Pilot == null) return;
            _uiError = null;
            if (ChainLeg()) Transfer.Stop();
            else Pilot.Stop();
        }

        void OnResume()
        {
            if (Pilot == null) return;
            _uiError = null;
            if (ChainResume()) Transfer.Resume();
            else Pilot.Resume();
        }

        void OnCancelSaved()
        {
            if (Pilot == null) return;
            _uiError = null;
            if (ChainResume()) Transfer.CancelSaved();
            else Pilot.CancelSaved();
        }

        void OnTargetPicked(string id)
        {
            if (Busy()) return;
            int i = RendezvousReadout.FindId(_choices, id);
            if (i < 0) return;
            _uiError = null;
            Select(i);
            _nextLists = 0;
            _nextRefresh = 0;
        }

        void Select(int i)
        {
            _selected = i >= 0 ? _choices[i].Id : null;
            if (_selected != null)
                RendezvousSettings.target_id.V = _selected;
            _targets.Select(_selected);
        }

        // ================================================================ tick

        public override bool IsRunning
        {
            get
            {
                var p = Pilot;
                return p != null && (p.isRunning || ChainLeg());
            }
        }

        public override HudState Hud
        {
            get
            {
                if (!IsRunning) return null;
                var p = Pilot;
                _hud.Phases = _steps_;
                _hud.CurrentPhase = _lastIndex;
                _hud.Phase = _lastIndex >= 0 && _lastIndex < _steps_.Count ? _steps_[_lastIndex] : "";
                _hud.Readouts.Clear();
                _hud.Readouts.Add(("Distance", _distTile.Value + " " + DistanceUnit(_distance)));
                _hud.Readouts.Add(("Closing", _closingTile.Value + " m/s"));
                _hud.Alert = p.Phase == RvPhase.Failed ? p.Status : null;
                _hud.Stop = OnStop;
                return _hud;
            }
        }

        public override void Tick()
        {
            if (_sample) return;
            var pilot = Pilot;
            if (pilot == null)
            {
                if (_noPilotShown) return;
                _noPilotShown = true;
                _status.Reset();
                _status.Status.Status(NoPilotStatus);
                _status.Refresh();
                _main.SetEnabled(false);
                _abort.SetEnabled(false);
                return;
            }
            _noPilotShown = false;
            float now = Time.realtimeSinceStartup;
            if (now >= _nextLists)
            {
                _nextLists = now + ListSeconds;
                Guarded(UpdateLists);
            }
            if (now >= _nextRefresh)
            {
                _nextRefresh = now + RefreshSeconds;
                Guarded(UpdateRelative);
                Guarded(UpdatePlan);
                Guarded(UpdateReadouts);
                Guarded(UpdateTimeline);
            }
            UpdateControls();
            UpdateStatus();
        }

        static SpaceWarp2.API.Logging.ILogger _log;

        static void Guarded(Action read)
        {
            try
            {
                read();
            }
            catch (Exception e)
            {
                (_log ??= ModIdentity.Logger("Rendezvous")).LogInfo($"[Rendezvous] new tab: {e.GetType().Name}: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- lists

        void UpdateLists()
        {
            if (!Context.InGame) return;
            var pilot = Pilot;
            var ours = RendezvousSnapshot.Ours;
            var gameTarget = RendezvousSnapshot.TargetVessel(ours);
            string gameTargetId = gameTarget?.GlobalId.Guid.ToString("D");

            _choices = RendezvousReadout.Targets(RendezvousSnapshot.VesselsByBody(ours));
            int i;
            if (gameTargetId != null && gameTargetId != _lastGameTarget && RendezvousReadout.FindId(_choices, gameTargetId) >= 0)
                i = RendezvousReadout.FindId(_choices, gameTargetId);
            else
            {
                i = RendezvousReadout.FindId(_choices, _selected);
                if (i < 0)
                    i = RendezvousReadout.PreSelect(_choices, gameTargetId, RendezvousSettings.target_id.V);
            }
            _lastGameTarget = gameTargetId;

            FillItems(ours);
            _targets.SetItems(_items, null);
            Select(i);

            UpdatePorts(ours);

            if ((pilot.Stages == null || pilot.Stages.Count == 0) &&
                VesselSnapshot.TryRead(out _, out _, out _, out _, out var st, out _) && st != null)
                _liveStages = st;

            var rcs = DockingParts.Thrusters(ours);
            _rcsTotal = ours == null ? -1 : rcs.Total;
            _rcsUsable = rcs.Usable;
            _rcsLeft = Monopropellant(ours);
        }

        // The rows: each body's name, then its vessels with their distance (vessels around our body).
        void FillItems(VesselComponent ours)
        {
            _items.Clear();
            var byId = new Dictionary<string, VesselComponent>();
            var all = GeneralTools.Game?.UniverseModel?.GetAllVessels();
            if (all != null)
                foreach (var v in all)
                    if (v?.SimulationObject != null) byId[v.GlobalId.Guid.ToString("D")] = v;
            double ut = GeneralTools.Current_UT;
            var ourOrbit = ours?.Orbit;
            Vector3d ourR = ourOrbit != null ? ourOrbit.GetRelativePositionAtUTZup(ut) : default;
            foreach (var c in _choices)
            {
                string detail = "";
                if (c.Id != null && ourOrbit != null && byId.TryGetValue(c.Id, out var v) && v.Orbit != null &&
                    ReferenceEquals(v.Orbit.referenceBody, ourOrbit.referenceBody))
                    detail = RendezvousTabFormat.Distance((v.Orbit.GetRelativePositionAtUTZup(ut) - ourR).magnitude);
                _items.Add(new RendezvousTargetList.Item { Id = c.Header ? null : c.Id, Label = c.Label, Detail = detail });
            }
        }

        static float Monopropellant(VesselComponent vessel)
        {
            var db = GeneralTools.Game?.ResourceDefinitionDatabase;
            var group = vessel?.SimulationObject?.PartOwner?.ContainerGroup;
            if (db == null || group == null) return float.NaN;
            var id = db.GetResourceIDFromName("MonoPropellant");
            return RendezvousChecks.Fraction(group.GetResourceStoredUnits(id), group.GetResourceCapacityUnits(id));
        }

        void UpdatePorts(VesselComponent ours)
        {
            var target = RendezvousSnapshot.FindVessel(_selected);
            string key = $"{PilotVessel.IdOf(ours)}|{_selected}";
            _ourPorts = RendezvousReadout.Ports(PortEntries(ours));
            _theirPorts = RendezvousReadout.Ports(PortEntries(target));
            if (key != _portsFor)
            {
                _portsFor = key;
                _ourPick = _theirPick = null;
            }
            _ourPick = FillPorts(_ourDrop, _ourPorts, _ourPick);
            _theirPick = FillPorts(_theirDrop, _theirPorts, _theirPick);

            _portsOk = target != null && _theirPorts.Count > 1 && _ourPorts.Count > 1;
            if (target == null)
                _portHint.Set("");
            else if (_theirPorts.Count <= 1)
                _portHint.Set("The target has no docking port.");
            else if (_ourPorts.Count <= 1)
                _portHint.Set("Our vessel has no docking port.");
            else
                _portHint.Set("Auto picks the free compatible pair that needs the least rotation.");
            PortSummary();
        }

        static List<RendezvousReadout.PortEntry> PortEntries(VesselComponent vessel)
        {
            var entries = new List<RendezvousReadout.PortEntry>();
            if (vessel == null) return entries;
            foreach (var p in DockingParts.Ports(vessel))
                entries.Add(new RendezvousReadout.PortEntry { Id = p.Info.PartId, Name = p.Part?.PartName, Free = p.Info.Free });
            return entries;
        }

        static string FillPorts(AppDropdown drop, List<RendezvousReadout.Choice> choices, string pick)
        {
            int i = RendezvousReadout.FindId(choices, pick);
            drop.SetChoices(RendezvousReadout.Labels(choices), i >= 0 ? i : 0);
            return i >= 0 ? pick : null;
        }

        // "Ours: <port>" / "Theirs: <port>": the pilot's pair while it docks, else the picks.
        void PortSummary()
        {
            var p = Pilot;
            string ours, theirs;
            if (p != null && (p.Docking || p.Phase == RvPhase.Docked) && (p.OurPortName != null || p.TargetPortName != null))
            {
                ours = p.OurPortName ?? "?";
                theirs = p.TargetPortName ?? "?";
            }
            else
            {
                ours = PortName(_ourPorts, _ourPick);
                theirs = PortName(_theirPorts, _theirPick);
            }
            SetText(_portOurs, "Ours: " + ours);
            SetText(_portTheirs, "Theirs: " + theirs);
        }

        static string PortName(List<RendezvousReadout.Choice> choices, string pick)
        {
            int i = RendezvousReadout.FindId(choices, pick);
            return i >= 0 ? choices[i].Label : RendezvousReadout.AutoLabel + " (least rotation)";
        }

        static void SetText(Label l, string text)
        {
            if (l.text != text) l.text = text;
        }

        // ---------------------------------------------------------------- relative state, view

        void UpdateRelative()
        {
            _distance = _relSpeed = _relInc = _closing = double.NaN;
            _angle = 0;
            _far = false;
            if (!Context.InGame) return;
            var ours = RendezvousSnapshot.Ours;
            var target = RendezvousSnapshot.FindVessel(_selected);
            if (ours == null || target == null)
            {
                _relativeNote.Set(_selected == null ? "Choose a vessel, or target one in the game." : "The vessel is gone.");
                return;
            }
            string ourBody = ours.Orbit?.referenceBody?.bodyName, targetBody = target.Orbit?.referenceBody?.bodyName;
            if (FarRendezvous.IsFar(ourBody, targetBody))
            {
                _far = true;
                _relativeNote.Set(FarRendezvous.FarHint(target.Name, targetBody));
                return;
            }
            if (!RendezvousSnapshot.TryRead(ours, target, out var s, out string error))
            {
                _relativeNote.Set(error);
                return;
            }
            RendezvousFlight.Relative(s.OurR, s.OurV, s.TargetR, s.TargetV, out _distance, out _relSpeed, out _relInc);
            _relativeNote.Set(RendezvousReadout.Relative(_distance, _relSpeed, _relInc));
            Vector3d rel = s.OurR - s.TargetR, dv = s.OurV - s.TargetV;
            if (_distance > 1e-6)
                _closing = -Vector3d.Dot(rel, dv) / _distance;
            // The target's orbit frame: prograde to the right, radial up (the out-of-plane part only
            // counts in the distance).
            Vector3d rh = s.TargetR.normalized, vh = s.TargetV.normalized;
            Vector3d xh = (vh - Vector3d.Dot(vh, rh) * rh).normalized;
            _angle = Math.Atan2(Vector3d.Dot(rel, rh), Vector3d.Dot(rel, xh));
        }

        bool DockShown(RendezvousPilot p) =>
            p.Docking || p.Phase == RvPhase.Docked || (p.Phase == RvPhase.Failed && !double.IsNaN(p.DockDistance));

        void UpdateReadouts()
        {
            var p = Pilot;
            bool docking = p.Docking && !double.IsNaN(p.DockDistance);
            double dist = docking ? p.DockDistance : _distance;
            double closing = docking ? p.DockClosing : _closing;
            var (dv, du) = RendezvousTabFormat.DistanceTile(dist);
            _distTile.SetValue(dv, du);
            _closingTile.SetValue(double.IsNaN(closing) ? "-" : Inv($"{closing:F2}"));
            _lateralTile.SetValue(docking && !double.IsNaN(p.DockLateral) ? Inv($"{p.DockLateral:F2}") : "-");
            _rcsTile.SetValue(float.IsNaN(_rcsLeft) ? "-" : Inv($"{_rcsLeft * 100:F0}"));
            _rcsTile.SetKind(float.IsNaN(_rcsLeft) || _rcsLeft >= 0.2f ? AppTile.Kind.Ok : AppTile.Kind.Alert);

            // The docking group of the classic tab: the phase line, the translation, the aborts.
            bool dockShown = DockShown(p);
            string translate = null, aborts = null;
            if (dockShown)
            {
                _dockLine.Set(p.Phase == RvPhase.Docked ? "Docked."
                    : RendezvousReadout.DockLine(p.DockPhase, p.DockDistance, p.DockLateral, p.DockClosing));
                bool probed = p.TranslateMode != TranslateMode.Refused || !string.IsNullOrEmpty(p.TranslateNote);
                translate = probed ? RendezvousReadout.TranslateLine(p.TranslateMode, p.TranslateNote) : null;
                aborts = RendezvousReadout.AbortLine(p.DockAborts, p.DockAbortReason);
            }
            else
                _dockLine.Set("");
            _checks.Set(RendezvousChecks.Build(_selected != null, _far, _portHint.text, _portsOk,
                _selected != null ? _rcsTotal : -1, _rcsUsable, _budgetLine, _budgetOk, translate, aborts));
            PortSummary();

            // The view.
            var state = new ApproachState { Distance = double.NaN };
            if (_selected == null)
                state.Message = "Choose a target";
            else if (docking)
            {
                state.HasTarget = true;
                state.PortFrame = true;
                state.Distance = p.DockDistance;
                state.Angle = ApproachGeometry.PortAngle(p.DockDistance, p.DockLateral);
                state.Note = HoldNote(p.DockPhase);
            }
            else if (_far)
            {
                state.Message = "The target orbits another body";
            }
            else if (!double.IsNaN(_distance))
            {
                state.HasTarget = true;
                state.Distance = _distance;
                state.Angle = _angle;
                state.Note = ApproachGeometry.OffScale(_distance) ? RendezvousTabFormat.Distance(_distance) : "";
            }
            else
                state.Message = "No relative state";
            _view.Set(state);
            _viewCard.Note = state.PortFrame ? "target frame, port axis" : "target frame, orbit axes";
        }

        public static string HoldNote(DockPhase phase)
        {
            switch (phase)
            {
                case DockPhase.Align: return "aligning on the axis";
                case DockPhase.Hold50: return "hold at 50 m";
                case DockPhase.Approach10: return "to 10 m";
                case DockPhase.Hold10: return "hold at 10 m";
                case DockPhase.Final: return "final approach";
                case DockPhase.Backoff: return "backing off";
                case DockPhase.Docked: return "contact";
                default: return "aborted";
            }
        }

        static string DistanceUnit(double m) => !double.IsNaN(m) && m >= 10000 ? "km" : "m";

        // ---------------------------------------------------------------- plan, stages

        void UpdatePlan()
        {
            if (!Context.InGame) return;
            var pilot = Pilot;
            var t = Transfer;
            var plan = pilot.Current;
            bool docks = RendezvousFlight.Docks(pilot.Mode);
            int from = pilot.isRunning ? pilot.StepIndex : 0;
            IReadOnlyList<StageDv> stages = pilot.Stages;
            if (stages == null || stages.Count == 0)
                stages = _liveStages;
            _budgetLine = null;
            _budgetOk = true;

            var chain = t?.ChainPlan;
            if (chain != null && chain.Ok && plan == null && !pilot.isRunning)
            {
                IReadOnlyList<StageDv> chainStages = t.Stages != null && t.Stages.Count > 0 ? t.Stages : stages;
                var chainBudget = t.ChainBudget ?? t.Budget;
                _budgetOk = chainBudget == null || chainBudget.Ok;
                FillPlan(FarRendezvous.Rows(chain, t.System, GeneralTools.Current_UT, t.isRunning ? t.StepIndex : 0),
                    FarRendezvous.Totals(chain, chainBudget));
                _budgetLine = BudgetLine(chainBudget);
                FillStages(RendezvousReadout.Stages(null, 0, chainStages, null));
                return;
            }
            if (plan == null || !plan.Ok)
            {
                FillPlan(null, null);
                FillStages(RendezvousReadout.Stages(null, 0, stages, null));
                return;
            }
            var budget = RendezvousFlight.Budget(plan, from, stages, docks);
            _budgetOk = budget.Ok;
            _budgetLine = BudgetLine(budget);
            FillPlan(RendezvousReadout.Steps(plan, GeneralTools.Current_UT, from), RendezvousReadout.Totals(plan, budget));
            FillStages(RendezvousReadout.Stages(plan, from, stages, budget));
        }

        static string BudgetLine(BudgetResult b)
        {
            if (b == null) return null;
            double margin = b.Available - b.Needed;
            return margin >= 0 ? Inv($"Δv: {margin:F0} m/s left after the plan") : Inv($"Δv: short by {-margin:F0} m/s");
        }

        void FillPlan(List<RendezvousReadout.StepRow> rows, string totals)
        {
            bool show = rows != null;
            Show(_planSection, show);
            if (!show)
            {
                _planKey = null;
                _planShown = false;
                return;
            }
            // A new plan opens the section once; the player may fold it afterwards.
            if (!_planShown) _planSection.SetOpen(true);
            _planShown = true;
            string key = totals ?? "";
            foreach (var r in rows) key += "|" + r.Kind + r.When + r.DeltaV + r.Done;
            if (key == _planKey) return;
            _planKey = key;
            _steps.Clear();
            foreach (var row in rows)
                _steps.Add(StepElement(row));
            _totals.Set(totals);
            _totals.EnableInClassList("ssa-rendezvous-totals--alert", !_budgetOk);
        }

        static VisualElement StepElement(RendezvousReadout.StepRow row)
        {
            var block = Ui.Box("ssa-rendezvous-step");
            block.EnableInClassList("ssa-rendezvous-step--done", row.Done);
            var line = Ui.Box("ssa-rendezvous-step__line");
            line.Add(Ui.Label(row.Kind, "ssa-rendezvous-step__kind"));
            line.Add(Ui.Label(row.DeltaV, "ssa-rendezvous-step__dv"));
            block.Add(line);
            block.Add(Ui.Label(row.When, "ssa-rendezvous-step__when"));
            if (!string.IsNullOrEmpty(row.Explanation))
                block.Add(Ui.Label(row.Explanation, "ssa-rendezvous-step__why"));
            return block;
        }

        void FillStages(List<string> rows)
        {
            string key = string.Join("\n", rows);
            if (key == _stagesKey) return;
            _stagesKey = key;
            _stagesBox.Clear();
            foreach (string row in rows)
                _stagesBox.Add(Ui.Label(row, "ssa-rendezvous-stage"));
            if (rows.Count == 0) _stagesBox.Add(Ui.Label("No stage read yet.", "ssa-rendezvous-stage"));
        }

        // ---------------------------------------------------------------- timeline

        void UpdateTimeline()
        {
            var p = Pilot;
            bool chainShown = ShowsChain() || ChainLeg();
            bool chain = chainShown || _far;
            RvMode mode = p.isRunning || p.Planning || p.Phase != RvPhase.Plan ? p.Mode : SelectedMode;
            var steps = RendezvousTabPhases.Steps(mode, chain);
            int index;
            if (chainShown)
                index = 0;
            else
            {
                int i = RendezvousTabPhases.Index(mode, p.Phase, p.DockPhase);
                index = i < 0 ? _lastIndex - (chain ? 1 : 0) : i;
                if (chain) index += 1;
            }
            index = Mathf.Clamp(index, 0, steps.Count - 1);
            string key = string.Join("|", steps) + "#" + index;
            if (key == _timelineKey) return;
            _timelineKey = key;
            _steps_ = steps;
            _lastIndex = index;
            Page.SetTimeline(steps, index);
        }

        // ---------------------------------------------------------------- controls, status

        void UpdateControls()
        {
            var p = Pilot;
            var t = Transfer;
            bool chainLeg = ChainLeg();
            bool busy = p.isRunning || p.Planning || chainLeg;
            bool can = !busy && _selected != null;
            bool dockAllowed = RendezvousReadout.DockOnlyAllowed(_distance) && !_far;
            Enable(_mode.ItemAt(2), dockAllowed || busy);
            bool modeOk = SelectedMode != RvMode.DockOnly || dockAllowed;
            Enable(_main, busy || (can && modeOk && (!_far || t != null)));
            string label = busy ? RendezvousTabPhases.RunningLabel(p.Planning || (t != null && t.Searching && chainLeg), p.Docking, chainLeg)
                : _far ? "Transfer + rendezvous" + (SelectedMode == RvMode.RendezvousAndDock ? " + dock" : "")
                : "Start";
            if (_main.Text != label) _main.Text = label;
            Enable(_abort, busy);
            Enable(_targets, !busy);
            Enable(_mode, !busy);
            Enable(_portChange, !busy);
            Enable(_portPicker, !busy);
            bool chainResume = ChainResume();
            Show(_resume, p.HasSavedPlan || chainResume);
            Enable(_resume, chainResume ? t.CanResume : p.CanResume);
            Show(_cancel, p.HasSavedPlan || p.isRunning || chainResume);
        }

        void UpdateStatus()
        {
            var p = Pilot;
            var t = Transfer;
            if (p.isRunning && t != null && t.ChainStatusShown && !ChainLeg())
                t.ForgetChainStatus();
            var s = _status.Status;
            s.Reset();
            if (!string.IsNullOrEmpty(_uiError))
                s.Warning(_uiError);
            else if (ShowsChain() && !string.IsNullOrEmpty(t.Status))
            {
                string text = t.Status;
                bool warn = t.Phase == TransferPhase.Failed || text.StartsWith("Refused") || text.StartsWith("Not enough")
                    || text.StartsWith("The stages") || text.Contains(" stopped during ");
                s.Status(text, warn ? StatusLine.Level.Warning : StatusLine.Level.Normal);
                if (t.Searching)
                    s.Progress(t.SearchProgress, "Planning");
            }
            else if (!string.IsNullOrEmpty(p.Status))
                s.Status(p.Status, p.Phase == RvPhase.Failed || p.Status.StartsWith("Rendezvous stopped")
                    ? StatusLine.Level.Warning : StatusLine.Level.Normal);
            else
                s.Status("Choose a target, then Start.");
            _status.Refresh();
            // A docked vessel or an arrival is good news: the pill turns prograde.
            if (p.Phase == RvPhase.Docked || p.Phase == RvPhase.Done)
                if (_status.Pill.Kind != StatusKind.Ok) _status.Pill.Set(StatusKind.Ok, _status.Pill.Text);

            string offer = p.isRunning ? null : ChainResume() ? t.ResumeOffer : p.ResumeOffer;
            if (_offer.text != (offer ?? "")) _offer.Set(offer);
            string note = !string.IsNullOrEmpty(offer) ? offer : NextStepNote();
            if (Page.Actions.Note != note) Page.Actions.SetNote(note);
        }

        string NextStepNote()
        {
            var p = Pilot;
            if (p == null || !p.isRunning || p.Current?.Steps == null || p.Phase == RvPhase.Dock) return IdleNote;
            int i = p.StepIndex;
            if (i < 0 || i >= p.Current.Steps.Count) return IdleNote;
            var step = p.Current.Steps[i];
            double dt = step.UT - GeneralTools.Current_UT;
            return "Next: " + RendezvousFlight.StepName(step.Kind) + (dt > 0 ? " in " + KerbinCalendar.Duration(dt) : " now") +
                Inv($", {step.DeltaVMagnitude:F1} m/s");
        }

        // ================================================================ sample (the mockup's values)

        public override void ShowSample()
        {
            _sample = true;
            _items.Clear();
            _items.Add(new RendezvousTargetList.Item { Id = "sample-1", Label = "Kerbin Station Alpha", Detail = "182 km" });
            _items.Add(new RendezvousTargetList.Item { Id = "sample-2", Label = "Fuel Depot", Detail = "2 410 km" });
            _items.Add(new RendezvousTargetList.Item { Id = "sample-3", Label = "Comms Relay 3", Detail = "9 800 km" });
            _targets.SetItems(_items, "sample-1");
            _mode.SetWithoutNotify(1);
            SetText(_portOurs, "Ours: Clamp-O-Tron (nose)");
            SetText(_portTheirs, "Theirs: Clamp-O-Tron (port 2)");
            _portHint.Set("");
            _translationNote.Set("");
            _distTile.SetValue("48.6", "m");
            _closingTile.SetValue("0.42");
            _lateralTile.SetValue("0.18");
            _rcsTile.SetValue("86");
            _relativeNote.Set("");
            _checks.Set(new List<RendezvousCheck>
            {
                new RendezvousCheck(StatusKind.Ok, RendezvousChecks.NeverCommanded),
                new RendezvousCheck(StatusKind.Ok, "Port aligned, 0.6°"),
                new RendezvousCheck(StatusKind.Ok, "Speed under limit"),
            });
            _status.Pill.Set(StatusKind.Ok, "Holding at 50 m, then final approach");
            _status.Pill.style.display = DisplayStyle.Flex;
            _view.Set(new ApproachState
            {
                HasTarget = true, PortFrame = true, Distance = 48.6,
                Angle = ApproachGeometry.PortAngle(48.6, 0.18), Note = "hold at 50 m",
            });
            _viewCard.Note = "target frame, port axis";
            _steps_ = RendezvousTabPhases.Full;
            _lastIndex = Array.IndexOf(RendezvousTabPhases.Full, "Hold 50 m");
            Page.SetTimeline(_steps_, _lastIndex);
            Page.Actions.SetNote(IdleNote);
            Show(_cancel, false);
            Show(_resume, false);
            _abort.SetEnabled(true);
            _main.SetEnabled(true);
            _main.Text = "Docking…";
        }
    }

    /// <summary>The tab's number formats (pure).</summary>
    public static class RendezvousTabFormat
    {
        static readonly NumberFormatInfo Spaced = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "." };

        /// <summary>"48 m", "9 800 m"; "18.2 km", "182 km", "2 410 km".</summary>
        public static string Distance(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return "-";
            if (metres < 10000) return metres.ToString("#,0", Spaced) + " m";
            double km = metres / 1000;
            return (km < 100 ? km.ToString("0.0", Spaced) : km.ToString("#,0", Spaced)) + " km";
        }

        /// <summary>The DISTANCE tile: "48.6" m under 1 km, "1 250" m under 10 km, then km.</summary>
        public static (string value, string unit) DistanceTile(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return ("-", "m");
            if (metres < 1000) return (metres.ToString("0.0", Spaced), "m");
            if (metres < 10000) return (metres.ToString("#,0", Spaced), "m");
            double km = metres / 1000;
            return (km < 100 ? km.ToString("0.0", Spaced) : km.ToString("#,0", Spaced), "km");
        }
    }
}
