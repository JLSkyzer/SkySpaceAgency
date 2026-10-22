using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing.Precision;
using KSP.Game;
using KSP.Modules;
using KSP.Sim.impl;
using UnityEngine;

namespace K2D2.Landing
{
    // The service module jettison of a parachute precision return: plan it on the live vessel
    // (JettisonPlan), then activate the next stage once and check that the active vessel is the
    // capsule alone within VerifySeconds of game time. Never activates a second stage: on any
    // doubt it fails with a reason, and the caller falls back. When it fails with capsule_lost,
    // the active vessel is not the capsule: the caller must not fly ParachuteDescent on it.
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c):
    // - PartComponent.ActivationStage is PartComponentData.StageIndex, which
    //   StagingComponent.RecachePartStageIndices sets to the part's index in AvailableStages.
    // - StagingComponent.ActivateNextStage, with L = StageCount - 1: if AvailableStages[L].IsActive
    //   it activates the parts of stage L - 1, moves the still-active parts of L into it and removes
    //   stage L; otherwise it activates stage L (JettisonPlan.NextStage).
    // - It returns without doing anything (only a UI message) while the time warp is on rails
    //   (IsWarping and not IsPhysicsTimeWarp) or the game is paused: this controller stops the
    //   on-rails warp first (instantly) and waits while paused.
    // - After the split the game makes the control owner's piece active (JettisonPlan's header);
    //   the plan requires it on the capsule. If the active vessel is still not the capsule, this
    //   controller switches to the vessel holding the capsule (ViewController.SetActiveVehicle,
    //   the call the game itself makes in SpaceSimulation.PromoteOrphanedParts).
    public class Jettison : ExecuteController
    {
        public const double VerifySeconds = 3;      // game time after the activation (and after a switch)
        public const float WarpExitSeconds = 10;    // real time to leave an on-rails warp

        readonly LandingPilot landing;

        public JettisonPlanResult plan;
        // Set with failure_reason when the active vessel is not the capsule after the split.
        public bool capsule_lost;
        int parts_before, stage_count;
        bool activated, switched;

        // The stage was activated: the split may have happened, the active vessel must be checked.
        public bool Activated => activated;
        public int PartsBefore => parts_before;
        double check_from_ut;
        float warp_exit_time;

        public Jettison(LandingPilot landing)
        {
            this.landing = landing;
        }

        // The plan on the live vessel; also used at start to refuse precision. A log line with the
        // stage, the decouplers and the part sets; the reason when it is refused.
        public static bool TryPlan(VesselComponent vessel, out JettisonPlanResult plan, out string log) =>
            TryPlan(vessel, out plan, out log, out _);

        static bool TryPlan(VesselComponent vessel, out JettisonPlanResult plan, out string log, out int stageCount)
        {
            plan = default;
            stageCount = 0;
            try
            {
                PartComponent control = vessel?.GetControlOwner();
                PartOwnerComponent owner = vessel?.SimulationObject?.PartOwner;
                StagingComponent staging = vessel?.SimulationObject?.Staging;
                if (owner == null || staging == null)
                {
                    log = "the vessel's parts or staging are not available.";
                    plan = Refused(log);
                    return false;
                }

                var names = new Dictionary<string, string>();
                List<CapsulePart> tree = ReentryContext.ReadParts(owner, new Dictionary<string, Data_Parachute>(), names,
                    out List<PartComponent> components, out int unknownNodes);
                var parts = new List<JettisonPart>(tree.Count);
                for (int i = 0; i < tree.Count; i++)
                {
                    PartComponent part = components[i];
                    parts.Add(new JettisonPart
                    {
                        Part = tree[i],
                        ActivationStage = part.ActivationStage,
                        DecoupleStage = part.DecoupleStage,
                        IsHeatshield = part.TryGetModuleData<PartComponentModule_Heatshield, Data_Heatshield>(out Data_Heatshield shield) && shield != null,
                        IsDockingPort = part.TryGetModuleData<PartComponentModule_DockingNode, Data_DockingNode>(out Data_DockingNode port) && port != null,
                        IsControl = control != null && part.Guid == control.Guid,
                    });
                }

                stageCount = staging.StageCount;
                bool lastActive = stageCount > 0 && staging.AvailableStages[stageCount - 1].IsActive;
                int next = JettisonPlan.NextStage(stageCount, lastActive);
                plan = JettisonPlan.Plan(parts, next);

                string Name(string id) => names.TryGetValue(id, out string n) ? n : id;
                string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(Name));
                string stages = $"{stageCount} stage(s), last {(lastActive ? "active" : "not active yet")}, next {next}" +
                    (control == null ? ", no control owner" : $", control owner {control.PartName}");
                if (!plan.Ok)
                {
                    log = $"{stages}: {plan.Reason}";
                    return false;
                }
                log = $"{stages}: decoupler(s) {Names(plan.Decouplers)}" +
                    (plan.OtherActivated.Count > 0 ? $", also activates {string.Join(", ", plan.OtherActivated.Select(id => Name(id) + " (service side)"))}" : "") +
                    $"; keeps {plan.CapsuleIds.Count} part(s) ({Names(plan.CapsuleIds)}), drops {plan.ServiceIds.Count} ({Names(plan.ServiceIds)}); " +
                    $"heat shields kept {plan.HeatshieldsKept} of {plan.Heatshields}; " +
                    $"{plan.ServiceWithDecoupleStage} of the dropped part(s) have DecoupleStage {next}" +
                    (unknownNodes > 0 ? $"; {unknownNodes} decoupler(s) with an unknown explosive node elsewhere" : "");
                return true;
            }
            catch (Exception e)
            {
                log = $"cannot read the vessel's staging ({e.GetType().Name}: {e.Message}).";
                plan = Refused(log);
                return false;
            }
        }

        static JettisonPlanResult Refused(string why) =>
            new JettisonPlanResult { Ok = false, Reason = why, Message = $"{JettisonPlan.Refusal} ({why})", Stage = -1 };

        public override void Start()
        {
            base.Start();
            activated = false;
            switched = false;
            capsule_lost = false;
            warp_exit_time = float.NaN;
            VesselComponent vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            // Planned again now: the player may have staged or reordered since the start.
            if (!TryPlan(vessel, out plan, out string log, out stage_count))
            {
                landing.logger.LogWarning($"[Landing] reentry: jettison refused: {log}");
                Fail(plan.Message);
                return;
            }
            parts_before = vessel.SimulationObject.PartOwner.PartCount;
            landing.logger.LogInfo($"[Landing] reentry: jettison plan: {log}; {parts_before} part(s) before");
            status_line = "Jettisoning the service module";
        }

        public override void Update()
        {
            if (finished)
                return;
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Failed($"{e.GetType().Name}: {e.Message}");
            }
        }

        void Step()
        {
            VesselComponent vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            GameInstance game = GameManager.Instance?.Game;
            if (vessel == null || game == null)
                return;
            double ut = game.UniverseModel.UniverseTime;

            if (!activated)
            {
                if (game.UniverseModel.Time.IsPaused)
                    return;
                TimeWarp warp = game.ViewController?.TimeWarp;
                if (warp != null && warp.IsWarping && !warp.IsPhysicsTimeWarp)
                {
                    if (float.IsNaN(warp_exit_time))
                        warp_exit_time = Time.unscaledTime;
                    TimeWarpTools.SetRateIndex(0, true);
                    if (Time.unscaledTime - warp_exit_time > WarpExitSeconds)
                        Failed($"the time warp did not stop within {WarpExitSeconds:n0} s");
                    return;
                }

                // The staging must be the one planned at Start.
                StagingComponent staging = vessel.SimulationObject.Staging;
                int count = staging.StageCount;
                int next = JettisonPlan.NextStage(count, count > 0 && staging.AvailableStages[count - 1].IsActive);
                if (count != stage_count || next != plan.Stage)
                {
                    Failed($"stages changed since the plan ({stage_count} stage(s), next {plan.Stage} -> {count}, next {next})");
                    return;
                }

                vessel.ActivateNextStage();
                activated = true;
                check_from_ut = ut;
                landing.logger.LogInfo($"[Landing] reentry: jettison: stage {plan.Stage} activated at T{ut:F1}");
                return;
            }

            var ids = PartIds(vessel);
            int left = plan.ServiceIds.Count(ids.Contains);
            int capsule = plan.CapsuleIds.Count(ids.Contains);
            if (left == 0 && capsule == plan.CapsuleIds.Count)
            {
                landing.logger.LogInfo($"[Landing] reentry: jettison done in {ut - check_from_ut:F1} s{(switched ? " after the vessel switch" : "")}: " +
                    $"{parts_before} -> {ids.Count} part(s) ({plan.ServiceIds.Count} dropped, {plan.CapsuleIds.Count} kept)");
                status_line = "Service module jettisoned";
                finished = true;
                return;
            }
            if (ut - check_from_ut <= VerifySeconds)
                return;

            if (capsule < plan.CapsuleIds.Count)
            {
                // The active vessel is not the capsule: switch once to the vessel that is.
                VesselComponent target = switched ? null : CapsuleVessel(game);
                if (target != null)
                {
                    landing.logger.LogWarning($"[Landing] reentry: jettison: the active vessel holds {capsule} of {plan.CapsuleIds.Count} capsule part(s) " +
                        $"and {left} service part(s); switching to {target.Name} ({PartIds(target).Count} part(s))");
                    game.ViewController.SetActiveVehicle(target);
                    switched = true;
                    check_from_ut = ut;
                    return;
                }
                capsule_lost = true;
                Failed($"capsule lost: the active vessel holds {capsule} of {plan.CapsuleIds.Count} capsule part(s) {VerifySeconds:n0} s after stage {plan.Stage}" +
                    (switched ? " and the switch to the capsule" : "") + $", and no vessel holds the capsule alone ({parts_before} -> {ids.Count} part(s))");
                return;
            }
            Failed($"{left} of {plan.ServiceIds.Count} service module part(s) still attached {VerifySeconds:n0} s after stage {plan.Stage}: " +
                $"{parts_before} -> {ids.Count} part(s)");
        }

        static HashSet<string> PartIds(VesselComponent vessel) =>
            new HashSet<string>(vessel.SimulationObject.PartOwner.Parts.Where(p => p != null).Select(p => p.Guid));

        // The vessel that holds every capsule part and no service part, or null.
        VesselComponent CapsuleVessel(GameInstance game)
        {
            foreach (VesselComponent v in game.UniverseModel.GetAllVessels())
            {
                if (v?.SimulationObject?.PartOwner == null)
                    continue;
                var ids = PartIds(v);
                if (plan.CapsuleIds.All(ids.Contains) && !plan.ServiceIds.Any(ids.Contains))
                    return v;
            }
            return null;
        }

        void Failed(string why)
        {
            landing.logger.LogWarning($"[Landing] reentry: jettison failed: {why}");
            Fail("Service module jettison failed: " + why + ".");
        }
    }
}
