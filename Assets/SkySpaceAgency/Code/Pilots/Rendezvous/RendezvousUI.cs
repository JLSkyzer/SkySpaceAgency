using System;
using System.Collections.Generic;
using K2D2.KSPService;
using K2D2.Transfer;
using K2D2.UI;
using K2UI;
using K2UI.Tabs;
using KSP.Sim.impl;
using KTools;
using UnityEngine.UIElements;

namespace K2D2.Rendezvous
{
    // The Rendezvous tab's choices, kept between sessions.
    public static class RendezvousSettings
    {
        public static Setting<string> target_id = new("rendezvous.target_id", "");
        public static Setting<bool> prepare_only = new("rendezvous.prepare_only", false);
    }

    // Rendezvous tab (spec 4.3), in the old Dock tab's slot (page code "dock"): the target vessel,
    // the port pair, the buttons, and RendezvousPilot's readouts (relative state, steps, Δv per
    // stage, translation mode, docking phase). Lists and texts come from RendezvousReadout (pure).
    public class RendezvousUI : K2Page
    {
        // Every element this page looks up, by name (checked by the UI tests against the window).
        public static readonly string[] Elements =
        {
            "target_vessel", "relative", "our_port", "target_port", "port_hint", "prepare_only",
            "rendezvous_dock", "rendezvous_only", "dock_only", "stop", "resume", "cancel_saved", "resume_offer",
            "status_pilot", "pilot_console", "progress",
            "dock_group", "dock_ports", "translate_mode", "dock_phase", "dock_aborts",
            "plan_group", "steps", "totals", "stages_foldout", "stages",
        };

        const float RefreshSeconds = 0.5f;
        const float ListSeconds = 2f;

        readonly RendezvousPilot pilot;

        DropdownField target_drop, our_port_drop, target_port_drop;
        Label relative_label, port_hint, resume_offer, dock_ports, translate_mode, dock_phase, dock_aborts, totals_label;
        Button rv_dock_bt, rv_only_bt, dock_only_bt, stop_bt, resume_bt, cancel_bt;
        VisualElement dock_group, plan_group, steps_el, stages_el;
        FullStatus status_bar;

        List<RendezvousReadout.Choice> targets = new List<RendezvousReadout.Choice>();
        List<RendezvousReadout.Choice> our_ports = new List<RendezvousReadout.Choice>();
        List<RendezvousReadout.Choice> their_ports = new List<RendezvousReadout.Choice>();
        string selected_target;       // the vessel shown in the list (GUID), null when none
        string last_game_target;      // the game's target last seen: a new one is selected once
        string ports_for;             // "<ours>|<target>": the vessels the port lists were read from
        string our_port_pick, target_port_pick;   // null: Auto

        double distance = double.NaN, rel_speed = double.NaN, rel_inc = double.NaN;
        List<StageDv> live_stages = new List<StageDv>();
        string ui_error;
        float next_refresh, next_lists;

        public RendezvousUI(RendezvousPilot pilot)
        {
            this.pilot = pilot;
            code = "dock";
        }

