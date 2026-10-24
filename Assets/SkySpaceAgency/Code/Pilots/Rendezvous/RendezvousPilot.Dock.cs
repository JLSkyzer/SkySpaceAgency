using System;
using System.Collections.Generic;
using KSP.Api;
using KSP.Sim;
using KSP.Sim.impl;
using UnityEngine;

namespace K2D2.Rendezvous
{
    // The docking: the port pair, "control from here" and the target port, RCS on, the thruster
    // probe (Translate), then DockingGuidance every frame until the game latches the ports. Always
    // flown (never Prepare only); never stages; the target is never acted on.
    public partial class RendezvousPilot
    {
        enum DockStage { None, RcsSettle, Probe, Approach }

        // ---------------------------------------------------------------- what the tab reads

        public DockPhase DockPhase => guidance?.Phase ?? DockPhase.Align;
        public bool Docking => Phase == RvPhase.Dock && dock_stage != DockStage.None;
        public string TranslateNote { get; private set; }
        public TranslateMode TranslateMode => translate.Mode;
        // The last docking trace: distance to the port face, lateral error, closing speed.
        public double DockDistance { get; private set; } = double.NaN;
        public double DockLateral { get; private set; } = double.NaN;
        public double DockClosing { get; private set; } = double.NaN;
        public string OurPortName => dock_our_port?.Part?.PartName;
        public string TargetPortName => dock_target_port?.Part?.PartName;

        // ---------------------------------------------------------------- state

        readonly Translate translate = new Translate();
        DockStage dock_stage;
        DockingGuidance guidance;
        PortRead dock_our_port, dock_target_port;
        HashSet<string> dock_target_parts = new HashSet<string>();
        bool dock_touched;          // control, target or RCS changed: restore them at the end
        bool rcs_present;
        float rcs_settle_until;
        double last_dock_ut = double.NaN, next_trace_ut = double.NaN, contact_since = double.NaN;
        float frames_unread_since = float.NaN;   // real time; NaN while the port frames read
        int aborts_seen;

        void ResetDock()
        {
            dock_stage = DockStage.None;
            guidance = null;
            dock_our_port = dock_target_port = null;
            dock_target_parts = new HashSet<string>();
            dock_touched = false;
            last_dock_ut = next_trace_ut = contact_since = double.NaN;
            frames_unread_since = float.NaN;
            aborts_seen = 0;
            TranslateNote = null;
            DockDistance = DockLateral = DockClosing = double.NaN;
        }

        // Port pair, refusals, control from here, target port, RCS on.
        void BeginDock()
        {
            SetPhase(RvPhase.Dock);
            var ours = ActiveVessel();
            var target = RendezvousSnapshot.FindVessel(target_id);
            if (target == null)
            {
                Fail("the target vessel no longer exists");
                return;
            }
            if (!DockingParts.IsLoaded(target) || !DockingParts.IsLoaded(ours))
            {
                Fail("the target is out of physics range: get closer (rendezvous first)");
                return;
            }
            if (!RendezvousSnapshot.TryRead(ours, target, out var s, out string error))
            {
                Fail(error);
                return;
            }
            var ourPorts = DockingParts.Ports(ours);
            var theirPorts = DockingParts.Ports(target);
            PortRead mine, theirs;
            if (our_port_pick != null || target_port_pick != null)
            {
                mine = DockingParts.Find(ourPorts, our_port_pick);
                theirs = DockingParts.Find(theirPorts, target_port_pick);
                string bad = mine == null || theirs == null ? "the chosen pair of ports is not on the vessels any more"
                    : !mine.Info.Free || !theirs.Info.Free ? "a chosen port is not free"
                    : null;
                if (bad != null)
                {
                    Fail(bad);
                    return;
                }
            }
            else
            {
                Vector3d forward = OurForward(ours);
                var (o, t, reason) = DockingParts.Choose(ourPorts, theirPorts, forward, s.OurR - s.TargetR);
                if (reason != null)
                {
                    Fail("no docking pair: " + reason);
                    return;
                }
                mine = o;
                theirs = t;
            }
            if (!theirs.FrameRead || !mine.FrameRead)
            {
                Fail("the ports' docking transforms cannot be read (both vessels must be loaded)");
                return;
            }
            string spin = DockingReads.SpinRefusal(s.TargetOmega, theirs.Info.Axis);
            if (spin != null)
            {
                Fail(spin);
                return;
            }
            dock_our_port = mine;
            dock_target_port = theirs;
            dock_target_parts = new HashSet<string>(PartIds(target));
            logger.LogInfo($"[Docking] pair: our {mine.Part.PartName} ({mine.Info.PartId}) -> their {theirs.Part.PartName} ({theirs.Info.PartId}), " +
                $"{(our_port_pick != null ? "picked by the player" : "automatic")}; {(s.TargetR - s.OurR).magnitude:F0} m apart, " +
                $"target spin {DockingReads.SpinAboutAxisDegPerSec(s.TargetOmega, theirs.Info.Axis):F2}°/s about its port axis, {dock_target_parts.Count} target part(s)");

            dock_touched = true;
            DockingParts.ControlFromHere(ours, mine);
            RendezvousSnapshot.SetTarget(ours, theirs.Part.GlobalId, $"the port {theirs.Part.PartName}");
            RendezvousSnapshot.WatchDocking();
            var rcs = DockingParts.Thrusters(ours);
            rcs_present = rcs.Total > 0;
            if (rcs_present)
                DockingParts.SetRcs(ours, true);
            rcs_settle_until = Time.realtimeSinceStartup + 1;
            dock_stage = DockStage.RcsSettle;
            Status = "Docking: preparing the thrusters...";
        }

