using SkySpaceAgency.KSPService;
using KSP.Sim;
using KTools;
// using KTools.UI;

namespace SkySpaceAgency.Controller
{
    class AttitudeSettings
    {
        // Renamed from warp.* in K2-D2 1.4.0; a value saved under the old key is read once, when
        // the new one is absent.
        public static ClampSetting<float> elevation = new(SettingsFile.Instance.Renamed("attitude.elevation", "warp.elevation"), 0, -90, 90);
        public static ClampSetting<float> heading = new(SettingsFile.Instance.Renamed("attitude.heading", "warp.heading"), 0, 0, 360);
    }

    public class AttitudePilot : Pilot
    {
        public static AttitudePilot Instance { get; set; }

        KSPVessel current_vessel;

        public AttitudePilot()
        {
            _page = new AttitudeUI(this);

            // logger.LogMessage("LandingController !");
            current_vessel = SkySpaceAgencyPlugin.Instance.current_vessel;

            Instance = this;
            debug_mode_only = false;
            SkySpaceAgencyPilotsMgr.Instance.RegisterPilot("Attitude", this);
        }

        public override void onReset()
        {
            isRunning = false;
        }

        bool _active = false;
        public override bool isRunning
        {
            get { return _active; }
            set
            {
                if (value == _active)
                    return;

                if (!value)
                {
                    // stop
                    // var current_vessel = SkySpaceAgencyPlugin.Instance.current_vessel;
                    // if (current_vessel != null)
                    //     current_vessel.SetThrottle(0);

                    _active = false;
                }
                else
                {
                    // reset controller to desactivate other controllers.
                    SkySpaceAgencyPlugin.ResetControllers();
                    _active = true;

                    var autopilot = current_vessel.Autopilot;

                    // force autopilot
                    autopilot.Enabled = true;
                    autopilot.SetMode(AutopilotMode.StabilityAssist);
                }

                base.isRunning = _active;
            }
        }

        Vector3d direction = Vector3d.zero;

        public override void Update()
        {
            if (!isRunning) return;

            if (current_vessel == null) return;
            var autopilot = current_vessel.Autopilot;

            // force autopilot
            autopilot.Enabled = true;

            var telemetry = SASTool.getTelemetry();
            if (telemetry == null)
                return;

            var up = telemetry.HorizonUp;

            direction = QuaternionD.Euler(-AttitudeSettings.elevation.V, AttitudeSettings.heading.V, 0) * Vector3d.forward;
            Vector direction_vector = new Vector(up.coordinateSystem, direction);

            autopilot.SAS.lockedMode = false;
            autopilot.SAS.SetTargetOrientation(direction_vector, false);
        }

        // public override void onGUI()
        // {
        //     if (SkySpaceAgencyPlugin.Instance.settings_visible)
        //     {
        //         SkySpaceAgencySettings.onGUI();
        //         return;
        //     }

        //     UI_Tools.Title("Attitude Pilot");

        //     AttitudeSettings.elevation = UI_Tools.ElevationSlider("attitude.elevation", AttitudeSettings.elevation);
        //     AttitudeSettings.heading = UI_Tools.HeadingControl("attitude.heading", AttitudeSettings.heading);
        //     isRunning = UI_Tools.BigToggleButton(isRunning, "Start", "Stop");
        // }

    }
}
