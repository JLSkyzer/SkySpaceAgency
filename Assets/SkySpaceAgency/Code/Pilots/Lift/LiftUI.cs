using System;
using System.Collections.Generic;
using K2UI.Tabs;
using K2UI;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Transfer;
using KTools;

namespace K2D2.Lift
{
    class LiftUI : K2Page
    {
        LiftPilot pilot;
        public LiftUI(LiftPilot pilot)
        {
            this.pilot = pilot;
            code = "lift";
        }

        public FullStatus status_bar;

        K2Slider end_rotate_ratio, mid_rotate_ratio;

        Label heading_label;

        VisualElement final_grp;

        // Live LIFT INFO readout, same table pattern as NodeExUI's node_infos_el.
        VisualElement lift_infos_el;

        // TRANSFER GOAL (sub-project 4.2): the goal and target dropdowns and the plan readout,
        // shown only for Prepare transfer; the Orbit goal keeps today's layout.
        static readonly string[] GoalLabels = { "Orbit", "Prepare transfer" };
        DropdownField goal_drop, target_drop;
        VisualElement transfer_group, transfer_infos_el, heading_row, heading_el;
        Label transfer_hint;
        BodySystem list_system;
        List<BodyChoices.Entry> entries = new List<BodyChoices.Entry>();
        float next_body_try;

        public override bool onInit()
        {
            LiftSettings settings = pilot.settings;

            status_bar = new FullStatus(panel);
            panel.Q<IntegerField>("start_altitude_km").Bind(settings.start_altitude_km);
            mid_rotate_ratio = panel.Q<K2Slider>("mid_rotate_ratio").Bind(settings.mid_rotate_ratio);
            end_rotate_ratio = panel.Q<K2Slider>("end_rotate_ratio").Bind(settings.end_rotate_ratio);
            panel.Q<IntegerField>("destination_Ap_km").Bind(settings.destination_Ap_km);

            lift_infos_el = panel.Q<VisualElement>("lift_infos");

            final_grp = panel.Q<VisualElement>("final_grp");

            settings.mid_rotate_ratio.listeners += v =>
            {
                if (end_rotate_ratio.value < v)
                    end_rotate_ratio.value = v;
            };

            settings.end_rotate_ratio.listeners += v =>
            {
                if (mid_rotate_ratio.value > v)
                    mid_rotate_ratio.value = v;
            };

            heading_label = panel.Q<Label>("heading_label");
            var heading = panel.Q<K2Compass>("heading").Bind(settings.heading);
            heading_el = heading;
            heading_row = panel.Q<VisualElement>("heading_row");

            goal_drop = panel.Q<DropdownField>("goal");
            goal_drop.choices = new List<string>(GoalLabels);
            goal_drop.SetValueWithoutNotify(GoalLabel(settings.goal.V));
            goal_drop.RegisterValueChangedCallback(evt =>
            {
                int i = Array.IndexOf(GoalLabels, evt.newValue);
                if (i >= 0)
                    settings.goal.V = (LiftGoal)i;
            });
            // The target by name, as the Transfer tab keeps it (BodyChoices).
            target_drop = panel.Q<DropdownField>("transfer_target");
            target_drop.RegisterValueChangedCallback(evt =>
            {
                int i = BodyChoices.FindLabel(entries, evt.newValue);
                if (i >= 0)
                    settings.transfer_target.V = entries[i].Name;
            });
            transfer_group = panel.Q<VisualElement>("transfer_goal_group");
            transfer_infos_el = panel.Q<VisualElement>("transfer_infos");
            transfer_hint = panel.Q<Label>("transfer_hint");

            var graph = panel.Q<VisualElement>("graph");
            pilot.ascent_path.InitUI(graph);

            var max_throttle = panel.Q<K2Slider>("max_throttle").Bind(settings.max_throttle);

            var run_button = panel.Q<ToggleButton>("run");
            pilot.is_running_event += is_running => run_button.Value = is_running;
            // Same "give him a little life" touch as the Node tab: K2's 3 grille lines cascade
            // on/off with the autopilot, via the same is_running_event run_button already
            // listens to above.
            pilot.is_running_event += is_running => status_bar.avatar?.SetRunning(is_running);
            run_button.listeners +=  v =>
            {
                pilot.isRunning = v;
                run_button.label = v ? "Stop" : "Start";
            };

            // The old "settings" page's controls now live inside the ADVANCED foldout on "page"
            // instead of the separate hidden "settings" VisualElement, so setupUI needs to query
            // the whole panel to find them (same move as NodeExUI.cs).
            settings.setupUI(panel);

            // Reset button moved down into the ADVANCED foldout itself, now that the settings
            // page it used to live on is on its way out (replaced by an info button to the About
            // tab once the restyle is done).
            addResetButton(panel.Q<Foldout>("advanced_foldout"), "lift");

            return true;
        }

