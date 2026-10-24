using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Node;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Rendezvous
{
    /// <summary>
    /// Game glue of the rendezvous and docking (sub-project 4.3). Plans with RendezvousPlanner on a
    /// worker thread, flies the plan's burns with TargetOrbitBurn (closed loop, the 4.1 staging
    /// guard), solves the correction with a fresh Lambert from the actual state, then docks with
    /// DockingGuidance and Translate (RendezvousPilot.Dock.cs). The decisions are pure
    /// (RendezvousFlight); this class reads the game on the main thread only.
    ///
    /// Prepare only makes one game node at a time for the rendezvous burns and never warps; the
    /// docking is always flown. The run is saved per vessel (&lt;mod&gt;/plans/rendezvous_&lt;GUID&gt;.json)
    /// at every phase change; Resume always plans again from the actual states.
    ///
    /// Every stop leaves the throttle at 0, the warp off, the RCS and the control part as they were.
    /// </summary>
    public partial class RendezvousPilot : Pilot
    {
        public static RendezvousPilot Instance { get; private set; }

        internal readonly ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Rendezvous");

        // ---------------------------------------------------------------- what the tab reads

        public RvPhase Phase { get; private set; } = RvPhase.Plan;
        public RvMode Mode { get; private set; }
        public RvPlan Current { get; private set; }
        public int StepIndex { get; private set; }
        public bool PrepareOnly { get; private set; }
        public string Status { get; private set; } = "";
        public string TargetName { get; private set; }
        public bool Planning => job.Running;
        public BudgetResult Budget { get; private set; }
        public IReadOnlyList<StageDv> Stages => stages;
        // "Resume the rendezvous with Station (was: PhasingWait)", or null.
        public string ResumeOffer { get; private set; }
        public bool HasSavedPlan => saved != null || saved_error != null;
        public bool CanResume => !isRunning && !job.Running && saved != null && saved_target_ok;

        // ---------------------------------------------------------------- state

        string vessel_id, target_id, our_port_pick, target_port_pick;
        string body_name;
        bool active, starting;
        int replans;
        List<StageDv> stages = new List<StageDv>();
        double standoff = 200;

        SavedPlan saved;
        string saved_error;
        bool saved_target_ok;
        string checked_vessel;
        float next_offer_check;

        public RendezvousPilot()
        {
            Instance = this;
            debug_mode_only = false;
            K2D2PilotsMgr.Instance.RegisterPilot("Rendezvous", this);
            node = new PrepareOnlyNode(logger, "[Rendezvous]");
            _page = new RendezvousUI(this);   // the Rendezvous tab, in the old Dock tab's slot
            burn.stage_when_dry = true;
        }

        static string PlansFolder => K2D2_Plugin.PlansFolder;

        // ---------------------------------------------------------------- running

        public override bool isRunning
        {
            get => active;
            set
            {
                if (value == active)
                    return;
                if (!value)
                {
                    StopBurn();
                    CancelJob();
                    StopWarp();
                    StopDocking();
                    node.StopWaiting();
                }
                active = value;
                base.isRunning = value;
            }
        }

        public override void onReset()
        {
            if (starting)
                return;
            // A docking sends a vessel change (the merged vessel) before VesselDockedMessage, and a
            // staging during a burn may send one too: the pilot lives through both.
            if (Landing.LandingPilot.reset_by_vessel_change && isRunning)
            {
                var outcome = CheckVessel("vessel change message");
                if (outcome == RvVesselOutcome.Docked)
                {
                    FinishDocked("the vessel change merged both vessels");
                    return;
                }
                if (outcome != RvVesselOutcome.Changed)
                    return;
            }
            if (isRunning)
            {
                Save();
                isRunning = false;
                Status = "Stopped by a vessel or scene change. Resume plans again from the actual state.";
                logger.LogInfo("[Rendezvous] stopped by a reset (vessel change, scene change or another pilot)");
            }
            CancelJob();
            checked_vessel = null;
        }

        public override void Update()
        {
            PollJob();
            if (!isRunning)
            {
                RefreshOffer();
                return;
            }
            try
            {
                Fly();
            }
            catch (Exception e)
            {
                logger.LogError($"[Rendezvous] {e}");
                Fail($"internal error ({e.GetType().Name}: {e.Message})");
            }
        }

        // ---------------------------------------------------------------- API

        /// <summary>
        /// Starts a rendezvous with the target vessel (its GUID; null: the game's target), then docks
        /// unless the mode is RendezvousOnly. ourPortId / targetPortId: the pair the player picked,
        /// null for the automatic choice. prepareOnly: one node at a time for the rendezvous burns.
        /// </summary>
        public bool Start(RvMode mode, string targetId, bool prepareOnly, string ourPortId = null, string targetPortId = null)
        {
            if (isRunning || job.Running)
            {
                Status = "Stop the rendezvous first.";
                return false;
            }
            var ours = ActiveVessel();
            var target = targetId != null ? RendezvousSnapshot.FindVessel(targetId) : RendezvousSnapshot.TargetVessel(ours);
            string readError = null;
            double distance = double.NaN;
            bool isOurs = target != null && ours != null && target.GlobalId.Equals(ours.GlobalId);
            if (target != null && !isOurs)
            {
                if (RendezvousSnapshot.TryRead(ours, target, out var s, out readError))
                    distance = (s.TargetR - s.OurR).magnitude;
            }
            string refusal = RendezvousFlight.StartRefusal(mode, target != null, isOurs, readError, distance);
            if (refusal != null)
            {
                Status = refusal;
                logger.LogInfo($"[Rendezvous] start refused: {refusal}");
                return false;
            }
            vessel_id = IdOf(ours);
            if (vessel_id == null)
            {
                Status = "Cannot start: the vessel has no id.";
                return false;
            }
            target_id = target.GlobalId.Guid.ToString("D");
            TargetName = target.Name;
            our_port_pick = ourPortId;
            target_port_pick = targetPortId;
            body_name = ours.Orbit?.referenceBody?.bodyName;
            Begin(mode, prepareOnly, distance, "start");
            return true;
        }

        public void Stop()
        {
            if (!isRunning)
            {
                CancelJob();
                return;
            }
            Save();
            isRunning = false;
            checked_vessel = null;
            Status = "Stopped. Resume plans again from the actual state.";
            logger.LogInfo($"[Rendezvous] stopped by the player in {Phase}, step {StepIndex}");
        }

        /// <summary>Resumes the active vessel's saved rendezvous: always planned again.</summary>
        public void Resume()
        {
            if (!CanResume)
                return;
            var r = saved.Rendezvous;
            RendezvousFlight.TryParse(r.Mode, out RvMode mode);
            RendezvousFlight.TryParse(r.Phase, out RvPhase phase);
            var ours = ActiveVessel();
            var target = RendezvousSnapshot.FindVessel(r.TargetId);
            double distance = double.NaN;
            if (target != null && RendezvousSnapshot.TryRead(ours, target, out var s, out _))
                distance = (s.TargetR - s.OurR).magnitude;
            var resumed = RendezvousFlight.ResumeMode(mode, phase, distance);
            logger.LogInfo($"[Rendezvous] resume: saved in {phase} ({mode}), {distance:F0} m from {r.TargetName}: {resumed}");
            var p = saved;
            if (!Start(resumed, r.TargetId, p.PrepareOnly, r.OurPortId, r.TargetPortId))
                return;
            if (Mode != mode && RendezvousFlight.Docks(mode))
            {
                Mode = mode;   // keep "rendezvous + dock" in the file
                Save();
            }
        }

        /// <summary>Forgets the active vessel's saved rendezvous (and stops it).</summary>
        public void CancelSaved()
        {
            if (isRunning)
            {
                isRunning = false;
                logger.LogInfo("[Rendezvous] stopped: the saved plan was cancelled");
            }
            CancelJob();
            DeleteSaved(saved?.VesselId ?? vessel_id ?? ActiveVesselId());
            saved = null;
            saved_error = null;
            ResumeOffer = null;
            Phase = RvPhase.Plan;
            Status = "Saved rendezvous cancelled.";
        }

        // ---------------------------------------------------------------- start, plans

        void Begin(RvMode mode, bool prepareOnly, double distance, string why)
        {
            Mode = mode;
            PrepareOnly = prepareOnly;
            Current = null;
            Budget = null;
            StepIndex = 0;
            replans = 0;
            ResetFlight();
            control_part = ControlPartOf(ActiveVessel());
            vessel_watch.Reset();
            saved = null;
            saved_error = null;
            ResumeOffer = null;
            DockingParts.BeginRun();

            starting = true;
            try
            {
                K2D2_Plugin.ResetControllers(); // one pilot at a time
            }
            finally
            {
                starting = false;
            }
            isRunning = true;
            logger.LogInfo($"[Rendezvous] {why}: {mode} with {TargetName} ({target_id}), {distance:F0} m away, " +
                $"{(prepareOnly ? "Prepare only for the rendezvous burns" : "flown")}, vessel {vessel_id}, around {body_name}");
            if (mode == RvMode.DockOnly)
                BeginDock();
            else
                StartPlan();
        }

        // Both states and the stages now, then the planner on a worker thread.
        void StartPlan()
        {
            SetPhase(RvPhase.Plan);
            var ours = ActiveVessel();
            var target = RendezvousSnapshot.FindVessel(target_id);
            if (target == null)
            {
                Fail("the target vessel no longer exists");
                return;
            }
            if (!RendezvousSnapshot.TryRead(ours, target, out var s, out string error))
            {
                Fail(error);
                return;
            }
            if (!VesselSnapshot.TryRead(out int body, out _, out _, out _, out var st, out error) || body != s.Body)
            {
                Fail("cannot read the vessel's stages: " + (error ?? "another body"));
                return;
            }
            stages = st;
            var system = GameBodySystem.Build(s.UT, out error);
            if (system == null)
            {
                Fail("cannot read the bodies: " + error);
                return;
            }
            double mu = system[s.Body].Mu, low = system[s.Body].LowOrbitRadius;
            body_name = system[s.Body].Name;
            Status = $"Planning the rendezvous with {TargetName}...";
            RunJob(ct => RendezvousPlanner.Plan(s.OurR, s.OurV, s.TargetR, s.TargetV, mu, s.UT, low, standoff, RendezvousPlanner.RevolutionCap, ct),
                t => OnPlanned(t, s.UT));
        }

        void OnPlanned(Task<RvPlan> t, double ut)
        {
            if (!isRunning)
                return;
            if (!PilotJob.Result(t, out var plan, out string error, out bool canceled))
            {
                if (!canceled)
                    Fail("the plan failed: " + error);
                return;
            }
            logger.LogInfo(RendezvousFlight.Describe(plan, TargetName, ut, last_job_ms));
            if (!plan.Ok)
            {
                Fail("refused: " + plan.Refusal);
                return;
            }
            var budget = RendezvousFlight.Budget(plan, 0, stages, RendezvousFlight.Docks(Mode));
            logger.LogInfo($"[Rendezvous] stage budget: {(budget.Ok ? "ok" : budget.Refusal)}; needs {budget.Needed:F0} m/s, " +
                $"has {budget.Available:F0} m/s; stages: {StageList.Describe(stages)}");
            Budget = budget;
            if (!budget.Ok)
            {
                Fail(budget.Refusal);
                return;
            }
            Current = plan;
            StepIndex = 0;
            Status = $"{plan.TotalDeltaV:F1} m/s, arrival in {StrTool.DurationToString(Math.Max(0, plan.ArrivalUT - GeneralTools.Current_UT))}.";
            Save();
        }

        // ---------------------------------------------------------------- worker job

        readonly PilotJob job = new PilotJob();
        double last_job_ms => job.LastMs;

        // Only pure code runs on the worker: the states were read before.
        void RunJob<T>(Func<CancellationToken, T> work, Action<Task<T>> done) => job.Run(work, done);

        void PollJob() => job.Poll(e =>
        {
            logger.LogError($"[Rendezvous] {e}");
            if (isRunning)
                Fail($"internal error ({e.GetType().Name}: {e.Message})");
        });

        void CancelJob() => job.Cancel();

        // ---------------------------------------------------------------- persistence

        static string SavedPath(string id) => Path.Combine(PlansFolder, PlanStore.FileName(id, SavedPlan.KindRendezvous));

        void Save()
        {
            if (string.IsNullOrEmpty(vessel_id) || string.IsNullOrEmpty(target_id))
                return;
            try
            {
                var sp = RendezvousFlight.ToSaved(vessel_id, body_name, target_id, TargetName, Mode, Phase,
                    GeneralTools.Current_UT, PrepareOnly, our_port_pick, target_port_pick);
                PlanFiles.Write(SavedPath(vessel_id), PlanStore.ToJson(sp), logger, "[Rendezvous]");
            }
            catch (Exception e)
            {
                logger.LogWarning($"[Rendezvous] could not save the plan: {e.GetType().Name}: {e.Message}");
            }
        }

        void DeleteSaved(string id)
        {
            if (!string.IsNullOrEmpty(id))
                PlanFiles.Delete(SavedPath(id), logger, "[Rendezvous]");
        }

        void RefreshOffer()
        {
            if (job.Running)
                return;
            string id = ActiveVesselId();
            if (id == checked_vessel || UnityEngine.Time.realtimeSinceStartup < next_offer_check)
                return;
            checked_vessel = id;
            next_offer_check = UnityEngine.Time.realtimeSinceStartup + 1;
            saved = null;
            saved_error = null;
            saved_target_ok = false;
            ResumeOffer = null;
            if (id == null)
                return;
            string path = SavedPath(id);
            try
            {
                if (!File.Exists(path))
                    return;
                if (!PlanStore.TryFromJson(File.ReadAllText(path), out var p, out string error) || p.Rendezvous == null)
                {
                    saved_error = error ?? "not a rendezvous";
                    ResumeOffer = $"The saved rendezvous cannot be read ({saved_error}): cancel it.";
                    logger.LogWarning($"[Rendezvous] saved run {Path.GetFileName(path)} unreadable: {saved_error}");
                    return;
                }
                saved = p;
                RendezvousFlight.TryParse(p.Rendezvous.Phase, out RvPhase phase);
                var target = RendezvousSnapshot.FindVessel(p.Rendezvous.TargetId);
                string ourBody = ActiveVessel()?.Orbit?.referenceBody?.bodyName;
                string targetBody = target?.Orbit?.referenceBody?.bodyName;
                saved_target_ok = target != null && targetBody == ourBody && RendezvousFlight.CanResume(phase);
                ResumeOffer = !RendezvousFlight.CanResume(phase) ? $"The rendezvous with {p.Rendezvous.TargetName} is finished: cancel it."
                    : target == null ? $"The target {p.Rendezvous.TargetName} no longer exists: cancel the saved rendezvous."
                    : targetBody != ourBody ? $"{p.Rendezvous.TargetName} now orbits {targetBody}: cancel the saved rendezvous."
                    : $"Resume the rendezvous with {p.Rendezvous.TargetName} (stopped in {phase}): it is planned again from here.";
                logger.LogInfo($"[Rendezvous] saved run for vessel {id}: {ResumeOffer}");
            }
            catch (Exception e)
            {
                saved_error = e.Message;
                ResumeOffer = $"The saved rendezvous cannot be read ({e.GetType().Name}): cancel it.";
            }
        }

        // ---------------------------------------------------------------- vessel

        static VesselComponent ActiveVessel() => PilotVessel.Active();

        static string IdOf(VesselComponent vessel) => PilotVessel.IdOf(vessel);

        static string ControlPartOf(VesselComponent vessel) => PilotVessel.ControlPartOf(vessel);

        static string ActiveVesselId() => PilotVessel.ActiveId();

        static List<string> PartIds(VesselComponent vessel) => PilotVessel.PartIds(vessel);

        // Our vessel, found by its id even when the player switched to another one; null when it is gone.
        VesselComponent OurVessel() => RendezvousSnapshot.FindVessel(vessel_id);

        // The active vessel is ours: only then do the game's flight inputs (throttle, SAS) reach it.
        bool OursIsActive() => vessel_id != null && ActiveVesselId() == vessel_id;

        string control_part;
        readonly VesselWatch vessel_watch = new VesselWatch();
        bool vessel_wait_logged;

        RvVesselOutcome CheckVessel(string why)
        {
            var vessel = ActiveVessel();
            string now = IdOf(vessel);
            string part = ControlPartOf(vessel);
            bool merged = RendezvousFlight.MergedByDocking(Phase, now, vessel_id, PartIds(vessel), dock_our_port?.Info.PartId, dock_target_parts);
            var watch = merged ? VesselVerdict.Changed
                : vessel_watch.Decide(now, part, vessel_id, control_part, UnityEngine.Time.realtimeSinceStartupAsDouble);
            var outcome = RendezvousFlight.OnVesselChange(Phase, merged, watch);
            switch (outcome)
            {
                case RvVesselOutcome.Same:
                    if (part != null && Phase != RvPhase.Dock)
                        control_part = part;
                    vessel_wait_logged = false;
                    return outcome;
                case RvVesselOutcome.Wait:
                    if (!vessel_wait_logged)
                    {
                        vessel_wait_logged = true;
                        logger.LogInfo($"[Rendezvous] active vessel unsure ({why}): {now ?? "no id"}, control part {part ?? "none"}: waiting");
                    }
                    return outcome;
                case RvVesselOutcome.Docked:
                    logger.LogInfo($"[Docking] vessel change ({why}): {vessel_id} -> {now}, which holds our port and the target's parts: docked");
                    return outcome;
            }
            vessel_wait_logged = false;
            logger.LogInfo($"[Rendezvous] active vessel changed ({why}): {vessel_id} -> {now ?? "none"} -> " +
                (outcome == RvVesselOutcome.TakeOver ? "the new vessel takes the run over" : "the rendezvous stops"));
            if (outcome == RvVesselOutcome.TakeOver)
            {
                string old = vessel_id;
                vessel_id = now;
                Save();
                DeleteSaved(old);
            }
            return outcome;
        }
    }
}
