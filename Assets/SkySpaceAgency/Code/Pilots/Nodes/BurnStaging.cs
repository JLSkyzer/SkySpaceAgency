using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing;
using K2D2.Landing.Precision;
using K2D2.Transfer;
using KSP.Game;
using KSP.Modules;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Node
{
    /// <summary>
    /// Game glue: staging during a closed-loop burn that ran dry, for the callers that opt in
    /// (TargetOrbitBurn.stage_when_dry). The decisions are pure: DryStageTimer (0.5 s without
    /// thrust, at most one staging per second) and StageDrop + StagingGuard (what the next stage
    /// drops, from the part tree). This class reads the vessel and fires the stage.
    ///
    /// Facts from the IL of Ksp2Redux.dll (Redux 26w42c), see Jettison:
    /// - StagingComponent.ActivateNextStage fires StageCount-1 before the first staging and
    ///   StageCount-2 after it (JettisonPlan.NextStage); PartComponent.ActivationStage is on that
    ///   scale (the index in AvailableStages).
    /// - It does nothing while the warp is on rails or the game is paused: the warp is stopped
    ///   first, and nothing happens while paused.
    /// - After a split the control owner's piece stays the active vessel (JettisonPlan's header).
    /// </summary>
    public sealed class BurnStaging
    {
        public enum Outcome { None, Pending, Staged, Failed }

        static readonly ILogger log = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Transfer");

        readonly DryStageTimer timer = new DryStageTimer();

        public void Reset() => timer.Reset();

        /// <summary>
        /// Every burning frame with Δv still to go. throttle is the one applied since the previous
        /// frame, expectedAccel the full-throttle acceleration the burn counts on (m/s²),
        /// measuredAccel the engines' real one. Pending: wait (the warp is being stopped, or the
        /// game's auto-staging is busy). Failed: stop the burn with failure, already logged.
        /// </summary>
        public Outcome Update(VesselComponent vessel, double ut, double throttle, double expectedAccel, double measuredAccel,
            out string failure)
        {
            failure = null;
            bool dry = DryStageTimer.IsDry(throttle, expectedAccel, measuredAccel);
            if (!timer.ShouldStage(ut, dry, deltaVLeft: true))
                return Outcome.None;

            // The game's auto-staging (StagingPilot) has just fired a stage: count it as ours.
            if (StagingPilot.Instance != null && StagingPilot.Instance.is_staging)
            {
                log.LogInfo("[Transfer] engines dry during the burn, but the auto-staging is already staging: waiting");
                timer.Staged(ut);
                return Outcome.Pending;
            }

            try
            {
                GameInstance game = GameManager.Instance?.Game;
                if (game == null || vessel == null)
                    return Outcome.None;
                if (game.UniverseModel?.Time == null || game.UniverseModel.Time.IsPaused)
                    return Outcome.Pending;
                TimeWarp warp = game.ViewController?.TimeWarp;
                if (warp != null && warp.IsWarping && !warp.IsPhysicsTimeWarp)
                {
                    // ActivateNextStage does nothing on rails.
                    TimeWarpTools.SetRateIndex(0, true);
                    return Outcome.Pending;
                }

                return Stage(vessel, ut, out failure);
            }
            catch (Exception e)
            {
                failure = $"{StageDrop.Prefix}cannot read the next stage ({e.GetType().Name}: {e.Message}). Stage manually.";
                log.LogWarning("[Transfer] staging during the burn: " + failure);
                return Outcome.Failed;
            }
        }

        Outcome Stage(VesselComponent vessel, double ut, out string failure)
        {
            failure = null;
            PartComponent control = vessel.GetControlOwner();
            PartOwnerComponent owner = vessel.SimulationObject?.PartOwner;
            StagingComponent staging = vessel.SimulationObject?.Staging;
            if (owner == null || staging == null)
            {
                failure = $"{StageDrop.Prefix}the vessel's parts or stages are not available. Stage manually.";
                log.LogWarning("[Transfer] staging during the burn: " + failure);
                return Outcome.Failed;
            }

            var names = new Dictionary<string, string>();
            List<CapsulePart> tree = ReentryContext.ReadParts(owner, new Dictionary<string, Data_Parachute>(), names,
                out List<PartComponent> components, out int unknownNodes);
            var parts = new List<DropPart>(tree.Count);
            for (int i = 0; i < tree.Count; i++)
            {
                PartComponent part = components[i];
                bool isControl = control != null && part.Guid == control.Guid;
                parts.Add(new DropPart
                {
                    Part = tree[i],
                    Name = part.PartName,
                    ActivationStage = part.ActivationStage,
                    IsControl = isControl,
                    IsCommand = isControl || (part.TryGetModuleData<PartComponentModule_Command, Data_Command>(out Data_Command command) && command != null),
                    IsDockingPort = part.TryGetModuleData<PartComponentModule_DockingNode, Data_DockingNode>(out Data_DockingNode port) && port != null,
                });
            }

            int count = staging.StageCount;
            bool lastActive = count > 0 && staging.AvailableStages[count - 1].IsActive;
            int next = JettisonPlan.NextStage(count, lastActive);
            StageDropResult result = StageDrop.Decide(parts, next);

            string Name(string id) => names.TryGetValue(id, out string n) ? n : id;
            string dropped = result.DroppedIds.Count == 0 ? "nothing" : string.Join(", ", result.DroppedIds.Select(Name));
            log.LogInfo($"[Transfer] staging {next} drops: {dropped} ({result.DroppedIds.Count} of {parts.Count} part(s)); " +
                $"decoupler(s) {(result.Decouplers.Count == 0 ? "none" : string.Join(", ", result.Decouplers.Select(Name)))}; " +
                $"{count} stage(s), last {(lastActive ? "active" : "not active yet")}, " +
                (control == null ? "no control owner" : $"control owner {control.PartName}") +
                (unknownNodes > 0 ? $"; {unknownNodes} decoupler(s) with an unknown explosive node, cut on every side" : "") +
                $"; {DeltaVStageCheck(vessel, next)}");

            if (!result.MayStage)
            {
                failure = result.Message;
                log.LogWarning($"[Transfer] staging during the burn refused: {result.Reason}");
                return Outcome.Failed;
            }

            vessel.ActivateNextStage();
            timer.Staged(ut);
            log.LogInfo($"[Transfer] staged during the burn: stage {next}");
            return Outcome.Staged;
        }

        // Cross-check of the numbering: does the game's Δv list (StageDv.Stage, from
        // DeltaVStageInfo.Stage) hold the stage ActivateNextStage fires?
        static string DeltaVStageCheck(VesselComponent vessel, int next)
        {
            var info = vessel.VesselDeltaV?.StageInfo;
            if (info == null)
                return "no Δv stage list to compare";
            List<int> stages = info.Cast<DeltaVStageInfo>().Where(s => s != null).Select(s => s.Stage).OrderByDescending(s => s).ToList();
            string list = string.Join(", ", stages);
            return stages.Contains(next)
                ? $"Δv stage list [{list}] holds stage {next}"
                : $"Δv stage list [{list}] does NOT hold stage {next}: the Δv numbering differs from ActivateNextStage's";
        }
    }
}
