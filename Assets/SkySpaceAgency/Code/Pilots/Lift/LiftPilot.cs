
using K2D2.KSPService;
using KTools;
using K2D2.Controller;
using K2D2.Node;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Lift
{
    public class LiftPilot : Pilot
    {
        public enum LiftStatus
        {
            Off,
            WaitLaunch,     // transfer goal only: planning, then waiting on the pad
            Ascent,
            Coasting,
            Adjust,
            Circularize,
            PlaneTrim,      // transfer goal only: ends the run itself (EndLiftPilot)
            Handoff         // rendezvous goal only: ends the run and starts the Rendezvous pilot
        }

        public ILogger logger = ModIdentity.Logger("Lift");

        public static LiftPilot Instance { get; set; }

        internal  LiftSettings settings;

        internal  LiftAscentPath ascent_path = null;

        KSPVessel current_vessel;

        Vector3d direction = Vector3d.zero;

        // sub pilots
        // Both ascent controllers are built up front (K2-D2 1.4.0); the profile picks which one
        // flies, and that choice is frozen for the run (run_ascent) so changing it mid-flight
        // can't swap the controller under the burn. Until the run starts, `ascent` is the selected
        // one (the tab's readouts).
        internal Ascent ascent_guided = null;
        ReactiveAscent ascent_gravity_turn = null;
        AscentControllerBase run_ascent = null;

        // The profile this run flies (frozen at Start): Gravity Turn for Orbit launches only.
        internal AscentProfile run_profile = AscentProfile.Guided;

        internal AscentControllerBase ascent => run_ascent ?? SelectedAscent();

        // Before a run: the controller the tab's goal and profile would fly.
        AscentControllerBase SelectedAscent() => ControllerFor(LiftGoalRules.ProfileFor(settings.goal.V, settings.ascent_profile.V));

        AscentControllerBase ControllerFor(AscentProfile profile) =>
            profile == AscentProfile.GravityTurn ? (AscentControllerBase)ascent_gravity_turn : ascent_guided;

        // Said while the run lasts when it flies another profile than the one selected.
        internal string start_note = null;

        Adjust adjust = null;
        Coasting coasting = null;   
        FinalCircularize final_circularize;
        WaitLaunch wait_launch;
        PlaneTrim plane_trim;
        RendezvousHandoff handoff;

        // The transfer goal's plan for this run (set by WaitLaunch); null for the Orbit goal, whose
        // run is today's, unchanged.
        internal LiftTransferPlan transfer = null;

        public ExecuteController current_subpilot = null;

        public LiftPilot()
        {
            settings = new LiftSettings();
            ascent_path = new LiftAscentPath(settings);
            _page = new LiftUI(this);

            current_vessel = K2D2_Plugin.Instance.current_vessel;
       
            ascent_guided = new Ascent(settings, ascent_path);
            ascent_gravity_turn = new ReactiveAscent(settings);
            adjust = new Adjust(settings, this);
            coasting = new Coasting(this, settings);
            final_circularize = new FinalCircularize(this, settings);
            wait_launch = new WaitLaunch(this, settings);
            plane_trim = new PlaneTrim(this);
            handoff = new RendezvousHandoff(this);
        
            Instance = this;

            debug_mode_only = false;

            K2D2PilotsMgr.Instance.RegisterPilot("Lift", this);
        }

        // The cause of a reset that stops the run (K2D2_Plugin.ResetControllers); null for Stop.
        ResetCause? stop_cause;

        public override void onReset()
        {
            stop_cause = K2D2_Plugin.CurrentResetCause;
            try
            {
                isRunning = false;
            }
            finally
            {
                stop_cause = null; // a reset of a stopped run must not leak into the next Stop
            }
        }

        LiftStatus _status = LiftStatus.Off;
        internal LiftStatus status
        {
            get { return _status; }
            set
            {
                if (_status == value)
                    return;
                LiftStatus previous = _status;
                _status = value;
                // The roll program can leave SAS in locked-rotation mode at the end of the ascent;
                // the prograde holds that follow need it off (K2-D2 1.4.0).
                if (previous == LiftStatus.Ascent)
                    ReleaseRollLock();
                switch (value)
                {
                    case LiftStatus.Off:
                    {
                        // stop
                        if (current_vessel != null)
                        {
                            current_vessel.SetThrottle(0);
                            // Roll program (see Ascent.cs) drives this as a raw control axis input,
                            // not through SAS - if the pilot gets stopped mid-correction, release it
                            // here too so a stray roll command doesn't keep firing after the
                            // autopilot itself has stopped.
                            current_vessel.Roll = 0;
                        }

                        current_subpilot = null;
                        run_ascent = null;

                        // Transfer and rendezvous goals: no plan search left running, and no warp
                        // left on by the pad wait, the trim or the hand-over.
                        wait_launch.Cancel();
                        settings.SetApOverride(-1);
                        if (previous == LiftStatus.WaitLaunch || previous == LiftStatus.PlaneTrim || previous == LiftStatus.Handoff)
                            TimeWarpTools.SetRateIndex(0, false);

                        // Rendezvous goal stopped from outside (Stop, another pilot, scene or vessel
                        // change, mod off): say which step, why, and what is left. EndLiftPilot and
                        // the hand-over write their own.
                        if (!ending && transfer != null && transfer.Goal == LiftGoal.Rendezvous && previous != LiftStatus.Off)
                        {
                            string reason = LiftGoalRules.StopReason(stop_cause);
                            result_ok = false;
                            end_status = LiftGoalRules.ChainStopped(previous, transfer.TargetName, reason);
                            logger.LogInfo($"[Lift] rendezvous stopped during {LiftGoalRules.StepName(previous)}: {reason}");
                        }
                        stop_cause = null;
                    }
                        break;
                    case LiftStatus.WaitLaunch:
                        current_subpilot = wait_launch;
                        break;
                    case LiftStatus.PlaneTrim:
                        current_subpilot = plane_trim;
                        break;
                    case LiftStatus.Handoff:
                        current_subpilot = handoff;
                        break;
                    case LiftStatus.Ascent:
                        current_subpilot = ascent;
                        break;
                    case LiftStatus.Coasting:
                        current_subpilot = coasting;
                        break;
                    case LiftStatus.Adjust:
                        current_subpilot = adjust;
                        break;
                    case LiftStatus.Circularize:
                        current_subpilot = final_circularize;
                        break;
                }

                if (current_subpilot != null)
                    current_subpilot.Start();
            }
        }

        public override bool isRunning
        {
            get { return _status != LiftStatus.Off; }
            set
            {
                if (value == isRunning)
                    return;

                if (!value)
                {
                    status = LiftStatus.Off;
                }
                else
                {
                    // reset controller to desactivate other controllers.
                    K2D2_Plugin.ResetControllers();
                    OnStartController();
                }

                // send call backs
                base.isRunning = value; 
            }
        }

        void OnStartController()
        {
            transfer = null;
            ascent_guided.transfer_plan = null;
            LiftGoal goal = settings.goal.V;
            run_profile = LiftGoalRules.ProfileFor(goal, settings.ascent_profile.V);
            run_ascent = ControllerFor(run_profile);
            start_note = null;
            if (run_profile != settings.ascent_profile.V)
            {
                start_note = LiftGoalRules.GravityTurnOrbitOnly;
                logger.LogInfo($"[Lift] {LiftGoalRules.GravityTurnOrbitOnly} (goal {goal})");
            }
            else
                logger.LogInfo($"[Lift] start: goal {goal}, ascent profile {run_profile}");
            settings.SetApOverride(-1);
            if (settings.goal.V == LiftGoal.PrepareTransfer || settings.goal.V == LiftGoal.Rendezvous)
                status = LiftStatus.WaitLaunch;
            else
                status = LiftStatus.Ascent;
        }

        // Transfer goal, during the ascent: the liftoff time and place, or no liftoff 5 s after the
        // launch time (throttle cut, back to the pad wait for the next pass). True when the status
        // changed.
        bool CheckLiftoff()
        {
            var p = transfer;
            var vessel = current_vessel.VesselComponent;
            if (p == null || !p.Ready || status != LiftStatus.Ascent || !double.IsNaN(p.LiftoffUT) || vessel == null)
                return false;
            double now = GeneralTools.Current_UT;
            if (!vessel.LandedOrSplashed)
            {
                p.LiftoffUT = now;
                p.LiftoffPosition = vessel.Orbit.GetRelativePositionAtUTZup(now);
                logger.LogInfo(System.FormattableString.Invariant(
                    $"[Lift] liftoff at UT {now:F1}, planned UT {p.Window.LaunchUT:F1} (T{now - p.Window.LaunchUT:+0.0;-0.0;+0.0} s), first plan UT {p.PlannedLaunchUT:F1}"));
                return false;
            }
            if (LiftGoalRules.Decide(now, p.Window.LaunchUT, true) != PadAction.Resolve)
                return false;
            logger.LogInfo("[Lift] still on the pad 5 s after the launch time: throttle cut");
            current_vessel.SetThrottle(0);
            status = LiftStatus.WaitLaunch;
            return true;
        }

        // Transfer goal, at the end of the ascent burn: the ascent lead and downrange the launch
        // window will use next time (LiftGoalRules.AscentLead).
        void MeasureAscent()
        {
            var p = transfer;
            var vessel = current_vessel.VesselComponent;
            if (p == null || double.IsNaN(p.LiftoffUT) || vessel == null)
                return;
            if (p.EarlyLiftoff)
            {
                logger.LogInfo("[Lift] ascent lead not measured: the vessel lifted off before the launch time");
                return;
            }
            double now = GeneralTools.Current_UT;
            double dt = now - p.LiftoffUT;
            double downrange = LiftGoalRules.DownrangeRad(p.LiftoffPosition, vessel.Orbit.GetRelativePositionAtUTZup(now), p.Omega, dt);
            double downrangeDeg = downrange * 180 / System.Math.PI;
            logger.LogInfo(System.FormattableString.Invariant(
                $"[Lift] ascent lead measured: {dt:F1} s, downrange {downrangeDeg:F2}° on {p.BodyName} ") +
                System.FormattableString.Invariant(
                $"(the window used {p.LeadSeconds:F0} s, {p.DownrangeRad * 180 / System.Math.PI:F2}°, {(p.LeadMeasured ? "measured" : "estimate")})"));
            var stored = settings.MeasuredLead(p.BodyName, run_profile);
            if (LiftGoalRules.UpdatedLead(stored.s.V, stored.deg.V, dt, downrangeDeg, out double s, out double deg))
            {
                stored.s.V = (float)s;
                stored.deg.V = (float)deg;
                logger.LogInfo(System.FormattableString.Invariant($"[Lift] ascent lead stored for {p.BodyName} ({run_profile}): {s:F1} s, {deg:F2}° (clamped, smoothed)"));
            }
        }

        internal bool result_ok = false;
        internal string end_status;

        // True while the run ends through EndLiftPilot or the hand-over, which write their own end text.
        bool ending;

        // planRefused: the rendezvous planner refused (target or plan) on the pad: nothing was
        // launched, and pressing Start again would give the same answer.
        public void EndLiftPilot(bool result_ok, string end_status, bool planRefused = false)
        {
            // Rendezvous goal: a stop in the chain says the step, the reason and what is left (6b).
            if (!result_ok && transfer != null && transfer.Goal == LiftGoal.Rendezvous && status != LiftStatus.Off)
            {
                logger.LogInfo($"[Lift] rendezvous stopped during {LiftGoalRules.StepName(status)}: {end_status}");
                end_status = planRefused
                    ? LiftGoalRules.PlanRefused(transfer.TargetName, end_status)
                    : LiftGoalRules.ChainStopped(status, transfer.TargetName, end_status);
            }
            this.result_ok = result_ok;
            this.end_status = end_status;
            // Was: status = LiftStatus.Off directly. That flips the getter's answer
            // (isRunning reads _status != Off) but skips the isRunning SETTER entirely, so
            // base.isRunning's own backing field never gets updated and is_running_event never
            // fires. Node/Landing end their run via isRunning = false (see NodeExPilot.Stop(),
            // LandingPilot's touchdown/abort paths) for exactly this reason - anything bound to
            // is_running_event (LiftUI's run_button toggle state, K2's avatar SetRunning) needs
            // that callback to update, or it stays stuck showing "running" after the pilot has
            // actually finished on its own (as opposed to being stopped via the Brake/Start
            // button, which already goes through this setter).
            ending = true;
            try
            {
                isRunning = false;
            }
            finally
            {
                ending = false;
            }
        }

        // Rendezvous goal: a stop in the chain because of the target (gone, landed, elsewhere).
        internal void StopRendezvousChain(string why, bool targetLost)
        {
            logger.LogInfo($"[Lift] rendezvous stopped during {LiftGoalRules.StepName(status)}: {why}");
            result_ok = false;
            end_status = LiftGoalRules.ChainStopped(status, transfer?.TargetName ?? "the target", why, targetLost);
            ending = true;
            try
            {
                isRunning = false;
            }
            finally
            {
                ending = false;
            }
        }

        // Rendezvous goal, in the parking orbit: ends the Lift run (throttle 0, warp 1x), then starts
        // the Rendezvous pilot on the target. Lift ends first: RendezvousPilot.Start resets every
        // pilot, which must not reset a Lift run from inside its own update.
        internal void ChainToRendezvous()
        {
            var p = transfer;
            string target = p?.TargetName ?? "the target";
            bool dock = p != null && p.Dock;
            string id = p?.TargetId;
            ending = true;
            try
            {
                isRunning = false;
            }
            finally
            {
                ending = false;
            }
            var rv = K2D2.Rendezvous.RendezvousPilot.Instance;
            bool started = false;
            string why;
            if (rv == null)
                why = "the Rendezvous pilot is not loaded";
            else if (string.IsNullOrEmpty(id))
                why = "No target vessel.";
            else
            {
                started = rv.Start(dock ? K2D2.Rendezvous.RvMode.RendezvousAndDock : K2D2.Rendezvous.RvMode.RendezvousOnly, id, false);
                why = rv.Status;
            }
            logger.LogInfo("[Lift] rendezvous: hand-over " + (started ? "started" : "refused: " + why));
            result_ok = LiftGoalRules.HandoffMessage(started, target, dock, why, out end_status);
        }

        public WarpTo warp_to = new WarpTo();


        void ReleaseRollLock()
        {
            var sas = current_vessel?.Autopilot?.SAS;
            if (sas != null)
                sas.lockedMode = false;
        }

        public void NextMode()
        {
            LiftGoal goal = transfer?.Goal ?? settings.goal.V;
            // Without auto-circularize (Orbit goal only) the run ends once apoapsis is final, after
            // Adjust, and leaves the circularization to the player, optionally paused (K2-D2 1.4.0).
            if (status == LiftStatus.Adjust && LiftGoalRules.EndsAfterAdjust(goal, settings.auto_circularize.V))
            {
                logger.LogInfo($"[Lift] ascent complete without auto-circularize (pause {(settings.pause_on_final.V ? "on" : "off")})");
                if (settings.pause_on_final.V)
                {
                    TimeWarpTools.SetRateIndex(0, false);
                    TimeWarpTools.SetIsPaused(true);
                }
                EndLiftPilot(true, "Ascent complete - plan your circularization at apoapsis");
                return;
            }
            status = LiftGoalRules.NextStatus(status, goal, settings.auto_circularize.V);
        }

        public override void Update()
        {
        
            if (current_vessel == null) return;

            if (isRunning)
                ascent.computeValues(status == LiftStatus.Ascent);
            else if (page.isVisible)
                ascent.computeIdleValues(current_vessel);

            if (!isRunning)
                return;

            if (transfer != null && CheckLiftoff())
                return;

            var subpilot = current_subpilot;
            if (subpilot != null)
            {
                LiftStatus before = status;
                subpilot.Update();

                // A subpilot's Update() can end the whole run right here - Coasting's Ap-under-
                // atmosphere-limit check and FinalCircularize's burn-complete case (see Final.cs)
                // both call lift.EndLiftPilot() directly rather than just setting finished, so
                // that is_running_event actually fires (see EndLiftPilot's own comment about why
                // it goes through the isRunning setter instead of setting status directly). That
                // synchronously nulls current_subpilot via the status setter's Off case, before
                // we get back here. Re-checking the field itself (not the stale local) means we
                // don't NullReferenceException on a null current_subpilot, and don't call
                // NextMode() a second time on top of a run that already ended itself.
                if (current_subpilot == subpilot && subpilot.finished)
                {
                    // Orbit and Prepare transfer: the next status, as always; the rendezvous goal
                    // hands over after the circularization (LiftGoalRules.NextStatus).
                    NextMode();
                    if (transfer != null && before == LiftStatus.Ascent)
                        MeasureAscent();
                }
            }
        }
    }
}
