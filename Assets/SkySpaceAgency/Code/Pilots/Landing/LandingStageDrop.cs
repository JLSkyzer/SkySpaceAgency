using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.Node;
using K2D2.Transfer;
using KSP.Game;
using KSP.Modules;
using KSP.Sim.impl;
using UnityEngine;

namespace K2D2.Landing
{
    // Game glue of a landing with a chosen stage: drops the active lower stage, one activation at
    // a time, the way BurnStaging and Jettison do. The decisions are pure: StagedBrakePlan.DropNow
    // (dry or at the drop height), DryStageTimer (0.5 s without thrust) and StageDrop +
    // StagingGuard (what the next stage drops, from the part tree, landing legs included).
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c), see Jettison and BurnStaging:
    // - StagingComponent.ActivateNextStage fires StageCount-1 before the first staging and
    //   StageCount-2 after it (JettisonPlan.NextStage); PartComponent.ActivationStage is on that scale.
    // - It does nothing while the warp is on rails or the game is paused: the warp is stopped
    //   first, and nothing happens while paused.
    // - After a split the control owner's piece is the active vessel (JettisonPlan's header).
    // After the activation, the dropped parts must have left the active vessel within
    // VerifySeconds of game time; otherwise the drop failed.
    public class LandingStageDrop
    {
        public const double VerifySeconds = 3;
        public const float WarpExitSeconds = 10;

        public enum Outcome { None, Waiting, Activated, Dropped, Failed }

        readonly LandingPilot landing;
        readonly DryStageTimer timer = new DryStageTimer();

        bool activated;
        double activated_ut;
        float warp_exit_time = float.NaN;
        StageDropResult pending;
        int parts_before;
        DropReason pending_reason;
        double pending_altitude, pending_speed;

        public LandingStageDrop(LandingPilot landing)
        {
            this.landing = landing;
        }

        public bool Activated => activated;
        public DropReason PendingReason => pending_reason;
        public double PendingAltitude => pending_altitude;
        public double PendingSpeed => pending_speed;
        public string LastFailure { get; private set; }

        public void Reset()
        {
            timer.Reset();
            activated = false;
            warp_exit_time = float.NaN;
            pending = default;
            LastFailure = null;
        }

        /// <summary>
        /// What dropping the next stage would drop, from the live part tree, and the next stages
        /// after it (count activations, on the full tree: a later stage's result also lists the
        /// parts of the earlier ones, which only repeats their refusal). Null when every stage may
        /// be dropped, else the refusal for the player.
        /// </summary>
        public static string CheckDrops(VesselComponent vessel, int count, out string log)
        {
            log = "";
            try
            {
                if (!ReadTree(vessel, out List<DropPart> parts, out Dictionary<string, string> names, out int next, out string why))
                {
                    log = why;
                    return $"Cannot tell what dropping the lower stage would drop ({why}).";
                }
                var lines = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    StageDropResult r = StageDrop.Decide(parts, next - i);
                    lines.Add($"activation {next - i} drops {Describe(r, names)}");
                    if (!r.MayStage)
                    {
                        log = string.Join("; ", lines);
                        return r.NoStageLeft
                            ? "No stage left to drop: choose the current stage."
                            : $"Stage {i + 1} cannot be dropped: it would drop {r.Reason}. Choose another landing stage.";
                    }
                }
                log = string.Join("; ", lines);
                return null;
            }
            catch (Exception e)
            {
                log = $"{e.GetType().Name}: {e.Message}";
                return $"Cannot tell what dropping the lower stage would drop ({log}).";
            }
        }

        /// <summary>
        /// Every frame of a landing with a lower stage still to drop. throttle: the one applied,
        /// expectedAccel: the active engines' full-throttle acceleration, measuredAccel: the real
        /// one. Dropped: the stage left (the caller counts it). Failed: LastFailure says why,
        /// already logged; the caller stops the drops.
        /// </summary>
        public Outcome Update(VesselComponent vessel, double ut, double altitude, double surfaceSpeed, double dropHeight,
            double throttle, double expectedAccel, double measuredAccel)
        {
            if (vessel == null)
                return Outcome.None;
            if (activated)
                return Verify(vessel, ut);

            bool dryNow = DryStageTimer.IsDry(throttle, expectedAccel, measuredAccel);
            bool dry = timer.ShouldStage(ut, dryNow, deltaVLeft: true);
            DropReason reason = StagedBrakePlan.DropNow(true, dry, altitude, dropHeight);
            if (reason == DropReason.None)
                return Outcome.None;

            // The game's auto-staging is already staging: let it, the stage list will tell.
            if (StagingPilot.Instance != null && StagingPilot.Instance.is_staging)
                return Outcome.Waiting;

            GameInstance game = GameManager.Instance?.Game;
            if (game?.UniverseModel?.Time == null || game.UniverseModel.Time.IsPaused)
                return Outcome.Waiting;
            TimeWarp warp = game.ViewController?.TimeWarp;
            if (warp != null && warp.IsWarping && !warp.IsPhysicsTimeWarp)
            {
                // ActivateNextStage does nothing on rails.
                if (float.IsNaN(warp_exit_time))
                    warp_exit_time = Time.unscaledTime;
                TimeWarpTools.SetRateIndex(0, true);
                if (Time.unscaledTime - warp_exit_time > WarpExitSeconds)
                    return Failed($"the time warp did not stop within {WarpExitSeconds:n0} s");
                return Outcome.Waiting;
            }
            warp_exit_time = float.NaN;

            if (!ReadTree(vessel, out List<DropPart> parts, out Dictionary<string, string> names, out int next, out string why))
                return Failed($"cannot read the next stage ({why})");
            StageDropResult result = StageDrop.Decide(parts, next);
            landing.logger.LogInfo($"[Landing] stage drop: {Reason(reason)} at {altitude:n0} m (drop height {dropHeight:n0} m), " +
                $"{surfaceSpeed:n0} m/s; activation {next} drops {Describe(result, names)}");
            if (!result.MayStage)
                return Failed(result.NoStageLeft ? "no stage left to drop" : $"the next stage would drop {result.Reason}");

            parts_before = vessel.SimulationObject.PartOwner.PartCount;
            vessel.ActivateNextStage();
            timer.Staged(ut);
            activated = true;
            activated_ut = ut;
            pending = result;
            pending_reason = reason;
            pending_altitude = altitude;
            pending_speed = surfaceSpeed;
            landing.logger.LogInfo($"[Landing] stage drop: activation {next} fired at T{ut:F1}, {parts_before} part(s) before");
            return Outcome.Activated;
        }

        // The dropped parts have left the active vessel.
        Outcome Verify(VesselComponent vessel, double ut)
        {
            var ids = new HashSet<string>(vessel.SimulationObject.PartOwner.Parts.Where(p => p != null).Select(p => p.Guid));
            int left = pending.DroppedIds.Count(ids.Contains);
            if (left == 0)
            {
                activated = false;
                landing.logger.LogInfo($"[Landing] stage drop: done in {ut - activated_ut:F1} s, {parts_before} -> {ids.Count} part(s) " +
                    $"({pending.DroppedIds.Count} dropped)");
                return Outcome.Dropped;
            }
            if (ut - activated_ut <= VerifySeconds)
                return Outcome.Waiting;
            activated = false;
            return Failed($"{left} of {pending.DroppedIds.Count} part(s) still attached {VerifySeconds:n0} s after activation {pending.Stage} " +
                $"({parts_before} -> {ids.Count} part(s))");
        }

        /// <summary>
        /// Right after an activation the game may make the kept piece another vessel: the landing
        /// goes on when the active vessel holds every kept part.
        /// </summary>
        public bool KeepThroughVesselChange(VesselComponent active, double ut)
        {
            if (!activated || pending.KeptIds == null || ut - activated_ut > 2 * VerifySeconds)
                return false;
            var ids = active?.SimulationObject?.PartOwner?.Parts?.Where(p => p != null).Select(p => p.Guid).ToList();
            return ids != null && pending.KeptIds.All(ids.Contains);
        }

        Outcome Failed(string why)
        {
            LastFailure = why;
            landing.logger.LogWarning($"[Landing] stage drop failed: {why}");
            return Outcome.Failed;
        }

        static string Reason(DropReason reason) => reason == DropReason.Dry ? "stage dry" : "drop height reached";

        static string Describe(StageDropResult r, Dictionary<string, string> names)
        {
            string Name(string id) => names.TryGetValue(id, out string n) ? n : id;
            return (r.DroppedIds.Count == 0 ? "nothing" : string.Join(", ", r.DroppedIds.Select(Name))) +
                $" ({r.DroppedIds.Count} part(s)){(r.MayStage ? "" : $", refused: {r.Reason}")}";
        }

        // The part tree as StageDrop reads it, with the next activation's index.
        static bool ReadTree(VesselComponent vessel, out List<DropPart> parts, out Dictionary<string, string> names, out int next, out string why)
        {
            parts = null;
            names = new Dictionary<string, string>();
            next = -1;
            PartComponent control = vessel?.GetControlOwner();
            PartOwnerComponent owner = vessel?.SimulationObject?.PartOwner;
            StagingComponent staging = vessel?.SimulationObject?.Staging;
            if (owner == null || staging == null)
            {
                why = "the vessel's parts or stages are not available";
                return false;
            }
            List<CapsulePart> tree = ReentryContext.ReadParts(owner, new Dictionary<string, Data_Parachute>(), names,
                out List<PartComponent> components, out _);
            parts = new List<DropPart>(tree.Count);
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
                    IsLandingLeg = LandingGear.IsLeg(part, out _),
                });
            }
            int count = staging.StageCount;
            next = JettisonPlan.NextStage(count, count > 0 && staging.AvailableStages[count - 1].IsActive);
            why = null;
            return true;
        }
    }
}
