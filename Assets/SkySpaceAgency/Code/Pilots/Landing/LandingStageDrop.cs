using System;
using System.Collections.Generic;
using System.Linq;
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
    // A drop goes through these phases:
    //  - Cutting: the throttle is held at 0 (the pilot overrides the controller, HoldsThrottle)
    //    until the engines give no thrust, at least CutMinSeconds and at most CutMaxSeconds, as
    //    the simulation's gap without thrust. A booster that still has fuel then leaves at
    //    throttle 0 and cannot brake harder than the lander and be hit by it.
    //  - Verifying: the activation is fired; the game's staging must have moved on and the
    //    dropped parts left the active vessel within VerifySeconds (an activation that drops
    //    nothing passes on the staging alone). The throttle is still held at 0.
    //  - Igniting: the throttle is released, the controller relights. Within IgnitionSeconds the
    //    new stage must have active engines, pushing when throttled. If not, the next activation
    //    is fired once when StageDrop allows it (no legs, parachute, docking port or command part
    //    dropped or fired); otherwise the landing aborts with a message.
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c), see Jettison and BurnStaging:
    // - StagingComponent.ActivateNextStage fires StageCount-1 before the first staging and
    //   StageCount-2 after it (JettisonPlan.NextStage); PartComponent.ActivationStage is on that scale.
    // - It does nothing while the warp is on rails or the game is paused: the warp is stopped
    //   first, and nothing happens while paused.
    // - After a split the control owner's piece is the active vessel (JettisonPlan's header).
    public class LandingStageDrop
    {
        public const double CutMinSeconds = 0.1;
        public const double CutMaxSeconds = 0.5;
        public const double VerifySeconds = 3;
        public const double IgnitionSeconds = 1.5;
        public const float WarpExitSeconds = 10;
        // m/s²: measured acceleration under this counts as no thrust while cutting.
        const double QuietAccel = 0.02;

        public enum Outcome { None, Waiting, Activated, Dropped, Failed, Abort }
        enum Phase { Idle, Cutting, Verifying, Igniting }

        readonly LandingPilot landing;
        readonly DryStageTimer timer = new DryStageTimer();

        Phase phase = Phase.Idle;
        double phase_ut, activated_ut = double.NaN;
        float warp_exit_time = float.NaN;
        StageDropResult pending;
        int parts_before, count_before;
        bool last_active_before, extra_tried;
        DropReason pending_reason;
        double pending_altitude, pending_speed;

        public LandingStageDrop(LandingPilot landing)
        {
            this.landing = landing;
        }

        // The pilot holds the throttle at 0 while true (cut before the activation, then the gap).
        public bool HoldsThrottle => phase == Phase.Cutting || phase == Phase.Verifying;
        // A drop is under way (cut, activation or ignition check).
        public bool Busy => phase != Phase.Idle;
        public bool Activated => phase == Phase.Verifying || phase == Phase.Igniting;
        public DropReason PendingReason => pending_reason;
        public double PendingAltitude => pending_altitude;
        public double PendingSpeed => pending_speed;
        public string LastFailure { get; private set; }

        public void Reset()
        {
            timer.Reset();
            phase = Phase.Idle;
            activated_ut = double.NaN;
            warp_exit_time = float.NaN;
            pending = default;
            extra_tried = false;
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
        /// Every frame of a landing with a chosen stage while a lower stage is left or a drop is
        /// under way. throttle: the one applied, expectedAccel: the active engines' full-throttle
        /// acceleration, measuredAccel: the real one. Dropped: the stage left. Failed: the drops
        /// stop (LastFailure, logged), the landing goes on. Abort: the new stage gives no thrust,
        /// the landing must stop (LastFailure).
        /// </summary>
        public Outcome Update(VesselComponent vessel, double ut, bool lowerStageLeft, double altitude, double surfaceSpeed,
            double dropHeight, double throttle, double expectedAccel, double measuredAccel)
        {
            if (vessel == null)
                return Outcome.None;
            GameInstance game = GameManager.Instance?.Game;
            bool paused = game?.UniverseModel?.Time == null || game.UniverseModel.Time.IsPaused;
            switch (phase)
            {
                case Phase.Cutting:
                    return paused ? Outcome.Waiting : Cut(vessel, ut, expectedAccel, measuredAccel);
                case Phase.Verifying:
                    return Verify(vessel, ut);
                case Phase.Igniting:
                    return paused ? Outcome.Waiting : Ignite(vessel, ut, throttle, expectedAccel, measuredAccel);
            }

            if (!lowerStageLeft)
                return Outcome.None;
            bool dryNow = DryStageTimer.IsDry(throttle, expectedAccel, measuredAccel);
            bool dry = timer.ShouldStage(ut, dryNow, deltaVLeft: true);
            DropReason reason = StagedBrakePlan.DropNow(true, dry, altitude, dropHeight);
            if (reason == DropReason.None)
                return Outcome.None;

            if (paused)
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
                $"{surfaceSpeed:n0} m/s; activation {next} drops {Describe(result, names)}; cutting the throttle first");
            if (!result.MayStage)
                return Failed(result.NoStageLeft ? "no stage left to drop" : $"the next stage would drop {result.Reason}");

            pending = result;
            pending_reason = reason;
            pending_altitude = altitude;
            pending_speed = surfaceSpeed;
            phase = Phase.Cutting;
            phase_ut = ut;
            return Outcome.Waiting;
        }

        // Throttle held at 0: fire once the engines are quiet (or CutMaxSeconds have passed).
        Outcome Cut(VesselComponent vessel, double ut, double expectedAccel, double measuredAccel)
        {
            double measured = double.IsNaN(measuredAccel) ? 0 : measuredAccel;
            bool quiet = measured <= Math.Max(QuietAccel, DryStageTimer.DryShare * Math.Max(0, expectedAccel));
            double waited = ut - phase_ut;
            if (!(quiet && waited >= CutMinSeconds) && waited < CutMaxSeconds)
                return Outcome.Waiting;

            if (!ReadTree(vessel, out List<DropPart> parts, out _, out int next, out string why))
            {
                phase = Phase.Idle;
                return Failed($"cannot read the next stage ({why})");
            }
            if (next != pending.Stage)
            {
                phase = Phase.Idle;
                return Failed($"the stages changed while cutting the throttle (activation {pending.Stage} -> {next})");
            }
            StageDropResult again = StageDrop.Decide(parts, next);
            if (!again.MayStage)
            {
                phase = Phase.Idle;
                return Failed($"the next stage would drop {again.Reason}");
            }
            pending = again;
            Fire(vessel, ut, quiet ? null : $"engines still giving {measured:n2} m/s² after {waited:n1} s at throttle 0");
            phase = Phase.Verifying;
            return Outcome.Activated;
        }

        void Fire(VesselComponent vessel, double ut, string note)
        {
            StagingComponent staging = vessel.SimulationObject.Staging;
            count_before = staging.StageCount;
            last_active_before = count_before > 0 && staging.AvailableStages[count_before - 1].IsActive;
            parts_before = vessel.SimulationObject.PartOwner.PartCount;
            vessel.ActivateNextStage();
            timer.Staged(ut);
            phase_ut = ut;
            activated_ut = ut;
            landing.logger.LogInfo($"[Landing] stage drop: activation {pending.Stage} fired at T{ut:F1}, {parts_before} part(s), " +
                $"{count_before} stage(s) before" + (note != null ? $" ({note})" : ""));
        }

        // The staging moved on and the dropped parts left the active vessel.
        Outcome Verify(VesselComponent vessel, double ut)
        {
            StagingComponent staging = vessel.SimulationObject.Staging;
            int count = staging.StageCount;
            bool lastActive = count > 0 && staging.AvailableStages[count - 1].IsActive;
            bool staged = LandingStages.ActivationsDone(count_before, last_active_before, count, lastActive) > 0;
            var ids = new HashSet<string>(vessel.SimulationObject.PartOwner.Parts.Where(p => p != null).Select(p => p.Guid));
            int left = pending.DroppedIds.Count(ids.Contains);
            if (staged && left == 0)
            {
                landing.logger.LogInfo($"[Landing] stage drop: done in {ut - phase_ut:F1} s, {parts_before} -> {ids.Count} part(s) " +
                    $"({pending.DroppedIds.Count} dropped), {count_before} -> {count} stage(s); releasing the throttle");
                phase = Phase.Igniting;
                phase_ut = ut;
                extra_tried = false;
                return Outcome.Dropped;
            }
            if (ut - phase_ut <= VerifySeconds)
                return Outcome.Waiting;
            phase = Phase.Idle;
            return Failed($"{(staged ? "" : "the staging did not move on, ")}{left} of {pending.DroppedIds.Count} part(s) still attached " +
                $"{VerifySeconds:n0} s after activation {pending.Stage} ({parts_before} -> {ids.Count} part(s), {count_before} -> {count} stage(s))");
        }

        // After a drop: the new stage must have engines, pushing when throttled.
        Outcome Ignite(VesselComponent vessel, double ut, double throttle, double expectedAccel, double measuredAccel)
        {
            bool pushing = expectedAccel > 0 && !DryStageTimer.IsDry(throttle, expectedAccel, measuredAccel);
            if (pushing)
            {
                landing.logger.LogInfo($"[Landing] stage drop: the new stage pushes {ut - phase_ut:F1} s after the drop " +
                    $"(throttle {throttle * 100:n0} %, {measuredAccel:n2} of {expectedAccel:n2} m/s²)");
                phase = Phase.Idle;
                return Outcome.None;
            }
            if (ut - phase_ut < IgnitionSeconds)
                return Outcome.Waiting;

            string state = $"throttle {throttle * 100:n0} %, {measuredAccel:n2} of {expectedAccel:n2} m/s² " +
                $"({(expectedAccel > 0 ? "engines active but no thrust" : "no active engine")})";
            if (!extra_tried && ReadTree(vessel, out List<DropPart> parts, out Dictionary<string, string> names, out int next, out _))
            {
                StageDropResult r = StageDrop.Decide(parts, next);
                landing.logger.LogInfo($"[Landing] stage drop: no thrust {IgnitionSeconds:n1} s after the drop ({state}); " +
                    $"activation {next} would drop {Describe(r, names)}");
                if (r.MayStage)
                {
                    extra_tried = true;
                    pending = r;
                    Fire(vessel, ut, "to light the next engines");
                    return Outcome.Activated;
                }
            }
            phase = Phase.Idle;
            LastFailure = $"the stage after the drop gives no thrust ({state}). Take the controls";
            landing.logger.LogWarning($"[Landing] stage drop: abort: {LastFailure}");
            return Outcome.Abort;
        }

        /// <summary>
        /// Right after an activation the game may make the kept piece another vessel: the landing
        /// goes on when the active vessel holds every kept part.
        /// </summary>
        public bool KeepThroughVesselChange(VesselComponent active, double ut)
        {
            if (!Activated || pending.KeptIds == null || ut - activated_ut > 2 * VerifySeconds)
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