        public override bool onInit()
        {
            foreach (string name in Elements)
                if (panel.Q(name) == null)
                {
                    K2D2_Plugin.logger.LogError($"[Rendezvous] the Rendezvous tab has no element named {name}");
                    return false;
                }

            target_drop = panel.Q<DropdownField>("target_vessel");
            target_drop.RegisterValueChangedCallback(evt => OnTargetPicked(evt.newValue));
            our_port_drop = panel.Q<DropdownField>("our_port");
            our_port_drop.RegisterValueChangedCallback(evt => our_port_pick = PickedPort(our_ports, evt.newValue));
            target_port_drop = panel.Q<DropdownField>("target_port");
            target_port_drop.RegisterValueChangedCallback(evt => target_port_pick = PickedPort(their_ports, evt.newValue));
            relative_label = panel.Q<Label>("relative");
            port_hint = panel.Q<Label>("port_hint");
            panel.Q<K2Toggle>("prepare_only").Bind(RendezvousSettings.prepare_only);

            rv_dock_bt = panel.Q<Button>("rendezvous_dock");
            rv_only_bt = panel.Q<Button>("rendezvous_only");
            dock_only_bt = panel.Q<Button>("dock_only");
            stop_bt = panel.Q<Button>("stop");
            resume_bt = panel.Q<Button>("resume");
            cancel_bt = panel.Q<Button>("cancel_saved");
            rv_dock_bt.clicked += () => StartPilot(RvMode.RendezvousAndDock);
            rv_only_bt.clicked += () => StartPilot(RvMode.RendezvousOnly);
            dock_only_bt.clicked += () => StartPilot(RvMode.DockOnly);
            stop_bt.clicked += () => { ui_error = null; pilot.Stop(); };
            resume_bt.clicked += () => { ui_error = null; pilot.Resume(); };
            cancel_bt.clicked += () => { ui_error = null; pilot.CancelSaved(); };

            resume_offer = panel.Q<Label>("resume_offer");
            status_bar = new FullStatus(panel);
            dock_group = panel.Q("dock_group");
            dock_ports = panel.Q<Label>("dock_ports");
            translate_mode = panel.Q<Label>("translate_mode");
            dock_phase = panel.Q<Label>("dock_phase");
            dock_aborts = panel.Q<Label>("dock_aborts");
            plan_group = panel.Q("plan_group");
            steps_el = panel.Q("steps");
            totals_label = panel.Q<Label>("totals");
            stages_el = panel.Q("stages");
            return true;
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= next_lists)
            {
                next_lists = now + ListSeconds;
                Guarded(UpdateLists);
            }
            if (now >= next_refresh)
            {
                next_refresh = now + RefreshSeconds;
                Guarded(UpdateRelative);
                Guarded(UpdatePlan);
            }
            UpdateControls();
            UpdateStatus();
            UpdateDocking();
            return true;
        }

