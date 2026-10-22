using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing.Precision;
using KSP.Game;
using KSP.Modules;
using KSP.Sim.impl;

namespace K2D2.Landing
{
    // The service module jettison of a parachute precision return: plan it on the live vessel
    // (JettisonPlan), then activate the next stage once and check that the service module's
    // parts have left the active vessel within VerifySeconds of game time. Never activates a
    // second stage: on any doubt it fails with a reason, and the caller falls back.
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c):
    // - PartComponent.ActivationStage is PartComponentData.StageIndex, which
    //   StagingComponent.RecachePartStageIndices sets to the part's index in AvailableStages.
    // - StagingComponent.ActivateNextStage, with L = StageCount - 1: if AvailableStages[L].IsActive
    //   it activates the parts of stage L - 1, moves the still-active parts of L into it and removes
    //   stage L; otherwise it activates stage L (JettisonPlan.NextStage).
    // - It returns without doing anything (only a UI message) while the time warp is on rails
    //   (IsWarping and not IsPhysicsTimeWarp) or the game is paused: this controller leaves the
    //   on-rails warp first and waits while paused.
    public class Jettison : ExecuteController
    {
        public const double VerifySeconds = 3;      // game time after the activation
        public const double WarpExitSeconds = 10;   // game time to leave an on-rails warp

        readonly LandingPilot landing;

        public JettisonPlanResult plan;
        int parts_before;
        bool activated;
        double activated_ut, warp_exit_ut;

        public Jettison(LandingPilot landing)
        {
            this.landing = landing;
        }

        // The plan on the live vessel; also used at start to refuse precision. A log line with the
        // stage, the decouplers and the part sets; the reason when it is refused.
        public static bool TryPlan(VesselComponent vessel, out JettisonPlanResult plan, out string log)
        {
            plan = default;
            try
            {
                PartComponent control = vessel?.GetControlOwner();
                PartOwnerComponent owner = vessel?.SimulationObject?.PartOwner;
                StagingComponent staging = vessel?.SimulationObject?.Staging;
                if (owner == null || staging == null)
                {
                    log = "the vessel's parts or staging are not available.";
                    plan.Reason = log;
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
                        IsControl = control != null && part.Guid == control.Guid,
                    });
                }

                int stageCount = staging.StageCount;
                bool lastActive = stageCount > 0 && staging.AvailableStages[stageCount - 1].IsActive;
                int next = JettisonPlan.NextStage(stageCount, lastActive);
                plan = JettisonPlan.Plan(parts, next);

                string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => names.TryGetValue(id, out string n) ? n : id));
                string stages = $"{stageCount} stage(s), last {(lastActive ? "active" : "not active yet")}, next {next}";
                if (!plan.Ok)
                {
                    log = $"{stages}: {plan.Reason}";
                    return false;
                }
                log = $"{stages}: decoupler(s) {Names(plan.Decouplers)}" +
                    (plan.OtherActivated.Count > 0 ? $", also activates {Names(plan.OtherActivated)}" : "") +
                    $"; keeps {plan.CapsuleIds.Count} part(s) ({Names(plan.CapsuleIds)}), drops {plan.ServiceIds.Count} ({Names(plan.ServiceIds)}); " +
                    $"heat shields kept {plan.HeatshieldsKept} of {plan.Heatshields}; " +
                    $"{plan.ServiceWithDecoupleStage} of the dropped part(s) have DecoupleStage {next}" +
                    (unknownNodes > 0 ? $"; {unknownNodes} decoupler(s) with an unknown explosive node" : "");
                return true;
            }
            catch (Exception e)
            {
                log = $"cannot read the vessel's staging ({e.GetType().Name}: {e.Message}).";
                plan = new JettisonPlanResult { Ok = false, Reason = log, Stage = -1 };
                return false;
            }
        }

        public override void Start()
        {
            base.Start();
            activated = false;
            warp_exit_ut = double.NaN;
            VesselComponent vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            // Planned again now: the player may have staged or reordered since the start.
            if (!TryPlan(vessel, out plan, out string log))
            {
                landing.logger.LogWarning($"[Landing] reentry: jettison refused: {log}");
                Fail($"{JettisonPlan.Refusal} ({plan.Reason})");
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
            VesselComponent vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            GameInstance game = GameManager.Instance?.Game;
            if (vessel == null || game == null)
                return;
            double ut = game.UniverseModel.UniverseTime;

            if (!activated)
            {
                TimeWarp warp = game.ViewController?.TimeWarp;
                if (game.UniverseModel.Time.IsPaused)
                    return;
                if (warp != null && warp.IsWarping && !warp.IsPhysicsTimeWarp)
                {
                    if (double.IsNaN(warp_exit_ut))
                        warp_exit_ut = ut;
                    TimeWarpTools.SetRateIndex(0, false);
                    if (ut - warp_exit_ut > WarpExitSeconds)
                        Failed($"the time warp did not stop within {WarpExitSeconds:n0} s");
                    return;
                }
                vessel.ActivateNextStage();
                activated = true;
                activated_ut = ut;
                landing.logger.LogInfo($"[Landing] reentry: jettison: stage {plan.Stage} activated at T{ut:F1}");
                return;
            }

            var ids = new HashSet<string>(vessel.SimulationObject.PartOwner.Parts.Where(p => p != null).Select(p => p.Guid));
            int left = plan.ServiceIds.Count(ids.Contains);
            int capsule = plan.CapsuleIds.Count(ids.Contains);
            if (left == 0 && capsule == plan.CapsuleIds.Count)
            {
                landing.logger.LogInfo($"[Landing] reentry: jettison done in {ut - activated_ut:F1} s: {parts_before} -> {ids.Count} part(s) " +
                    $"({plan.ServiceIds.Count} dropped, {plan.CapsuleIds.Count} kept)");
                status_line = "Service module jettisoned";
                finished = true;
                return;
            }
            if (ut - activated_ut > VerifySeconds)
            {
                string what = capsule < plan.CapsuleIds.Count
                    ? $"the active vessel lost {plan.CapsuleIds.Count - capsule} capsule part(s) (it may now be the service module)"
                    : $"{left} of {plan.ServiceIds.Count} service module part(s) still attached";
                Failed($"{what} {VerifySeconds:n0} s after stage {plan.Stage}: {parts_before} -> {ids.Count} part(s)");
            }
        }

        void Failed(string why)
        {
            landing.logger.LogWarning($"[Landing] reentry: jettison failed: {why}");
            Fail("Service module jettison failed: " + why + ".");
        }
    }
}