        void UpdateDock()
        {
            switch (dock_stage)
            {
                case DockStage.RcsSettle:
                {
                    if (Time.realtimeSinceStartup < rcs_settle_until)
                        return;
                    var rcs = DockingParts.Thrusters(ActiveVessel());
                    bool usable = rcs_present && rcs.Usable > 0;
                    logger.LogInfo($"[Docking] RCS: {rcs}, {(usable ? "usable" : "not usable")}");
                    translate.StartProbe(usable);
                    dock_stage = DockStage.Probe;
                    return;
                }
                case DockStage.Probe:
                case DockStage.Approach:
                    break;
                default:
                    return;
            }

            var ours = ActiveVessel();
            var target = RendezvousSnapshot.FindVessel(target_id);
            if (!RendezvousSnapshot.TryDockState(ours, dock_our_port, target, dock_target_port, out var state, out string error))
            {
                if (target == null || !DockingParts.IsLoaded(target))
                {
                    Fail("the target left physics range during the docking");
                    return;
                }
                translate.Update();   // no input this frame: it zeroes the inputs
                if (float.IsNaN(frames_unread_since))
                    frames_unread_since = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - frames_unread_since > RendezvousFlight.FrameWaitSeconds)
                {
                    Fail($"the port frames could not be read for {RendezvousFlight.FrameWaitSeconds:F0} s ({error})");
                    return;
                }
                Status = "Docking: waiting for the port frames (" + error + ")";
                return;
            }
            frames_unread_since = float.NaN;
            double dt = double.IsNaN(last_dock_ut) ? 0 : state.Time - last_dock_ut;
            last_dock_ut = state.Time;

            if (dock_stage == DockStage.Probe)
            {
                translate.SetInput(state, default);
                translate.Update();
                if (translate.failure_reason != null)
                {
                    Fail("docking refused: " + translate.failure_reason);
                    return;
                }
                Status = "Docking: " + translate.status_line;
                if (!translate.Ready)
                    return;
                bool rcsLimits = translate.Mode != TranslateMode.MainEngine;
                var limits = DockingParts.Limits(dock_our_port, dock_target_port, rcsLimits, DockingParts.TargetRadius(target));
                limits = translate.Widen(limits);
                if (translate.failure_reason != null)
                {
                    // An engine too coarse for the port's contact gate (Task 5 fix).
                    Fail("docking refused: " + translate.failure_reason);
                    return;
                }
                guidance = new DockingGuidance(limits);
                TranslateNote = translate.ModeNote;
                logger.LogInfo($"[Docking] approach: mode {translate.Mode}{(TranslateNote != null ? $" ({TranslateNote})" : "")}, holds {limits.HoldFar:F0} m / {limits.HoldNear:F0} m, " +
                    $"final {limits.FinalSpeedMin:F2}-{limits.FinalSpeedMax:F2} m/s, capture {limits.CaptureMaxRvel:F2} m/s, fwd dot {limits.CaptureMinFwdDot:F3}, " +
                    $"roll dot {limits.MinRollDot:F3}, contact lateral {limits.ContactLateral:F2} m, keep-out {limits.TargetRadius + DockLimits.KeepOutMargin:F0} m");
                dock_stage = DockStage.Approach;
                next_trace_ut = state.Time;
                return;
            }

            // Approach. The target's turn reaches Translate through state.TargetAxisRate.
            var cmd = guidance.Step(state, dt);
            translate.SetInput(state, cmd);
            translate.Update();

            Vector3d a = state.TargetAxis.normalized;
            double ax = Vector3d.Dot(state.RelPos, a);
            DockDistance = state.RelPos.magnitude;
            DockLateral = (state.RelPos - ax * a).magnitude;
            DockClosing = -Vector3d.Dot(state.RelVel, a);
            if (guidance.Phase == DockPhase.Docked)
            {
                if (double.IsNaN(contact_since)) contact_since = state.Time;
            }
            else
                contact_since = double.NaN;

