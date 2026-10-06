using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing.Braking;
using KSP.Sim;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    // Mode.Parachute, the only phase of a parachute landing, without any engine:
    // 1. hold surface retrograde; the time warp stays in the player's hands;
    // 2. arm every STOWED parachute once, at start; the game opens them by their own settings;
    // 3. once one is DEPLOYED, release SAS (StabilityAssist) and deploy the legs;
    // 4. LandingPilot ends the run on LandedOrSplashed: this controller never sets finished.
    public class ParachuteDescent : ExecuteController
    {
        readonly ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.ParachuteDescent");

        readonly LandingPilot landing;

        public ParachuteCounts counts;
        public bool too_fast_under_canopy = false;
        public bool no_parachute_left = false;
        bool canopy_open = false;

        public ParachuteDescent(LandingPilot landing)
        {
            this.landing = landing;
        }

        public override void Start()
        {
            base.Start();
            canopy_open = false;
            too_fast_under_canopy = false;
            no_parachute_left = false;

            var vessel = K2D2_Plugin.Instance.current_vessel?.VesselComponent;
            counts = Parachutes.Count(vessel);
            int armed = Parachutes.ArmStowed(vessel);
            logger.LogInfo($"[Landing] parachute: start, {counts}; armed {armed} stowed parachute(s)");
            status_line = "Holding retrograde";
        }

        public override void Update()
        {
            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;

            counts = Parachutes.Count(vessel);

            if (!canopy_open)
            {
                current_vessel.SetSpeedMode(SpeedDisplayMode.Surface);
                SASTool.setAutoPilot(AutopilotMode.Retrograde);

                if (counts.Deployed > 0)
                {
                    canopy_open = true;
                    SASTool.setAutoPilot(AutopilotMode.StabilityAssist);
                    logger.LogInfo($"[Landing] parachute: canopy open at {landing.altitude:n0} m, " +
                        $"{landing.current_falling_speed:n1} m/s down, SAS released; {counts}");
                    landing.gear.Deploy(vessel, "parachute open");
                }
            }

            bool too_fast = ParachuteFeasibility.TooFastUnderCanopy(counts.Deployed, landing.altitude, landing.current_falling_speed);
            if (too_fast && !too_fast_under_canopy)
                logger.LogWarning($"[Landing] parachute: too fast under canopy: {landing.current_falling_speed:n1} m/s " +
                    $"at {landing.altitude:n0} m; {counts}");
            too_fast_under_canopy = too_fast;

            bool none_left = ParachuteFeasibility.NoParachuteLeft(counts.Usable, vessel.LandedOrSplashed);
            if (none_left && !no_parachute_left)
                logger.LogWarning($"[Landing] parachute: no parachute left; {counts}");
            no_parachute_left = none_left;

            if (canopy_open)
                status_line = "Under canopy";
            else if (counts.Armed + counts.SemiDeployed > 0)
                status_line = "Holding retrograde, waiting for the parachutes to open";
            else
                status_line = "Holding retrograde";
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Canopy", canopy_open ? "Open" : "Not open yet");
        }
    }
}