        static string GoalLabel(LiftGoal goal) =>
            goal == LiftGoal.PrepareTransfer ? GoalLabels[1] : GoalLabels[0];

        // A body list read from the game, retried every 2 s until there is one (as the Transfer tab).
        void UpdateBodies()
        {
            if (list_system == null && UnityEngine.Time.realtimeSinceStartup >= next_body_try)
            {
                next_body_try = UnityEngine.Time.realtimeSinceStartup + 2;
                try
                {
                    BodySystem s = GameBodySystem.Build(GeneralTools.Current_UT, out string error);
                    if (s == null)
                        K2D2_Plugin.logger.LogInfo($"[Lift] tab: no body list yet ({error})");
                    else
                    {
                        list_system = s;
                        entries = BodyChoices.Grouped(s);
                        target_drop.choices = BodyChoices.Labels(entries);
                    }
                }
                catch (Exception e)
                {
                    K2D2_Plugin.logger.LogInfo($"[Lift] tab: no body list yet ({e.GetType().Name}: {e.Message})");
                }
            }
            int i = BodyChoices.FindName(entries, pilot.settings.transfer_target.V);
            target_drop.SetValueWithoutNotify(i >= 0 ? entries[i].Label : "Choose a body");
        }

        void UpdateTransferGoal()
        {
            LiftSettings settings = pilot.settings;
            bool transfer_goal = settings.goal.V == LiftGoal.PrepareTransfer;
            goal_drop.SetValueWithoutNotify(GoalLabel(settings.goal.V));
            transfer_group.Show(transfer_goal);
            // The heading comes from the launch window for a transfer.
            heading_row.Show(!transfer_goal);
            heading_el.Show(!transfer_goal);
            goal_drop.SetEnabled(!pilot.isRunning);
            target_drop.SetEnabled(!pilot.isRunning);
            if (!transfer_goal)
                return;

            UpdateBodies();
            transfer_infos_el.Clear();
            string target = settings.transfer_target.V;
            var plan = pilot.transfer;
            // The last plan, while it is the one of the target chosen.
            if (plan != null && plan.TargetName == target)
            {
                transfer_hint.text = "";
                foreach (var (label, value) in LiftGoalRules.Readout(plan, GeneralTools.Current_UT))
                    AddInfoRow(transfer_infos_el, label, value);
            }
            else
                transfer_hint.text = string.IsNullOrEmpty(target)
                    ? "Choose the target body."
                    : $"Start plans the parking orbit for {target} and the launch time, then waits on the pad and fires the first stage at T-0.";
        }

        void AddInfoRow(string label, string value) => AddInfoRow(lift_infos_el, label, value);

        static void AddInfoRow(VisualElement table, string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("advanced-info-row");

            var label_el = new Label(label);
            label_el.AddToClassList("advanced-info-row-label");
            row.Add(label_el);

            var value_el = new Label(value);
            value_el.AddToClassList("advanced-info-row-value");
            row.Add(value_el);

            table.Add(row);
        }

        void UpdateLiftInfos()
        {
            lift_infos_el.Clear();

            if (pilot.current_subpilot == null)
            {
                AddInfoRow("Status", "Not running");
                return;
            }

            pilot.current_subpilot.UpdateInfoRows(AddInfoRow);
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;

            LiftSettings settings = pilot.settings;

            pilot.ascent_path.updateProfile(pilot.ascent.current_altitude_km);

            end_rotate_ratio.ValueText = $"{settings.end_rotate_altitude_km:n0} km";
            mid_rotate_ratio.ValueText = $"{settings.mid_rotate_altitude_km:n0} km";
            heading_label.text = $"{settings.heading.V:n1} °";

            final_grp.Show(false);

            UpdateTransferGoal();

            UpdateLiftInfos();

            status_bar.Reset();
            if (pilot.isRunning)
            {
                status_bar.Warning($"Status : {pilot.status}");

                if (pilot.current_subpilot != null)
                    pilot.current_subpilot.updateUI(panel, status_bar);
            }
            else
            {
                if (!string.IsNullOrEmpty(pilot.end_status))
                    status_bar.Status("Final status : " + pilot.end_status,
                        pilot.result_ok ? StatusLine.Level.Normal : StatusLine.Level.Warning);
                else
                    // Idle placeholder, same idea as Node's "No Node Created"/"Ready to execute
                    // node!" - K2 has something to say here instead of the status readout just
                    // sitting empty before the pilot's ever been run.
                    status_bar.Status("Lift autopilot not enabled");
            }

            return true;
        }



    }
}