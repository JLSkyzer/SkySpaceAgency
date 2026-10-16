using K2D2.Controller;
using K2D2.KSPService;
using KSP.Messages;
using KSP.Sim;
using KSP.Sim.Maneuver;
using KTools;

// using KTools.UI;
// using K2D2.InfosPages;
using UnityEngine;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Node
{
    public class NodeExPilot : Pilot
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.NodeExecute");

        public static NodeExPilot Instance { get; set; }

        public ManeuverNodeData next_maneuver_node = null;
        public ManeuverNodeData execute_node = null;

        public NodeExSettings settings = new NodeExSettings();

        // Sub Pilots
        TurnTo turn;
        WarpTo warp;
        BurnManeuver burn;
        CircularizeBurn circularize;

        // Used for auto-deleting execute_node once its burn is done (see nextMode()).
        ManeuverCreator maneuver_creator = new ManeuverCreator();

        //  ExecuteController current_pilot = null;
        KSPVessel current_vessel;

        public SingleExecuteController current_executor = new SingleExecuteController();

        NodeExUI ui;

        public NodeExPilot()
        {
            Instance = this;
            debug_mode_only = false;

            _page = ui = new NodeExUI(this);

            K2D2PilotsMgr.Instance.RegisterPilot("Node", this);

            sub_contollers.Add(current_executor);
            current_vessel = K2D2_Plugin.Instance.current_vessel;

            GeneralTools.Game.Messages.Subscribe<VesselChangedMessage>(OnActiveVesselChanged);

            turn = new TurnTo();
            warp = new WarpTo();
            burn = new BurnManeuver();
            circularize = new CircularizeBurn();
        }

        public void OnActiveVesselChanged(MessageCenterMessage msg)
        {
            Stop();
        }

        public enum Mode
        {
            Off,
            Turn,
            Warp,
            Burn,
            // "Circularize at AP/PE": closed-loop CircularizeBurn, no node.
            Circularize
        }

        public Mode mode = Mode.Off;

        public void setMode(Mode mode)
        {
            if (mode == this.mode)
                return;

            logger.LogInfo("setMode " + mode);  
            if (mode == Mode.Off)
            {
                TimeWarpTools.SetRateIndex(0, false);
                current_executor.setController(null);
                this.mode = mode;
                return;
            }

            if (next_maneuver_node == null)
                checkManeuver();

            if (next_maneuver_node == null)
            {
                Stop();
                return;
            }

            this.mode = mode;
            switch (mode)
            {
                case Mode.Off:
                    current_executor.setController(null);
                    break;
                case Mode.Turn:
                    // start
                    execute_node = next_maneuver_node;
                    current_executor.setController(turn);
                    turn.StartManeuver(execute_node);
                    break;
                case Mode.Warp:
                    if (!settings.auto_warp.V)
                    {
                        setMode(Mode.Burn);
                        return;
                    }
                    current_executor.setController(warp);
                    warp.StartManeuver(execute_node);
                    break;
                case Mode.Burn:
                    current_executor.setController(burn);
                    burn.StartManeuver(execute_node);
                    break;
            }

            logger.LogInfo("setMode " + mode);
        }

        public bool canStart()
        {
            if (next_maneuver_node == null)
                return false;

            var dt = GeneralTools.remainingStartTime(next_maneuver_node);
            if (dt < 0)
            {
                return false;
            }

            return true;
        }

        public void nextMode()
        {
            if (mode == Mode.Off)
            {
                Start();
                return;
            }
            if (mode == Mode.Burn)
            {
                // Auto-delete the node once its burn is done - there's nothing left to do with it.
                // RemoveNode, not RemoveAllNodes: a multi-node Flight Plan might have more queued
                // up behind this one, and checkManeuver() below picks the next one up once this
                // one's gone.
                maneuver_creator.Update();
                maneuver_creator.RemoveNode(execute_node);

                Stop();
                if (settings.pause_on_end.V)
                    TimeWarpTools.SetIsPaused(true);
                return;
            }

            var next = this.mode + 1;
            setMode(next);
        }

        public void UpdateUI()
        {
            if (!ui.isVisible) return;

            ui.status_bar.Reset();

            var st = ui.status_bar;
            if (!isRunning)
            {
                // K2's own face (see k2_avatar in node.uxml) now sits right next to this line, so
                // it no longer needs its own "K2:" text prefix to read as K2 talking - same for
                // every st.Warning()/st.Status() call in TurnTo.cs/BurnManeuvre.cs/WarpTo.cs below.
                if (next_maneuver_node == null)
                {
                    // Only worth showing while still true - a node plotted some other way since
                    // (e.g. by hand on the map) makes the old refusal moot.
                    if (circularize_error != null)
                        st.Warning(circularize_error);
                    else
                        st.Status("No Node Created");
                    return;
                }

                circularize_error = null;

                if (!valid_maneuver)
                {
                    st.Warning("Node data glitched");
                    st.Console("That's a known KSP2 map-loading glitch - pop open the map view and I'll pick it back up.");
                    return;
                }

                if (!canStart())
                {
                    st.Warning("That node's already in the past - replot it.");
                    return;
                }

                st.Status("Ready to execute node!");
            }
            else
                current_executor.updateUI(page.panel, st);
        }

        // Set when a circularize run is refused or stops on an error (see StartCircularize) so
        // UpdateUI can tell the player why nothing happened, instead of the button silently doing
        // nothing. Cleared on the next successful call.
        public string circularize_error = null;

        public bool valid_maneuver = false;

        public bool checkManeuver()
        {
            next_maneuver_node = current_vessel.GetNextManeuveurNode();
            valid_maneuver = false;
            if (next_maneuver_node == null)
            {
                Stop();
                return false;
            }

            double ut;

            var plan_solver = current_vessel.GetPlanSolver();
            if (plan_solver == null)
            {
                Stop();
                return false;
            }

            // check that the maneuver is well declared.
            Vector velocity_after_maneuver = plan_solver.GetVelocityAfterFirstManeuver(out ut);
            if (ut == 0)
            {
                // error
                Stop();
                return false;
            }

            valid_maneuver = true;
            return true;
        }

        // "Circularize at AP"/"Circularize at PE" buttons (Node tab). No node: a dedicated run of
        // this pilot (Mode.Circularize) delegates to the closed-loop CircularizeBurn, because a
        // node executor follows the game's maneuver model, which is wrong when the active stage is
        // nearly empty (see CircularizeBurn). Start/Stop reflect the run; a refusal is shown
        // through circularize_error and the pilot does not start.
        public void StartCircularize(bool atApoapsis)
        {
            if (K2D2_Plugin.Instance.current_vessel == null) return;

            // Reset every pilot first (this one included), same as every other "start a pilot" path.
            K2D2_Plugin.ResetControllers();
            TimeWarpTools.SetIsPaused(false);

            circularize.allow_warp = settings.auto_warp.V;
            circularize.Start(atApoapsis);
            if (circularize.failure_reason != null)
            {
                circularize_error = circularize.failure_reason;
                return;
            }

            circularize_error = null;
            execute_node = null;
            logger.LogInfo("setMode " + Mode.Circularize);
            mode = Mode.Circularize;
            current_executor.setController(circularize);
            // Pilot's setter, not ours: ours would start the node sequence.
            base.isRunning = true;
        }

        void UpdateCircularize()
        {
            base.Update();

            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.O))
                Stop();
            else if (circularize.failure_reason != null)
            {
                circularize_error = circularize.failure_reason;
                Stop();
            }
            else if (circularize.finished)
            {
                Stop();
                if (settings.pause_on_end.V)
                    TimeWarpTools.SetIsPaused(true);
            }

            UpdateUI();
        }

        public override void Update()
        {
            if (mode == Mode.Circularize)
            {
                UpdateCircularize();
                return;
            }

            checkManeuver();
            base.Update();

            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.O))
                isRunning = !isRunning;

            if (isRunning)
            {
                if (execute_node == null)
                {
                    Stop();
                    return;
                }

                //stagingController.CheckStaging();
                double UT = 0;
                switch (settings.start_mode.V)
                {
                    case NodeExSettings.StartMode.precise:
                        UT = execute_node.Time;
                        break;
                    case NodeExSettings.StartMode.half_duration:
                        UT = execute_node.Time - execute_node.BurnDuration / 2;
                        break;
                    case NodeExSettings.StartMode.constant:
                        UT = execute_node.Time - settings.start_before.V;
                        break;
                }

                warp.UT = UT;
                burn.UT = UT;
            }
            else
            {
           
            }
       
            if (current_executor.finished)
            {
                // auto next
                nextMode();
            }

            UpdateUI();
        }

  

        public override bool isRunning
        {
            get { return mode != Mode.Off; }
            set
            {
                if (value == base.isRunning)
                    return;
  
                if (!value)
                {
                    // stop
                    if (current_vessel != null)
                        current_vessel.SetThrottle(0);

                    setMode(Mode.Off);          
                }
                else
                {
                    // reset controller to desactivate other controllers.
                    K2D2_Plugin.ResetControllers();
                    TimeWarpTools.SetIsPaused(false);
                
                    setMode(Mode.Turn);
                }

                // send call backs
                base.isRunning = value; 
            }
        }

        public void Start()
        {
            isRunning = true;       
        }

        public void Stop()
        {
            isRunning = false;
        }

        public override void onReset()
        {
            Stop();
        }

        internal string ApiStatus()
        {
            string status = "";

            if (mode == Mode.Circularize)
                return $"Circularizing: {current_executor.status_line}";

            if (next_maneuver_node == null)
            {
                status = "No Maneuver Node";
            }
            else
            {
                if (!valid_maneuver) status = "Invalid Maneuver Node";
                // else if (!NodeExecute.Instance.canStart()) status = "No Future Maneuver Node";
                else if (isRunning)
                {
                    if (mode == Mode.Off) status = "Off";
                    else if (mode == Mode.Turn)
                    {
                        status = $"Turning: {current_executor.status_line}";
                        // report angle deviation?
                    }
                    else if (mode == Mode.Warp)
                    {
                        status = $"Warping: {current_executor.status_line}";
                        // report time to node?
                    }
                    else if (mode == Mode.Burn)
                    {
                        if (Game.UniverseModel.UniverseTime < next_maneuver_node.Time)
                        {
                            status = $"Waiting to Burn: {current_executor.status_line}";
                        }
                        else
                        {
                            status = $"Burning: {current_executor.status_line}";
                            // report burn remaining (delta-v?)
                        }
                    }
                }
                else status = "Done";
                // else status = "Unknown";
            }
            return status;
        }
    }
}
