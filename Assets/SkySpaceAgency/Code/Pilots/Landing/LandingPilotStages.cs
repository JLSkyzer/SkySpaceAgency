using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using K2D2.Landing.Braking;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // Landing with a chosen stage (powered braking only, parachute mode is unchanged): the lower
    // stages brake first and are dropped when dry or at the drop height (StagedBrakePlan), then
    // the chosen stage lands as before. The choice is landing_stage_choice (0 = the current stage:
    // exactly the landing as before). LandingStageDrop fires the stages.
    public partial class LandingPilot
    {
        // The Land tab's choice: firing-order index of the landing stage, 0 = the current stage.
        // Not saved: a stage number only means something on the vessel it was chosen on.
        internal int landing_stage_choice = 0;

        // The run: lower stages still to drop (0 = the landing stage is active, or no stage
        // choice), the landing stage's game number (DeltaVStageInfo.Stage), -1 without a choice.
        internal int staged_drops_left = 0;
        internal int staged_landing_stage = -1;
        internal StagePlan stage_plan;
        internal bool stage_plan_valid = false;
        // The run's drop height (m above the terrain), never lowered during a run; NaN before a plan.
        internal double stage_drop_height = double.NaN;
        // Shown while running: a refused or failed drop, or a plan that no longer works.
        internal string stage_message = "";
        internal LandingStageDrop stage_drop;

        // The plan made at the start, for the predicted-against-actual log lines.
        StagePlan start_stage_plan;
        bool start_stage_plan_valid;
        int drops_done = 0;
        string last_stage_log;
        float next_stage_log_time = 0;
        bool stage_number_warned = false;
        bool drops_stopped = false;
        // The game's staging at the start: the drops left count activations from it.
        int staged_choice = 0;
        int staging_count_start = 0;
        bool staging_last_active_start = true;
        // The plan does not work any more (shown as an error in the status line).
        internal bool stage_plan_alarm = false;

        // Auto-staging (StagingPilot) is suspended while a landing with a chosen stage runs.
        internal bool SuspendsAutoStaging => isRunning && staged_landing_stage >= 0 && (StagedBraking || (stage_drop?.Busy ?? false));

        // A staged braking is under way (or being checked at start).
        internal bool StagedBraking => staged_drops_left > 0 && !parachute_mode;

        // The player's drop height in metres (0 = automatic or not a number).
        internal double PlayerDropHeight
        {
            get
            {
                double h = LandingStages.ParseDropHeight(settings.drop_stage_at_km.V);
                return double.IsNaN(h) ? 0 : h;
            }
        }

        void ClearStagedRun()
        {
            staged_drops_left = 0;
            staged_landing_stage = -1;
            stage_plan_valid = false;
            start_stage_plan_valid = false;
            stage_drop_height = double.NaN;
            stage_message = "";
            drops_done = 0;
            last_stage_log = null;
            stage_number_warned = false;
            stage_plan_alarm = false;
            drops_stopped = false;
            staged_choice = 0;
            stage_drop?.Reset();
        }

        /// <summary>
        /// The vessel's stages in firing order (DeltaVStageInfo.Stage descending), the active one
        /// first; null with the reason when the game's Δv data is not available.
        /// </summary>
        internal static List<LandingStageData> ReadStageList(VesselComponent vessel, out string error)
        {
            error = null;
            VesselDeltaVComponent dv = vessel?.VesselDeltaV;
            if (dv?.StageInfo == null)
            {
                error = "The game gives no Δv data for this vessel.";
                return null;
            }
            if (!dv.IsReady)
            {
                error = "The game's Δv data is not ready yet: try again in a moment.";
                return null;
            }
            var list = new List<LandingStageData>();
            foreach (DeltaVStageInfo s in dv.StageInfo)
            {
                if (s == null)
                    continue;
                double thrust = s.ThrustActual > 0 && s.ThrustVac > 0 ? Math.Min(s.ThrustActual, s.ThrustVac) : s.ThrustVac;
                double isp = s.IspActual > 0 && s.IspVac > 0 ? Math.Min(s.IspActual, s.IspVac) : s.IspVac;
                list.Add(new LandingStageData
                {
                    Stage = s.Stage,
                    DeltaV = Finite(s.DeltaVinVac),
                    ThrustKN = Finite(thrust),
                    Isp = Finite(isp),
                    StartMassT = Finite(s.StartMass),
                });
            }
            list.Sort((a, b) => b.Stage.CompareTo(a.Stage));
            return list;
        }

        static double Finite(double x) => double.IsNaN(x) || double.IsInfinity(x) ? 0 : x;

        static string DescribeStages(IReadOnlyList<LandingStageData> list) =>
            string.Join(", ", list.Select((s, i) => string.Format(CultureInfo.InvariantCulture,
                "{0}: game stage {1}, Δv {2:0} m/s, thrust {3:0.0} kN, Isp {4:0} s, start mass {5:0.00} t",
                i + 1, s.Stage, s.DeltaV, s.ThrustKN, s.Isp, s.StartMassT)));

        /// <summary>
        /// Start checks of a landing with a chosen stage, before the braking simulation: the stage
        /// list, the landing stage's engine and what each drop would drop. Sets staged_drops_left
        /// on success. False with last_error set when refused.
        /// </summary>
        bool PrepareStagedRun(out double landing_accel)
        {
            ClearStagedRun();
            landing_accel = burn_dV.active_dv;
            if (landing_stage_choice <= 0 || parachute_mode)
                return true;

            VesselComponent vessel = current_vessel.VesselComponent;
            List<LandingStageData> list = ReadStageList(vessel, out string error);
            BrakeStage[] stages = list == null ? null
                : LandingStages.Build(list, landing_stage_choice, burn_dV.mass, burn_dV.active_thrust, burn_dV.active_isp, out error);
            if (stages == null)
            {
                last_error = error ?? LandingStages.GoneMessage;
                return false;
            }
            string refusal = LandingStageDrop.CheckDrops(vessel, landing_stage_choice, out string topology);
            logger.LogInfo($"[Landing] stage plan: landing on stage {landing_stage_choice + 1} (game stage {list[landing_stage_choice].Stage}), " +
                $"{landing_stage_choice} drop(s) | stages: {DescribeStages(list)} | {topology}" + (refusal != null ? $" -> {refusal}" : ""));
            if (refusal != null)
            {
                last_error = refusal;
                return false;
            }
            BrakeStage landing = stages[stages.Length - 1];
            landing_accel = landing.MassT > 0 ? landing.ThrustKN / landing.MassT : 0;
            StagingComponent staging = vessel.SimulationObject?.Staging;
            if (staging == null)
            {
                last_error = "The vessel's staging is not available.";
                return false;
            }
            staged_drops_left = landing_stage_choice;
            staged_choice = landing_stage_choice;
            staged_landing_stage = list[landing_stage_choice].Stage;
            staging_count_start = staging.StageCount;
            staging_last_active_start = staging_count_start > 0 && staging.AvailableStages[staging_count_start - 1].IsActive;
            return true;
        }

        /// <summary>
        /// The staged plan for the braking simulation's input, when lower stages are left: the
        /// stage list is read again (the active stage burns), the drops left follow the landing
        /// stage's place in it. False: plain braking on the active stage.
        /// </summary>
        bool TryStagedPlan(BrakeInput input, out StagePlan plan)
        {
            plan = default;
            if (!StagedBraking)
                return false;
            List<LandingStageData> list = ReadStageList(current_vessel.VesselComponent, out string error);
            if (list == null)
            {
                LogStage($"[Landing] stage plan: no stage list ({error}), braking on the active stage this time");
                return false;
            }
            SyncDropsLeft(list);
            if (staged_drops_left <= 0)
                return false;
            BrakeStage[] stages = LandingStages.Build(list, staged_drops_left, burn_dV.mass, burn_dV.active_thrust, burn_dV.active_isp, out error);
            if (stages == null)
            {
                LogStage($"[Landing] stage plan: {error} ({DescribeStages(list)})");
                return false;
            }
            input.Stages = stages;
            plan = StagedBrakePlan.Plan(new StagePlanInput { Brake = input, PlayerDropHeight = PlayerDropHeight });
            return true;
        }

        /// <summary>
        /// The stages for the precision plans (Precision.Vehicle.Stages), from the live list; null
        /// without lower stages left (error null) or when the list cannot be read (error set).
        /// </summary>
        internal BrakeStage[] StagesForPlans(out string error)
        {
            error = null;
            if (!StagedBraking)
                return null;
            List<LandingStageData> list = ReadStageList(current_vessel.VesselComponent, out error);
            if (list == null)
                return null;
            SyncDropsLeft(list);
            if (staged_drops_left <= 0)
                return null;
            return LandingStages.Build(list, staged_drops_left, burn_dV.mass, burn_dV.active_thrust, burn_dV.active_isp, out error);
        }

        // The drops left come from the game's staging: the activations made since the start
        // (LandingStages.ActivationsDone), whoever made them. The Δv list is only cross-checked:
        // its landing stage should be at the index of the drops left (logged once otherwise).
        void SyncDropsLeft(List<LandingStageData> list)
        {
            if (drops_stopped)
            {
                staged_drops_left = 0;
                return;
            }
            StagingComponent staging = current_vessel.VesselComponent?.SimulationObject?.Staging;
            if (staging == null)
                return;
            int count = staging.StageCount;
            bool lastActive = count > 0 && staging.AvailableStages[count - 1].IsActive;
            int done = LandingStages.ActivationsDone(staging_count_start, staging_last_active_start, count, lastActive);
            int left = Math.Max(0, staged_choice - done);
            if (left != staged_drops_left)
            {
                logger.LogInfo($"[Landing] stage plan: {done} activation(s) since the start ({staging_count_start} -> {count} stage(s)): " +
                    $"{left} drop(s) left (were {staged_drops_left})" + (left == 0 ? "; the landing stage is active" : ""));
                staged_drops_left = left;
            }
            if (list != null && !stage_number_warned)
            {
                int index = list.FindIndex(s => s.Stage == staged_landing_stage);
                if (index != staged_drops_left)
                {
                    stage_number_warned = true;
                    logger.LogWarning($"[Landing] stage plan: the Δv list puts game stage {staged_landing_stage} at index {index}, " +
                        $"the staging says {staged_drops_left} drop(s) left ({DescribeStages(list)}); the staging is followed");
                }
            }
        }

        // The plan's result, kept for the drops and the UI. During a run the drop height only goes
        // up. A plan that no longer works mid-run is an alarm (status line and log); the pilot
        // then drops the lower stages now when the landing stage alone can still finish from
        // here, else brakes now when it is still waiting.
        void UseStagePlan(StagePlan plan, double now)
        {
            stage_plan = plan;
            stage_plan_valid = true;
            stage_drop_height = isRunning && !double.IsNaN(stage_drop_height) ? Math.Max(stage_drop_height, plan.DropHeight) : plan.DropHeight;
            if (isRunning && !plan.Ok)
            {
                string reaction = "";
                if (plan.DropNowFeasible && staged_drops_left > 0 && !stage_drop.Busy)
                {
                    stage_drop_height = Math.Max(stage_drop_height, altitude + 1);
                    reaction = " The lower stage is dropped now.";
                }
                else if (mode == Mode.Pause || mode == Mode.QuickWarp || mode == Mode.RotationWarp || mode == Mode.Waiting)
                {
                    reaction = " Braking now.";
                    setMode(Mode.Brake);
                }
                string message = "Staged landing no longer works: " + plan.Error + reaction;
                if (!stage_plan_alarm || message != stage_message)
                    logger.LogWarning($"[Landing] stage plan: ALARM: {message} | {plan.Describe()}");
                stage_message = message;
                stage_plan_alarm = true;
            }
            else if (stage_plan_alarm)
            {
                logger.LogInfo($"[Landing] stage plan: works again: {plan.Describe()}");
                stage_message = "";
                stage_plan_alarm = false;
            }
            LogStage($"[Landing] stage plan: {plan.Describe()} | run drop height {stage_drop_height:n0} m, {staged_drops_left} drop(s) left, " +
                $"start in {plan.Brake.StartUT - now:n1}s", force: false);
        }

        void LogStage(string line, bool force = true)
        {
            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (!force && line == last_stage_log)
                return;
            // Changes are logged at most every 5 s (the numbers move a little every plan).
            if (!force && now_real < next_stage_log_time)
                return;
            last_stage_log = line;
            next_stage_log_time = now_real + 5;
            logger.LogInfo(line);
        }

        // Stops the drops for the rest of the run (the landing goes on with the current stage).
        void StopDrops(string message)
        {
            stage_message = message;
            stage_plan_alarm = true;
            drops_stopped = true;
            staged_drops_left = 0;
        }

        // Every frame of a running staged landing: drop the lower stage when dry or at the drop
        // height, then check that the next stage pushes (LandingStageDrop).
        void UpdateStageDrops()
        {
            if (!isRunning || staged_landing_stage < 0 || !(StagedBraking || stage_drop.Busy))
                return;
            bool descent = mode == Mode.Pause || mode == Mode.QuickWarp || mode == Mode.RotationWarp
                || mode == Mode.Waiting || mode == Mode.Brake || mode == Mode.TouchDown;
            if (!descent && !stage_drop.Busy)
                return;

            VesselComponent vessel = current_vessel.VesselComponent;
            double ut = GeneralTools.Current_UT;
            SyncDropsLeft(null);
            double dropHeight = double.IsNaN(stage_drop_height)
                ? StagedBrakePlan.Floor(PlayerDropHeight, PrecisionActive ? settings.start_touchdown_altitude.V : BrakeSimulator.SafeAltitude)
                : stage_drop_height;
            double speed = current_vessel.VesselVehicle.SurfaceSpeed;
            LandingStageDrop.Outcome outcome;
            try
            {
                outcome = stage_drop.Update(vessel, ut, staged_drops_left > 0, altitude, speed, dropHeight,
                    current_vessel.GetThrottle(), burn_dV.active_dv, burn_dV.actual_dv);
            }
            catch (Exception e)
            {
                logger.LogWarning($"[Landing] stage drop failed: {e.GetType().Name}: {e.Message}");
                stage_drop.Reset();
                StopDrops($"Lower stage not dropped ({e.GetType().Name}): landing on the current stage. Stage manually if needed.");
                return;
            }

            switch (outcome)
            {
                case LandingStageDrop.Outcome.Dropped:
                    drops_done++;
                    SyncDropsLeft(null);
                    LogDropAgainstPlan(ut);
                    // Plan again at once with the new stage.
                    next_brake_sim_time = 0;
                    break;
                case LandingStageDrop.Outcome.Activated:
                    SyncDropsLeft(null);
                    break;
                case LandingStageDrop.Outcome.Failed:
                    StopDrops($"Lower stage not dropped: {stage_drop.LastFailure}. Landing on the current stage: stage manually if needed.");
                    break;
                case LandingStageDrop.Outcome.Abort:
                    StopWithError("Staged landing stopped: " + stage_drop.LastFailure + ".");
                    break;
            }
        }

        void LogDropAgainstPlan(double ut)
        {
            string planned = "no planned drop";
            if (start_stage_plan_valid && start_stage_plan.Brake.Drops != null && drops_done - 1 < start_stage_plan.Brake.Drops.Count)
            {
                BrakeDrop d = start_stage_plan.Brake.Drops[drops_done - 1];
                planned = $"planned {(d.Dry ? "dry" : "forced")} at {d.Altitude:n0} m, {d.SurfaceSpeed:n0} m/s, start +{d.UT - start_stage_plan.Brake.StartUT:n1}s of braking";
            }
            logger.LogInfo($"[Landing] stage drop {drops_done}: {(stage_drop.PendingReason == DropReason.Dry ? "dry" : "drop height")} " +
                $"at {stage_drop.PendingAltitude:n0} m, {stage_drop.PendingSpeed:n0} m/s (drop height {stage_drop_height:n0} m) | {planned} | " +
                $"Δv burned so far {burn_dV.burned_dV:n0} m/s, {staged_drops_left} drop(s) left");
        }

        // A precision landing with a chosen stage: its predicted brake (the deorbit plan's) is the
        // start plan the drops and the touchdown are logged against.
        void RecordStagedPrecisionPlan(Precision.StartPlan plan, Precision.Vehicle vehicle)
        {
            Precision.StopPrediction stop = plan.Deorbit.Stop;
            BrakeStage landing = vehicle.Stages[vehicle.Stages.Length - 1];
            BrakeDrop? last = stop.Drops != null && stop.Drops.Count > 0 ? stop.Drops[stop.Drops.Count - 1] : (BrakeDrop?)null;
            start_stage_plan = new StagePlan
            {
                Ok = true,
                DropHeight = stop.DropHeight,
                SafeDropHeight = stop.SafeDropHeight,
                Brake = new BrakeResult
                {
                    Status = BrakeStatus.Ok, StartUT = stop.BrakeStartUT, DeltaVNeeded = stop.BrakeDeltaV,
                    StopAltitude = stop.StopAltitude, Drops = stop.Drops,
                },
                LandingStageReached = last.HasValue,
                DroppedDry = last.HasValue && last.Value.Dry,
                DropAltitude = last.HasValue ? last.Value.Altitude : double.NaN,
                SpeedAtDrop = last.HasValue ? last.Value.SurfaceSpeed : double.NaN,
                LandingDeltaVNeeded = plan.LandingStageDeltaV,
                LandingDeltaVAvailable = landing.DeltaV,
            };
            start_stage_plan_valid = true;
            // The flight drops at the drop height the prediction used (safe height included).
            stage_drop_height = double.IsNaN(stop.DropHeight) || stop.DropHeight <= 0 ? vehicle.DropHeight : stop.DropHeight;
            logger.LogInfo($"[Landing] stage plan: precision: stage 1 pays {plan.BeforeBrakeDeltaV:n0} of {vehicle.Stages[0].DeltaV:n0} m/s " +
                $"before the brake; predicted brake {start_stage_plan.Describe()}; landing stage needs {plan.LandingStageDeltaV:n0} m/s " +
                $"(brake part, terminal and lateral reserves) of {landing.DeltaV:n0} m/s");
        }

        // At the end of a staged landing: the braking predicted at the start against the real one.
        void LogStagedFinish()
        {
            if (staged_landing_stage < 0)
                return;
            string predicted = start_stage_plan_valid
                ? $"predicted Δv {start_stage_plan.Brake.DeltaVNeeded:n0} m/s (landing stage {start_stage_plan.LandingDeltaVNeeded:n0} of " +
                  $"{start_stage_plan.LandingDeltaVAvailable:n0} m/s), {start_stage_plan.Brake.Drops?.Count ?? 0} drop(s)"
                : "no plan at the start";
            logger.LogInfo($"[Landing] staged braking: {predicted} | actual Δv burned {burn_dV.burned_dV:n0} m/s, {drops_done} drop(s), " +
                $"{staged_drops_left} left");
        }
    }
}
