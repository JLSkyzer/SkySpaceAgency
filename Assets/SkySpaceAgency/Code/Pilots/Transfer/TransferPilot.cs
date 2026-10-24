using System;
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
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Transfer
{
    /// <summary>
    /// Game glue of the transfer to a body (sub-project 4.1): plans with TransferSearch on a worker
    /// thread, then flies the plan step by step (spec 4.1, flight sequence) or, with Prepare only,
    /// makes one game node at a time for the player. The decisions are pure (TransferFlight); this
    /// class reads the game on the main thread, runs the searches and solves on a worker, flies the
    /// burns with TargetOrbitBurn (closed loop, staging when dry) and saves the plan per vessel in
    /// &lt;mod&gt;/plans/plan_&lt;vessel GUID&gt;.json at every phase change. The flight itself is in
    /// TransferPilot.Flight.cs.
    ///
    /// Every stop (Stop, a failure, a reset) leaves the throttle at 0 and the warp off; a failure
    /// keeps the plan saved in the Failed phase, so Resume re-plans from the actual state.
    /// </summary>
    public partial class TransferPilot : Pilot
    {
        public static TransferPilot Instance { get; private set; }

        internal readonly ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Transfer");

        // ---------------------------------------------------------------- what the tab reads

        public TransferPhase Phase { get; private set; } = TransferPhase.Planned;
        public TransferPlan Current { get; private set; }
        public string Status { get; private set; } = "";
        // The per-stage check of the current plan (from the step it is at), null before a plan.
        public BudgetResult Budget { get; private set; }
        public IReadOnlyList<StageDv> Stages => stages;
        // The body system the current plan's indices refer to.
        public BodySystem System { get; private set; }
        public bool PrepareOnly { get; private set; }
        public int StepIndex { get; private set; }
        public bool Searching => job.Running && job.Tag is JobKind kind && kind == JobKind.Search;
        public float SearchProgress => progress.Value;
        // "Resume plan to Duna (next: …)", the mismatch message, or null when the active vessel has
        // no saved plan.
        public string ResumeOffer { get; private set; }
        public bool HasSavedPlan => saved != null || saved_error != null;
        public bool CanResume => !isRunning && !job.Running && saved != null && saved_check != null
            && saved_check.Verdict != ResumeVerdict.Mismatch;
        public bool CanStart => !isRunning && !job.Running && Current != null && Current.Ok && Budget != null && Budget.Ok;

        // ---------------------------------------------------------------- state

        List<StageDv> stages = new List<StageDv>();
        List<CelestialBodyComponent> bodies;
        string vessel_id;
        bool active;
        bool starting; // our own ResetControllers call must not stop us

        // The saved plan of the active vessel, offered for Resume.
        SavedPlan saved;
        ResumeCheck saved_check;
        string saved_error;
        string checked_vessel;
        float next_offer_check;

        public TransferPilot()
        {
            Instance = this;
            debug_mode_only = false;
            K2D2PilotsMgr.Instance.RegisterPilot("Transfer", this);
            _page = new TransferUI(this);
            node = new PrepareOnlyNode(logger, "[Transfer]");
            burn.stage_when_dry = true;
            burn.allow_warp = true;
        }

        internal static string PlansFolder => K2D2_Plugin.PlansFolder;

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
                }
                active = value;
                base.isRunning = value;
            }
        }

        public override void onReset()
        {
            if (starting)
                return;
            // A staging during a burn may send a vessel change: the same vessel goes on, and so does the
            // piece of a split that holds our control part (it takes the plan over).
            // A read still unsure (no id or no control part yet) waits: Fly decides a moment later.
            if (Landing.LandingPilot.reset_by_vessel_change && isRunning && CheckVessel("vessel change message") != VesselVerdict.Changed)
                return;
            if (isRunning)
            {
                Save();
                isRunning = false;
                Status = "Stopped by a vessel or scene change. Resume continues from the actual state.";
                logger.LogInfo("[Transfer] stopped by a reset (vessel change, scene change or another pilot)");
            }
            CancelJob();
            checked_vessel = null; // look for the new active vessel's saved plan
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
                logger.LogError($"[Transfer] {e}");
                Fail($"internal error ({e.GetType().Name}: {e.Message})");
            }
        }

        // ---------------------------------------------------------------- API

        /// <summary>
        /// Searches the transfer asked for (target and arrival; the vessel's body and state are read
        /// now) on a worker thread. The result arrives in Current, with Budget and Status.
        /// </summary>
        public void Plan(TransferInput input)
        {
            if (isRunning)
            {
                Status = "Stop the transfer before planning another one.";
                return;
            }
            if (input == null)
                return;
            CancelJob();
            if (!TakeSnapshot(out var snap, out string error, out _))
            {
                Status = "Cannot plan: " + error;
                return;
            }
            // The target name and altitudes are resolved against this snapshot's bodies.
            if (!input.Resolve(snap.System, out var q, out error))
            {
                Status = error;
                return;
            }
            q.FromBody = snap.Body;
            q.R0 = snap.R;
            q.V0 = snap.V;
            q.UT0 = snap.UT;
            if (q.ToBody < 0 || q.ToBody >= snap.System.Count)
            {
                Status = "Choose a target body.";
                return;
            }
            Adopt(snap);
            Current = null;
            Budget = null;
            Status = $"Searching for a transfer to {snap.System[q.ToBody].Name}...";
            logger.LogInfo($"[Transfer] search: {snap.System[q.FromBody].Name} -> {snap.System[q.ToBody].Name}, {q.Arrival}, " +
                Inv($"Pe {q.ArrivalPeriapsisRadius:F0} m Ap {q.ArrivalApoapsisRadius:F0} m inc {q.ArrivalInclinationDeg:F1}°, max trip {q.MaxTripSeconds:F0} s"));
            var system = snap.System;
            StartSearch(system, q, JobKind.Search, plan => OnPlanned(plan, system, snap.Stages, resume: null));
        }

        public void CancelSearch()
        {
            if (!job.Running)
                return;
            CancelJob();
            Status = "Search cancelled.";
        }

        /// <summary>Flies the current plan, or with prepareOnly makes its nodes one at a time.</summary>
        public bool Start(bool prepareOnly)
        {
            if (!CanStart)
            {
                Status = Current == null ? "Plan a transfer first." : Budget != null && !Budget.Ok ? Budget.Refusal : Status;
                return false;
            }
            if (!TakeSnapshot(out var snap, out string error, out _))
            {
                Status = "Cannot start: " + error;
                return false;
            }
            var plan = Current;
            if (plan.Request != null && snap.Body != plan.Request.FromBody)
            {
                Status = "The vessel is no longer around the body of the plan: plan again.";
                return false;
            }
            foreach (var st in plan.Steps)
                if (TransferFlight.IsBurn(st))
                {
                    if (st.UT < snap.UT - PlanStore.MissedBurnSeconds)
                    {
                        Status = $"The plan is out of date (its {st.Kind} was due {StrTool.DurationToString(snap.UT - st.UT)} ago): plan again.";
                        return false;
                    }
                    break;
                }
            Adopt(snap);
            string id = ActiveVesselId();
            // The orbit must still be the one the plan was searched from (not burned by hand since).
            if (plan.Request != null)
            {
                var asSaved = TransferFlight.ToSaved(plan, snap.System, id, TransferPhase.Planned, 0, plan.Request.UT0, prepareOnly);
                var check = PlanStore.Check(asSaved, snap.System, snap.Body, snap.R, snap.V, snap.UT);
                logger.LogInfo($"[Transfer] start check: {check.Verdict} ({check.Detail})");
                if (check.Verdict != ResumeVerdict.Resume)
                {
                    Status = $"The vessel no longer matches the plan ({check.Detail}): plan again.";
                    return false;
                }
            }
            Budget = TransferFlight.RemainingBudget(plan, 0, stages);
            if (!Budget.Ok)
            {
                Status = Budget.Refusal;
                return false;
            }
            vessel_id = id;
            if (vessel_id == null)
            {
                Status = "Cannot start: the vessel has no id.";
                return false;
            }
            Begin(plan, 0, prepareOnly, snap, "start");
            return true;
        }

        public void Stop()
        {
            if (job.Running && !isRunning)
            {
                CancelSearch();
                return;
            }
            if (!isRunning)
                return;
            Save();
            isRunning = false;
            checked_vessel = null; // offer the saved plan
            Status = "Stopped. Resume continues from the actual state.";
            logger.LogInfo($"[Transfer] stopped by the player in {Phase}, step {StepIndex}");
        }

        /// <summary>Resumes the active vessel's saved plan, re-planned from the actual state.</summary>
        public void Resume()
        {
            if (isRunning || saved == null)
                return;
            if (!TakeSnapshot(out var snap, out string error, out _))
            {
                Status = "Cannot resume: " + error;
                return;
            }
            var check = PlanStore.Check(saved, snap.System, snap.Body, snap.R, snap.V, snap.UT);
            var action = TransferFlight.DecideResume(saved, check);
            logger.LogInfo($"[Transfer] resume: {check.Verdict} -> {action} ({check.Detail}), saved in {saved.Phase} at step {saved.StepIndex}");
            if (action == ResumeAction.Continue && !TransferFlight.RemapBodies(saved, snap.System))
            {
                logger.LogInfo("[Transfer] resume: the body indices cannot be mapped by name: planned again");
                action = ResumeAction.Replan;
            }
            vessel_id = saved.VesselId;
            Adopt(snap);
            var resumed = saved;
            switch (action)
            {
                case ResumeAction.Refuse:
                    Status = TransferFlight.ResumeOffer(saved, check, snap.UT, StrTool.DurationToString);
                    return;
                case ResumeAction.Replan:
                {
                    var q = resumed.Plan.Request != null ? CopyRequest(resumed.Plan.Request) : new TransferRequest();
                    q.ToBody = IndexOfName(snap.System, resumed.ToBodyName);
                    q.FromBody = snap.Body;
                    q.R0 = snap.R;
                    q.V0 = snap.V;
                    q.UT0 = snap.UT;
                    if (q.ToBody < 0)
                    {
                        Status = PlanStore.MismatchMessage;
                        return;
                    }
                    Status = $"Planning the transfer to {resumed.ToBodyName} again from the actual state...";
                    var system = snap.System;
                    StartSearch(system, q, JobKind.Search, plan => OnPlanned(plan, system, snap.Stages, resumed));
                    return;
                }
                default:
                {
                    var plan = resumed.Plan;
                    var budget = TransferFlight.RemainingBudget(plan, resumed.StepIndex, stages);
                    Current = plan;
                    Budget = budget;
                    if (!budget.Ok)
                    {
                        Status = budget.Refusal;
                        return;
                    }
                    Begin(plan, resumed.StepIndex, resumed.PrepareOnly, snap, "resume");
                    free_arrival_step = resumed.FreeArrivalStep;
                    return;
                }
            }
        }

        /// <summary>Forgets the active vessel's saved plan (and stops the transfer).</summary>
        public void CancelSaved()
        {
            if (isRunning)
            {
                isRunning = false;
                logger.LogInfo("[Transfer] stopped: the saved plan was cancelled");
            }
            CancelJob();
            string id = saved?.VesselId ?? vessel_id ?? ActiveVesselId();
            DeleteSaved(id);
            saved = null;
            saved_check = null;
            saved_error = null;
            ResumeOffer = null;
            Phase = TransferPhase.Planned;
            Status = "Saved plan cancelled.";
        }

        // ---------------------------------------------------------------- start, plans

        void Begin(TransferPlan plan, int stepIndex, bool prepareOnly, Snapshot snap, string why)
        {
            Current = plan;
            StepIndex = stepIndex;
            PrepareOnly = prepareOnly;
            ResetFlight(snap.Body);
            control_part = ControlPartOf(ActiveVessel());
            vessel_watch.Reset();
            vessel_wait_logged = false;
            saved = null;
            saved_check = null;
            saved_error = null;
            ResumeOffer = null;

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
            VesselSnapshot.LogStartChecks(snap.System, snap.Bodies);
            logger.LogInfo($"[Transfer] {why}: {(prepareOnly ? "Prepare only (one node at a time, no warp)" : "flown")}, " +
                $"vessel {vessel_id}, step {stepIndex}/{plan.Steps.Count}, {snap.System[snap.Body].Name}");
            Phase = TransferPhase.Planned;
            Status = prepareOnly ? "Prepare only: the next node comes next." : "Transfer started.";
            Save();
            UpdateSoiPrediction("start");
        }

        // A finished search: the new plan for the tab, or a resumed plan to fly.
        void OnPlanned(TransferPlan plan, BodySystem system, List<StageDv> snapStages, SavedPlan resume)
        {
            System = system;
            stages = snapStages ?? stages;
            Current = plan;
            logger.LogInfo(TransferFlight.Describe(plan, system, last_job_ms));
            if (!plan.Ok)
            {
                Budget = null;
                Status = "Refused: " + plan.Refusal;
                logger.LogInfo($"[Transfer] refused: {plan.Refusal}");
                return;
            }
            Budget = TransferFlight.RemainingBudget(plan, 0, stages);
            logger.LogInfo($"[Transfer] stage budget: {(Budget.Ok ? "ok" : Budget.Refusal)}; needs {Budget.Needed:F0} m/s, has {Budget.Available:F0} m/s; " +
                $"stages: {StageList.Describe(stages)}");
            if (!Budget.Ok)
            {
                Status = Budget.Refusal;
                return;
            }
            Status = $"{plan.CaseName}: {plan.TotalDeltaV:F0} m/s, departure in {StrTool.DurationToString(Math.Max(0, plan.DepartureUT - GeneralTools.Current_UT))}, " +
                $"trip {StrTool.DurationToString(plan.TripSeconds)}.";
            if (resume != null)
            {
                if (!TakeSnapshot(out var snap, out string error, out _))
                {
                    Status = "Cannot resume: " + error;
                    return;
                }
                Adopt(snap);
                Begin(plan, 0, resume.PrepareOnly, snap, "resume (planned again)");
            }
        }

        // ---------------------------------------------------------------- snapshot

        sealed class Snapshot
        {
            public List<CelestialBodyComponent> Bodies;
            public BodySystem System;
            public int Body;
            public Vector3d R, V;
            public double UT;
            public List<StageDv> Stages;
        }

        // Fresh bodies, vessel and stages, read together on the main thread. transient: worth waiting
        // for (the Δv data not ready, a burn on rails).
        bool TakeSnapshot(out Snapshot snap, out string error, out bool transient)
        {
            snap = null;
            transient = false;
            VesselComponent vessel = K2D2_Plugin.Instance?.current_vessel?.VesselComponent;
            if (vessel == null)
            {
                error = "no active vessel";
                return false;
            }
            if (vessel.IsOrbitalPhysicsUnderThrustActive)
            {
                // The game moves the vessel on an n-body patch while it burns on rails.
                error = "the game is integrating a burn under warp";
                transient = true;
                return false;
            }
            var list = GameBodySystem.GameBodies();
            double ut = GeneralTools.Current_UT;
            var system = GameBodySystem.Build(list, ut, out error);
            if (system == null)
                return false;
            if (!VesselSnapshot.TryRead(list, out int body, out var r, out var v, out double t, out var st, out error))
            {
                transient = error != null && error.Contains("not ready");
                return false;
            }
            snap = new Snapshot { Bodies = list, System = system, Body = body, R = r, V = v, UT = t, Stages = st };
            return true;
        }

        void Adopt(Snapshot snap)
        {
            System = snap.System;
            bodies = snap.Bodies;
            stages = snap.Stages;
        }

        // ---------------------------------------------------------------- worker jobs

        enum JobKind { Search, Solve }

        sealed class ProgressBox : IProgress<float>
        {
            float value;
            public float Value => Volatile.Read(ref value);
            public void Report(float v) => Volatile.Write(ref value, v);
        }

        readonly PilotJob job = new PilotJob();
        readonly ProgressBox progress = new ProgressBox();
        double last_job_ms => job.LastMs;

        // Only pure code runs on the worker: the snapshot was read before.
        void RunJob<T>(JobKind kind, Func<CancellationToken, T> work, Action<Task<T>> done) => job.Run(work, done, kind);

        void PollJob() => job.Poll(e =>
        {
            logger.LogError($"[Transfer] {e}");
            if (isRunning)
                Fail($"internal error ({e.GetType().Name}: {e.Message})");
            else
                Status = $"Internal error: {e.GetType().Name}: {e.Message}";
        });

        void CancelJob() => job.Cancel();

        void StartSearch(BodySystem system, TransferRequest q, JobKind kind, Action<TransferPlan> onPlan)
        {
            progress.Report(0);
            RunJob(kind, ct => TransferSearch.Find(system, q, ct, progress), t =>
            {
                if (!PilotJob.Result(t, out var plan, out string error, out bool canceled))
                {
                    if (canceled)
                    {
                        Status = "Search cancelled.";
                        logger.LogInfo($"[Transfer] search cancelled after {last_job_ms:F0} ms");
                    }
                    else
                    {
                        logger.LogWarning($"[Transfer] search failed after {last_job_ms:F0} ms: {error}");
                        if (isRunning)
                            Fail("the search failed: " + error);
                        else
                            Status = "The search failed: " + error;
                    }
                    return;
                }
                logger.LogInfo($"[Transfer] search done in {last_job_ms:F0} ms");
                onPlan(plan);
            });
        }

        // ---------------------------------------------------------------- persistence

        static string SavedPath(string id) => Path.Combine(PlansFolder, PlanStore.FileName(id));

        void Save()
        {
            if (Current == null || System == null || string.IsNullOrEmpty(vessel_id))
                return;
            try
            {
                var sp = TransferFlight.ToSaved(Current, System, vessel_id, Phase, StepIndex, GeneralTools.Current_UT, PrepareOnly, burn_ignited, free_arrival_step);
                PlanFiles.Write(SavedPath(vessel_id), PlanStore.ToJson(sp), logger, "[Transfer]");
            }
            catch (Exception e)
            {
                logger.LogWarning($"[Transfer] could not save the plan: {e.GetType().Name}: {e.Message}");
            }
        }

        void DeleteSaved(string id)
        {
            if (!string.IsNullOrEmpty(id))
                PlanFiles.Delete(SavedPath(id), logger, "[Transfer]");
        }

        // When a vessel with a saved plan is active in flight: PlanStore.Check, then the offer.
        void RefreshOffer()
        {
            if (job.Running)
                return;
            string id = ActiveVesselId();
            if (id != checked_vessel && UnityEngine.Time.realtimeSinceStartup >= next_offer_check)
            {
                checked_vessel = id;
                LoadOffer(id);
            }
            if (saved != null && saved_check != null)
                ResumeOffer = TransferFlight.ResumeOffer(saved, saved_check, GeneralTools.Current_UT, StrTool.DurationToString);
        }

        void LoadOffer(string id)
        {
            saved = null;
            saved_check = null;
            saved_error = null;
            ResumeOffer = null;
            if (id == null)
                return;
            string path = SavedPath(id);
            try
            {
                if (!File.Exists(path))
                    return;
                string json = File.ReadAllText(path);
                if (!PlanStore.TryFromJson(json, out var p, out string error))
                {
                    saved_error = error;
                    ResumeOffer = $"The saved plan cannot be read ({error}): cancel it to plan again.";
                    logger.LogWarning($"[Transfer] saved plan {Path.GetFileName(path)} unreadable: {error}");
                    return;
                }
                if (!TakeSnapshot(out var snap, out error, out bool transient))
                {
                    // Try again in a moment (the Δv data is often not ready right after a load).
                    checked_vessel = null;
                    next_offer_check = UnityEngine.Time.realtimeSinceStartup + 1;
                    ResumeOffer = $"Saved plan to {p.ToBodyName}: reading the vessel...";
                    if (!transient)
                        logger.LogInfo($"[Transfer] saved plan found, but the vessel cannot be read yet: {error}");
                    return;
                }
                saved = p;
                saved_check = PlanStore.Check(p, snap.System, snap.Body, snap.R, snap.V, snap.UT);
                ResumeOffer = TransferFlight.ResumeOffer(p, saved_check, snap.UT, StrTool.DurationToString);
                logger.LogInfo($"[Transfer] saved plan for vessel {id}: to {p.ToBodyName}, {p.Phase} at step {p.StepIndex}, " +
                    $"check {saved_check.Verdict} ({saved_check.Detail}); offer: {ResumeOffer}");
            }
            catch (Exception e)
            {
                saved_error = e.Message;
                ResumeOffer = $"The saved plan cannot be read ({e.GetType().Name}): cancel it to plan again.";
                logger.LogWarning($"[Transfer] reading the saved plan failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- helpers

        // The game's active vessel now. KSPVessel.VesselComponent is refreshed only in K2D2_Plugin.Update:
        // inside a vessel-change message it may still be the previous vessel.
        static VesselComponent ActiveVessel() => PilotVessel.Active();

        static string IdOf(VesselComponent vessel) => PilotVessel.IdOf(vessel);

        static string ControlPartOf(VesselComponent vessel) => PilotVessel.ControlPartOf(vessel);

        internal static string ActiveVesselId() => PilotVessel.ActiveId();

        // The control part of our vessel, kept up to date while it is active.
        string control_part;
        readonly VesselWatch vessel_watch = new VesselWatch();
        bool vessel_wait_logged;

        // Same: the active vessel is ours. TakeOver: a new vessel (a split) holds our control part and
        // takes the plan over, saved under its id (the old file is deleted). Wait: the read is unsure or
        // the vessel differs, for less than VesselWatch's delay. Changed: another vessel, for good.
        VesselVerdict CheckVessel(string why)
        {
            var vessel = ActiveVessel();
            string now = IdOf(vessel);
            string part = ControlPartOf(vessel);
            var verdict = vessel_watch.Decide(now, part, vessel_id, control_part, UnityEngine.Time.realtimeSinceStartupAsDouble);
            switch (verdict)
            {
                case VesselVerdict.Same:
                    if (part != null)
                        control_part = part;
                    if (vessel_wait_logged)
                        logger.LogInfo($"[Transfer] the active vessel reads as ours again ({vessel_id})");
                    vessel_wait_logged = false;
                    return verdict;
                case VesselVerdict.Wait:
                    if (!vessel_wait_logged)
                    {
                        vessel_wait_logged = true;
                        logger.LogInfo($"[Transfer] active vessel unsure ({why}): {now ?? "no id"}, control part {part ?? "none"} " +
                            $"(ours {vessel_id}, {control_part ?? "unknown"}): waiting");
                    }
                    return verdict;
            }
            vessel_wait_logged = false;
            logger.LogInfo($"[Transfer] active vessel changed ({why}): {vessel_id} -> {now ?? "none"}, control part {control_part ?? "unknown"} " +
                $"vs {part ?? "none"} -> {(verdict == VesselVerdict.TakeOver ? "the new vessel takes the plan over" : "the transfer stops")}");
            if (verdict == VesselVerdict.TakeOver)
            {
                string old = vessel_id;
                vessel_id = now;
                Save();
                DeleteSaved(old);
                logger.LogInfo($"[Transfer] plan moved from vessel {old} to vessel {now}");
            }
            return verdict;
        }

        static int IndexOfName(BodySystem s, string name)
        {
            for (int i = 0; i < s.Count; i++)
                if (s[i].Name == name)
                    return i;
            return -1;
        }

        static TransferRequest CopyRequest(TransferRequest q) => new TransferRequest
        {
            FromBody = q.FromBody,
            ToBody = q.ToBody,
            R0 = q.R0,
            V0 = q.V0,
            UT0 = q.UT0,
            Arrival = q.Arrival,
            ArrivalPeriapsisRadius = q.ArrivalPeriapsisRadius,
            ArrivalApoapsisRadius = q.ArrivalApoapsisRadius,
            ArrivalInclinationDeg = q.ArrivalInclinationDeg,
            MaxTripSeconds = q.MaxTripSeconds,
        };

        static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);
    }
}