            if (state.Time >= next_trace_ut)
            {
                next_trace_ut = state.Time + RendezvousFlight.TraceSeconds;
                logger.LogInfo($"[Docking] trace: {guidance.Phase}, distance {DockDistance:F2} m (axial {ax:F2}), lateral {DockLateral:F2} m, " +
                    $"closing {DockClosing:F3} m/s, wanted ({cmd.WantedRelVel.x:F3}, {cmd.WantedRelVel.y:F3}, {cmd.WantedRelVel.z:F3}), " +
                    $"mode {translate.Mode}{(translate.UsesEngine ? $" {translate.EnginePhase}" : "")}, aborts {guidance.Aborts}");
                string spin = RendezvousSnapshot.TryRead(ours, target, out var s, out _) ? DockingReads.SpinRefusal(s.TargetOmega, state.TargetAxis) : null;
                if (spin != null)
                {
                    Fail(spin);
                    return;
                }
            }

            guidance.AbortNeeded(out string abortReason);
            var outcome = RendezvousFlight.JudgeDock(guidance.Phase, guidance.Aborts, aborts_seen, guidance.Phase == DockPhase.Aborted,
                abortReason ?? guidance.LastAbortReason, RendezvousSnapshot.DockedSeen, DockingParts.IsDocked(dock_our_port),
                double.IsNaN(contact_since) ? 0 : state.Time - contact_since, translate.failure_reason, out string why);
            aborts_seen = guidance.Aborts;
            switch (outcome)
            {
                case DockOutcome.Docked:
                    FinishDocked(why);
                    return;
                case DockOutcome.Failed:
                    Fail("docking: " + why);
                    return;
                case DockOutcome.Retry:
                    logger.LogInfo($"[Docking] {why}");
                    break;
            }
            Status = $"Docking: {guidance.Phase}, {DockDistance:F1} m, lateral {DockLateral:F2} m, closing {DockClosing:F2} m/s";
        }

        // The nose of our vessel, celestial Zup (the port chooser's tie-break).
        static Vector3d OurForward(VesselComponent vessel)
        {
            try
            {
                ICoordinateSystem celestial = vessel.Orbit.referenceBody.SimulationObject.transform.celestialFrame;
                Rotation now = Rotation.Reframed(K2D2_Plugin.Instance.current_vessel.GetRotation(), celestial);
                return DockingReads.ZupFromYup(now.localRotation * new Vector3d(0, 1, 0));
            }
            catch (Exception)
            {
                return Vector3d.zero;
            }
        }

        // Translation off, SAS back to Stability Assist, RCS and the control part as they were, all on
        // our own vessel (found by its id): after a switch the active vessel is another one, maybe
        // the target, which is never acted on. Its SAS and inputs are left alone then.
        void StopDocking()
        {
            if (dock_stage == DockStage.None && !dock_touched)
                return;
            bool oursActive = OursIsActive();
            if (oursActive)
                translate.Stop();
            else
                logger.LogInfo("[Docking] stopped while another vessel is active: its inputs and SAS are left alone");
            if (dock_touched)
            {
                var ours = OurVessel();
                if (oursActive)
                    SetStabilityAssist();
                if (ours == null)
                    logger.LogInfo("[Docking] our vessel is gone: RCS and control part not restored");
                DockingParts.RestoreRcs(ours);
                DockingParts.RestoreControl(ours);
                RendezvousSnapshot.UnwatchDocking();
            }
            dock_touched = false;
            dock_stage = DockStage.None;
        }

        void SetStabilityAssist()
        {
            try
            {
                SASTool.setAutoPilot(AutopilotMode.StabilityAssist);
            }
            catch (Exception e)
            {
                logger.LogInfo($"[Docking] SAS not set to Stability Assist: {e.GetType().Name}");
            }
        }

        // The merged vessel is active. Our control part comes back on it; its RCS is left as it is:
        // our old state was one vessel's, the merged vessel has the target's thrusters too.
        void FinishDocked(string why)
        {
            var merged = ActiveVessel();
            logger.LogInfo($"[Docking] docked ({why}): {merged?.Name ?? "?"}, {PartIds(merged).Count} parts, vessel {IdOf(merged)}" +
                (guidance != null ? $", {guidance.Aborts} abort(s), last trace {DockDistance:F2} m, lateral {DockLateral:F2} m, closing {DockClosing:F3} m/s" : ""));
            translate.Stop();
            if (dock_touched)
            {
                SetStabilityAssist();
                DockingParts.RestoreControl(merged);
                logger.LogInfo($"[Docking] RCS left as it is on the merged vessel ({(merged == null ? "unknown" : DockingParts.RcsState(merged).ToString())})");
                DockingParts.BeginRun();   // drops the recorded RCS state: never applied to the merged vessel
                RendezvousSnapshot.UnwatchDocking();
            }
            dock_touched = false;
            dock_stage = DockStage.None;
            DeleteSaved(vessel_id);
            Phase = RvPhase.Docked;
            isRunning = false;
            checked_vessel = null;
            Status = "Docked.";
        }
    }
}
