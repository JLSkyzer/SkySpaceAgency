using System;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.UI;
using KSP.Api;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.State;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Rendezvous
{
    // Flies the docking guidance's command: the wanted velocity relative to the target port with
    // the RCS (through the probed mapping) or the main engine (turn, short burn, turn back), and
    // our port's attitude through SAS. Game glue; the logic is in TranslateLogic.cs.
    //
    // Use (the Rendezvous pilot, main thread):
    // 1. after "control from here" on our port and the RCS group on: StartProbe(rcsUsable);
    // 2. every frame, before Update: SetInput(state, command). The probes only need the state;
    // 3. once Ready: Mode, ModeNote, and Widen(limits) for the guidance's limits in main-engine mode;
    // 4. Stop(our vessel id) at the end; Stop() acts on the active vessel. It never stages. It sets
    //    the throttle and X/Y/Z to 0 on Stop, on an invalid command, on a frame without input (or an unreadable port frame), and on a failure.
    public class Translate : ExecuteController
    {
        enum Stage { Idle, ProbeRcs, ProbeEngine, Run }

        readonly ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Translate");
        readonly BurndV burn_dV = new BurndV();
        KSPVessel current_vessel;

        Stage stage = Stage.Idle;
        bool rcsUsable;
        RcsProbeSequence rcsProbe;
        EngineProbe engineProbe;
        MainEngineSequence engine;
        double[] rcsAccel;
        Vector3d thrustLocal;
        Rotation? holdRotation;

        DockState state;
        DockCommand command;
        bool fresh;
        int loggedBurns;
        RcsCommand lastRcs;
        double lastThrottle;

        public TranslateMode Mode { get; private set; } = TranslateMode.Refused;
        public string ModeNote { get; private set; }
        public RcsMapping Mapping { get; private set; }
        public bool Ready => stage == Stage.Run && !finished;
        public bool UsesEngine => Mode == TranslateMode.MainEngine || Mode == TranslateMode.RcsWithEngine;
        public EnginePhase EnginePhase => engine?.Phase ?? EnginePhase.Idle;

        // The main engine's precision so far (m/s): the probe's residual, then the worst burn's.
        public double AchievedDeadband =>
            Math.Max(engineProbe != null && engineProbe.Ok ? engineProbe.Residual : 0, engine?.WorstResidual ?? 0);

        // The guidance limits: the hold tolerances widened when the main engine is coarser than
        // the approach assumes, the contact gate never. An engine too coarse for the port is a
        // refusal: Translate fails (failure_reason) and the limits come back unchanged.
        public DockLimits Widen(DockLimits limits)
        {
            string refusal = MainEngineSequence.DeadbandRefusal(limits, AchievedDeadband);
            if (refusal != null)
            {
                Mode = TranslateMode.Refused;
                ModeNote = refusal;
                Refuse();
                return limits;
            }
            DockLimits wide = MainEngineSequence.ForDeadband(limits, AchievedDeadband, out string note);
            if (note != null) logger.LogInfo($"[Docking] {note}");
            return wide;
        }

        public void StartProbe(bool rcsUsable)
        {
            base.Start();
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.rcsUsable = rcsUsable;
            rcsProbe = new RcsProbeSequence();
            engineProbe = new EngineProbe();
            engine = null;
            Mapping = null;
            rcsAccel = null;
            holdRotation = null;
            fresh = false;
            loggedBurns = 0;
            Mode = TranslateMode.Refused;
            ModeNote = null;
            burn_dV.Compute_Thrust();
            bool engineActive = burn_dV.active_dv > 0;
            status_line = "Probing the thrusters...";
            if (rcsUsable)
                stage = Stage.ProbeRcs;
            else
            {
                Mode = TranslateModes.Choose(false, null, engineActive, out string note);
                ModeNote = note;
                if (Mode == TranslateMode.MainEngine)
                    stage = Stage.ProbeEngine;
                else
                {
                    stage = Stage.Idle;
                    Fail(note);
                }
            }
            logger.LogInfo($"[Docking] translate: probing, RCS {(rcsUsable ? "usable" : "unusable")}, " +
                $"active engine {(engineActive ? $"{burn_dV.active_dv:n2} m/s²" : "none")}");
        }

        public void SetInput(DockState s, DockCommand c)
        {
            state = s;
            command = c;
            fresh = true;
        }

        public void Stop()
        {
            Zero();
            stage = Stage.Idle;
            var autopilot = current_vessel?.Autopilot;
            if (autopilot != null) autopilot.SAS.lockedMode = false;
        }

        // Stops on our own vessel, found by its id, even when another vessel is active: the
        // throttle, X/Y/Z and the SAS lock go through that vessel's own control state. The IL
        // (Redux 26w42c) shows why: ViewController.PerformSetActiveVessel and, every frame,
        // VesselBehavior.OnUpdate zero an inactive vessel's stick and X/Y/Z, but its mainThrottle
        // is kept, so an engine left at a throttle would keep firing. Nothing when it is gone.
        public void Stop(string vesselId)
        {
            stage = Stage.Idle;
            lastRcs = default;
            lastThrottle = 0;
            VesselComponent ours = RendezvousSnapshot.FindVessel(vesselId);
            if (ours == null)
            {
                logger.LogInfo($"[Docking] translate stop: vessel {vesselId} not found, its inputs are left as they are");
                return;
            }
            ours.ApplyFlightCtrlState(new FlightCtrlStateIncremental { mainThrottle = 0f, X = 0f, Y = 0f, Z = 0f });
            var autopilot = ours.Autopilot;
            if (autopilot != null) autopilot.SAS.lockedMode = false;
        }

        void Zero()
        {
            if (current_vessel == null) return;
            current_vessel.SetThrottle(0);
            current_vessel.X = 0;
            current_vessel.Y = 0;
            current_vessel.Z = 0;
            lastRcs = default;
            lastThrottle = 0;
        }

        public override void Update()
        {
            // Stopped, refused or failed: the inputs were zeroed then, and are the player's again.
            if (current_vessel == null || stage == Stage.Idle || finished) return;
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // Logged once: the controller is finished after this, and Update returns above.
                logger.LogWarning($"[Docking] translate: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                stage = Stage.Idle;
                try { Zero(); } catch (Exception) { }
                Fail($"Translate stopped on an error: {e.GetType().Name}");
            }
        }

        void Tick()
        {
            bool had = fresh;
            fresh = false;
            var frame = PortFrame.From(state.OurAxis, state.OurRoll);
            ICoordinateSystem celestial = current_vessel.VesselComponent?.Orbit?.referenceBody?.SimulationObject?.transform?.celestialFrame;
            if (!had || !frame.Ok || celestial == null)
            {
                Zero();
                status_line = !had ? "No docking state this frame" : "Our port's frame is unreadable";
                return;
            }
            burn_dV.Compute_Thrust();
            double maxAccel = burn_dV.active_dv;

            switch (stage)
            {
                case Stage.ProbeRcs: ProbeRcs(frame, celestial, maxAccel); break;
                case Stage.ProbeEngine: ProbeEngine(frame, celestial, maxAccel); break;
                case Stage.Run: Run(frame, celestial, maxAccel); break;
            }
        }

        void ProbeRcs(PortFrame frame, ICoordinateSystem celestial, double maxAccel)
        {
            Hold(celestial);
            current_vessel.SetThrottle(0);
            double rate = RendezvousSnapshot.AngularVelocity(current_vessel.VesselComponent, celestial).magnitude * 180 / Math.PI;
            float[] u = rcsProbe.Step(state.Time, state.RelVel, frame, rate);
            SetRcs(u[0], u[1], u[2]);
            if (!rcsProbe.Done) return;

            if (rcsProbe.StartedUnsettled)
                logger.LogInfo($"[Docking] rcs probe started still turning after {RcsProbeSequence.SettleTimeout:n0} s");
            Mapping = rcsProbe.Mapping;
            rcsAccel = rcsProbe.Accel;
            logger.LogInfo($"[Docking] rcs mapping {rcsProbe.Describe()}");
            Mode = TranslateModes.Choose(rcsUsable, Mapping, maxAccel > 0, out string note);
            ModeNote = note;
            if (Mode == TranslateMode.Refused) { Refuse(); return; }
            if (UsesEngine) { stage = Stage.ProbeEngine; return; }
            Begin();
        }

        void ProbeEngine(PortFrame frame, ICoordinateSystem celestial, double maxAccel)
        {
            Hold(celestial);
            SetRcs(0, 0, 0);
            double throttle = engineProbe.Step(state.Time, state.RelVel, frame, maxAccel);
            current_vessel.SetThrottle((float)throttle);
            lastThrottle = throttle;
            if (!engineProbe.Done) return;

            Vector3d d = engineProbe.ThrustLocal;
            logger.LogInfo($"[Docking] main-engine probe: {(engineProbe.Ok ? "ok" : "refused: " + engineProbe.Refusal)}, " +
                $"Δv {engineProbe.Response.magnitude:n4} m/s for {EngineProbe.ProbeDv:n3} asked, thrust along port frame " +
                $"(right {d.x:n3}, up {d.y:n3}, forward {d.z:n3}), {maxAccel:n2} m/s² at full throttle");
            if (!engineProbe.Ok)
            {
                // Only probed when the engine is needed (no RCS, RCS refused, or a dead RCS axis).
                Mode = TranslateMode.Refused;
                ModeNote = $"the main engine cannot translate ({engineProbe.Refusal})";
                Refuse();
                return;
            }
            thrustLocal = engineProbe.ThrustLocal;
            engine = new MainEngineSequence
            {
                TriggerDeadband = Math.Max(MainEngineSequence.AssumedDeadband, engineProbe.Residual),
            };
            logger.LogInfo($"[Docking] main-engine deadband: probe residual {engineProbe.Residual:n4} m/s " +
                $"(the approach assumes {MainEngineSequence.AssumedDeadband:n3}), trigger {engine.TriggerDeadband:n4} m/s");
            Begin();
        }

        void Begin()
        {
            stage = Stage.Run;
            holdRotation = null;
            logger.LogInfo($"[Docking] translate: mode {Mode}" + (ModeNote != null ? $" ({ModeNote})" : ""));
        }

        void Refuse()
        {
            logger.LogInfo($"[Docking] translate refused: {ModeNote}");
            Zero();
            stage = Stage.Idle;
            Fail(ModeNote);
        }

        void Run(PortFrame frame, ICoordinateSystem celestial, double maxAccel)
        {
            if (!command.Valid)
            {
                Zero();
                status_line = "No command";
                return;
            }
            Vector3d error = command.WantedRelVel - state.RelVel;
            Vector3d aimAxis = command.Forward, aimRoll = command.Up;
            double throttle = 0;
            RcsCommand rcs = default;

            if (Mode == TranslateMode.Rcs || Mode == TranslateMode.RcsWithEngine)
                rcs = RcsAllocation.Allocate(Mapping, rcsAccel, error, frame);

            if (engine != null && UsesEngine)
            {
                Vector3d wanted = Mode == TranslateMode.MainEngine ? command.WantedRelVel : state.RelVel + rcs.Uncovered;
                Vector3d omega = RendezvousSnapshot.AngularVelocity(current_vessel.VesselComponent, celestial);
                double rate = omega.magnitude * 180 / Math.PI;
                double rateToPort = MainEngineSequence.RelativeRateDeg(omega, state.TargetAxis, state.TargetAxisRate);
                EngineStep step = engine.Step(state.Time, state.RelVel, wanted, frame.ToWorld(thrustLocal),
                    frame.Forward, command.Forward, rate, maxAccel, rateToPort);
                if (engine.Failure != null)
                {
                    logger.LogInfo($"[Docking] main engine: {engine.Failure}");
                    Zero();
                    stage = Stage.Idle;
                    Fail(engine.Failure);
                    return;
                }
                throttle = step.Throttle;
                if (step.AimBurn)
                    AttitudeAim.BurnPortAxes(frame, thrustLocal, step.BurnDir, out aimAxis, out aimRoll);
                LogBurns();
            }

            SetRcs(rcs.X, rcs.Y, rcs.Z);
            current_vessel.SetThrottle((float)throttle);
            lastThrottle = throttle;
            Aim(frame, celestial, aimAxis, aimRoll);
            status_line = UsesEngine && engine != null && engine.Phase != EnginePhase.Idle
                ? $"Main engine: {engine.Phase}"
                : $"Translating: error {error.magnitude:n3} m/s";
        }

        void LogBurns()
        {
            if (engine.Burns == loggedBurns) return;
            loggedBurns = engine.Burns;
            logger.LogInfo($"[Docking] main-engine burn {engine.Burns}: asked {engine.LastAsked:n4} m/s, flown " +
                $"{engine.LastFlown:n4}, residual {engine.LastResidual:n4} m/s, lowest throttle {engine.MinThrottleSeen:n4}");
            if (engine.LastResidual > 2 * MainEngineSequence.AssumedDeadband)
                logger.LogInfo($"[Docking] main-engine deadband: {engine.WorstResidual:n4} m/s is worse than the " +
                    $"{MainEngineSequence.AssumedDeadband:n3} m/s the approach assumes");
        }

        void SetRcs(float x, float y, float z)
        {
            current_vessel.X = x;
            current_vessel.Y = y;
            current_vessel.Z = z;
            lastRcs = new RcsCommand { X = x, Y = y, Z = z };
        }

        // Holds the attitude the vessel had when the probe started.
        void Hold(ICoordinateSystem celestial)
        {
            var autopilot = current_vessel.Autopilot;
            if (autopilot == null) return;
            if (holdRotation == null) holdRotation = Rotation.Reframed(current_vessel.GetRotation(), celestial);
            Lock(autopilot, holdRotation.Value);
        }

        // Points our port's axis at axis with its roll at roll (zero: any), through the vessel's
        // rotation: the offset between the vessel's frame and the port's is read every frame.
        void Aim(PortFrame port, ICoordinateSystem celestial, Vector3d axis, Vector3d roll)
        {
            var autopilot = current_vessel.Autopilot;
            if (autopilot == null || axis.sqrMagnitude == 0) return;
            Rotation now = Rotation.Reframed(current_vessel.GetRotation(), celestial);
            Vector3d nose = DockingReads.ZupFromYup(now.localRotation * new Vector3d(0, 1, 0));
            Vector3d dorsal = DockingReads.ZupFromYup(now.localRotation * new Vector3d(0, 0, 1));
            if (!AttitudeAim.VesselAxes(nose, dorsal, port, axis, roll, out Vector3d wn, out Vector3d wd)) return;
            QuaternionD q = AttitudeAim.RotationFromNoseAndDorsal(DockingReads.ZupFromYup(wn), DockingReads.ZupFromYup(wd));
            Lock(autopilot, new Rotation(celestial, q));
        }

        static void Lock(VesselAutopilot autopilot, Rotation rotation)
        {
            autopilot.Enabled = true;
            if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                autopilot.SetMode(AutopilotMode.StabilityAssist);
            autopilot.SAS.LockRotation(rotation);
            autopilot.SAS.lockedMode = true;
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Translate", Mode.ToString());
            addRow("RCS X/Y/Z", $"{lastRcs.X:n2} / {lastRcs.Y:n2} / {lastRcs.Z:n2}");
            if (UsesEngine)
            {
                addRow("Main engine", $"{EnginePhase}, throttle {lastThrottle:n3}");
                addRow("Engine deadband", $"{AchievedDeadband:n4} m/s");
            }
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            if (ModeNote != null) st.Warning(ModeNote);
            st.Console(status_line);
        }
    }
}