        // A game read that throws (scene change, vessel being destroyed) skips one refresh.
        static void Guarded(Action read)
        {
            try
            {
                read();
            }
            catch (Exception e)
            {
                K2D2_Plugin.logger.LogInfo($"[Rendezvous] tab: {e.GetType().Name}: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- lists

        void UpdateLists()
        {
            var ours = RendezvousSnapshot.Ours;
            var game_target = RendezvousSnapshot.TargetVessel(ours);
            string game_target_id = game_target?.GlobalId.Guid.ToString("D");

            targets = RendezvousReadout.Targets(RendezvousSnapshot.VesselsByBody(ours));
            target_drop.choices = RendezvousReadout.Labels(targets);
            // A new game target is selected once; the player's pick stands until the next one.
            int i;
            if (game_target_id != null && game_target_id != last_game_target && RendezvousReadout.FindId(targets, game_target_id) >= 0)
                i = RendezvousReadout.FindId(targets, game_target_id);
            else
            {
                i = RendezvousReadout.FindId(targets, selected_target);
                if (i < 0)
                    i = RendezvousReadout.PreSelect(targets, game_target_id, RendezvousSettings.target_id.V);
            }
            last_game_target = game_target_id;
            Select(i);

            UpdatePorts(ours);

            // The vessel's stages before a plan has read them.
            if ((pilot.Stages == null || pilot.Stages.Count == 0) &&
                VesselSnapshot.TryRead(out _, out _, out _, out _, out var st, out _) && st != null)
                live_stages = st;
        }

        void Select(int i)
        {
            selected_target = i >= 0 ? targets[i].Id : null;
            if (selected_target != null)
                RendezvousSettings.target_id.V = selected_target;
            target_drop.SetValueWithoutNotify(i >= 0 ? targets[i].Label : RendezvousReadout.NoTargetLabel);
        }

        void OnTargetPicked(string label)
        {
            int i = RendezvousReadout.FindLabel(targets, label);
            if (i < 0 || targets[i].Header)
            {
                // A body's header is not a vessel: keep the previous choice.
                Select(RendezvousReadout.FindId(targets, selected_target));
                return;
            }
            ui_error = null;
            Select(i);
            next_lists = 0;      // the port lists follow the new target now
            next_refresh = 0;
        }

        // Our ports and the target's, read again when either vessel changes. The picks survive a
        // re-read when the port is still there; else they fall back to Auto.
        void UpdatePorts(VesselComponent ours)
        {
            var target = RendezvousSnapshot.FindVessel(selected_target);
            string key = $"{PilotVessel.IdOf(ours)}|{selected_target}";
            our_ports = RendezvousReadout.Ports(PortEntries(ours));
            their_ports = RendezvousReadout.Ports(PortEntries(target));
            if (key != ports_for)
            {
                ports_for = key;
                our_port_pick = target_port_pick = null;
            }
            our_port_pick = FillPorts(our_port_drop, our_ports, our_port_pick);
            target_port_pick = FillPorts(target_port_drop, their_ports, target_port_pick);

            if (target == null)
                port_hint.text = "";
            else if (their_ports.Count <= 1)
                port_hint.text = "The target has no docking port.";
            else if (our_ports.Count <= 1)
                port_hint.text = "Our vessel has no docking port.";
            else
                port_hint.text = "Auto picks the free compatible pair that needs the least rotation.";
        }

        static List<RendezvousReadout.PortEntry> PortEntries(VesselComponent vessel)
        {
            var entries = new List<RendezvousReadout.PortEntry>();
            if (vessel == null) return entries;
            foreach (var p in DockingParts.Ports(vessel))
                entries.Add(new RendezvousReadout.PortEntry { Id = p.Info.PartId, Name = p.Part?.PartName, Free = p.Info.Free });
            return entries;
        }

        static string FillPorts(DropdownField drop, List<RendezvousReadout.Choice> choices, string pick)
        {
            drop.choices = RendezvousReadout.Labels(choices);
            int i = RendezvousReadout.FindId(choices, pick);
            drop.SetValueWithoutNotify(choices[i >= 0 ? i : 0].Label);
            return i >= 0 ? pick : null;
        }

        static string PickedPort(List<RendezvousReadout.Choice> choices, string label)
        {
            int i = RendezvousReadout.FindLabel(choices, label);
            return i >= 0 ? choices[i].Id : null;
        }

        // ---------------------------------------------------------------- relative state

        void UpdateRelative()
        {
            distance = rel_speed = rel_inc = double.NaN;
            var ours = RendezvousSnapshot.Ours;
            var target = RendezvousSnapshot.FindVessel(selected_target);
            if (ours == null || target == null)
            {
                relative_label.text = selected_target == null ? "Choose a vessel, or target one in the game." : "The vessel is gone.";
                return;
            }
            if (!RendezvousSnapshot.TryRead(ours, target, out var s, out string error))
            {
                relative_label.text = error;
                return;
            }
            RendezvousFlight.Relative(s.OurR, s.OurV, s.TargetR, s.TargetV, out distance, out rel_speed, out rel_inc);
            relative_label.text = RendezvousReadout.Relative(distance, rel_speed, rel_inc);
        }

        // ---------------------------------------------------------------- controls

        void StartPilot(RvMode mode)
        {
            ui_error = null;
            if (selected_target == null)
            {
                ui_error = "Choose a target vessel first.";
                return;
            }
            pilot.Start(mode, selected_target, RendezvousSettings.prepare_only.V, our_port_pick, target_port_pick);
        }

        void UpdateControls()
        {
            bool busy = pilot.isRunning || pilot.Planning;
            bool can = !busy && selected_target != null;
            rv_dock_bt.SetEnabled(can);
            rv_only_bt.SetEnabled(can);
            dock_only_bt.SetEnabled(can && RendezvousReadout.DockOnlyAllowed(distance));
            stop_bt.SetEnabled(busy);
            target_drop.SetEnabled(!busy);
            our_port_drop.SetEnabled(!busy);
            target_port_drop.SetEnabled(!busy);
            resume_bt.Show(pilot.HasSavedPlan);
            resume_bt.SetEnabled(pilot.CanResume);
            cancel_bt.Show(pilot.HasSavedPlan || pilot.isRunning);
        }

        void UpdateStatus()
        {
            status_bar.Reset();
            status_bar.avatar?.SetRunning(pilot.isRunning);
            if (!string.IsNullOrEmpty(ui_error))
                status_bar.Warning(ui_error);
            else if (!string.IsNullOrEmpty(pilot.Status))
                status_bar.Status(pilot.Status, pilot.Phase == RvPhase.Failed || pilot.Status.StartsWith("Rendezvous stopped")
                    ? StatusLine.Level.Warning : StatusLine.Level.Normal);
            else
                status_bar.Status("Choose a target, then Rendezvous + dock.");

            string offer = pilot.isRunning ? null : pilot.ResumeOffer;
            resume_offer.Show(!string.IsNullOrEmpty(offer));
            resume_offer.text = offer ?? "";
        }

        // ---------------------------------------------------------------- docking

        void UpdateDocking()
        {
            bool show = pilot.Docking || pilot.Phase == RvPhase.Docked ||
                (pilot.Phase == RvPhase.Failed && !double.IsNaN(pilot.DockDistance));
            dock_group.Show(show);
            if (!show)
                return;
            dock_ports.text = RendezvousReadout.PortsLine(pilot.OurPortName, pilot.TargetPortName);
            bool probed = pilot.TranslateMode != TranslateMode.Refused || !string.IsNullOrEmpty(pilot.TranslateNote);
            translate_mode.Show(probed);
            translate_mode.text = probed ? RendezvousReadout.TranslateLine(pilot.TranslateMode, pilot.TranslateNote) : "";
            dock_phase.text = pilot.Phase == RvPhase.Docked
                ? "Docked."
                : RendezvousReadout.DockLine(pilot.DockPhase, pilot.DockDistance, pilot.DockLateral, pilot.DockClosing);
            string aborts = RendezvousReadout.AbortLine(pilot.DockAborts, pilot.DockAbortReason);
            dock_aborts.Show(aborts.Length > 0);
            dock_aborts.text = aborts;
        }

        // ---------------------------------------------------------------- plan, stages

        void UpdatePlan()
        {
            var plan = pilot.Current;
            bool docks = RendezvousFlight.Docks(pilot.Mode);
            int from = pilot.isRunning ? pilot.StepIndex : 0;
            IReadOnlyList<StageDv> stages = pilot.Stages;
            if (stages == null || stages.Count == 0)
                stages = live_stages;

            if (plan == null || !plan.Ok)
            {
                plan_group.Show(false);
                FillStages(RendezvousReadout.Stages(null, 0, stages, null));
                return;
            }
            plan_group.Show(true);
            double now = GeneralTools.Current_UT;
            steps_el.Clear();
            foreach (var row in RendezvousReadout.Steps(plan, now, from))
                steps_el.Add(StepElement(row));
            var budget = RendezvousFlight.Budget(plan, from, stages, docks);
            totals_label.text = RendezvousReadout.Totals(plan, budget);
            totals_label.style.color = budget.Ok ? new StyleColor(StyleKeyword.Null) : new StyleColor(new UnityEngine.Color(1f, 0.55f, 0.4f));
            FillStages(RendezvousReadout.Stages(plan, from, stages, budget));
        }

        void FillStages(List<string> rows)
        {
            stages_el.Clear();
            foreach (string row in rows)
            {
                var label = new Label(row);
                label.style.whiteSpace = WhiteSpace.Normal;
                stages_el.Add(label);
            }
        }

        static VisualElement StepElement(RendezvousReadout.StepRow row)
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
    }
}
