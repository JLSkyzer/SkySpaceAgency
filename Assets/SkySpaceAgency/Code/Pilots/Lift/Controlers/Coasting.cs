
using SkySpaceAgency.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
// using UnityEngine;
using UnityEngine.UIElements;
using SkySpaceAgency.UI;
using SkySpaceAgency.Controller;
using SkySpaceAgency.Node;
namespace SkySpaceAgency.Lift
{
    // Coasting from engine cut-off to the edge of the atmosphere, holding surface prograde and
    // (with Coasting Warp on) warping toward the edge.
    public class Coasting : ExecuteController
    {
        LiftSettings lift_settings = null;

        KSPVessel current_vessel;

        LiftPilot lift;

        public Coasting(LiftPilot lift, LiftSettings lift_settings)
        {
            current_vessel = SkySpaceAgencyPlugin.Instance.current_vessel;
            this.lift = lift;
            this.lift_settings = lift_settings;
        }

        TurnTo turn_to = null;

        double densityAtm = 0;
        double duration_to_atm = 0;
        public float current_altitude_km = 0;

        public override void Start()
        {
            base.Start();

            current_vessel.SetThrottle(0);
            TimeWarpTools.SetRateIndex(0, false);
            turn_to = new TurnTo();
            turn_to.StartProGrade(SpeedDisplayMode.Surface);
        }

        // Narrative feedback only here - Altitude/Atm Density are in UpdateInfoRows below (LIFT INFO
        // table).
        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status("Coasting");

            if (!turn_to.finished)
                st.Console(turn_to.status_line);
            else if (lift_settings.coasting_warp.V)
                st.Console($"End warp : {StrTool.DurationToString(duration_to_atm)} x{TimeWarpTools.CurrentRate}");
        }

        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Altitude", $"{current_altitude_km:n2} km");
            addRow("Atm Density", $"{densityAtm:n2}");
        }

        // K2-D2 1.4.0 fixes: the sea altitude (atmosphereDepth is measured from sea level, not from
        // the terrain under the vessel), the wanted warp index itself (it was one too high), no warp
        // while the vertical speed is not positive, and the coast still runs with Coasting Warp off
        // (it used to skip straight to Adjust, inside the atmosphere). The pause for the player's own
        // circularization moved to the end of Adjust (LiftPilot.NextMode).
        public override void Update()
        {
            finished = false;

            CelestialBodyComponent mainBody = current_vessel.currentBody();
            // target_Ap_km: the destination apoapsis, or the transfer goal's override.
            if (lift_settings.target_Ap_km < mainBody.atmosphereDepth / 1000)
            {
                lift.EndLiftPilot(false, "Warning Ap is under Atm. limit");
                return;
            }

            double altitude_m = current_vessel.GetSeaAltitude();
            current_altitude_km = (float)(altitude_m / 1000);
            densityAtm = mainBody.GetPressure(altitude_m);

            double left_m = mainBody.atmosphereDepth - altitude_m;
            if (left_m < 0)
            {
                finished = true;
                return;
            }

            turn_to.Update();

            // With Coasting Warp off only the warp is skipped: the coast still has to reach the
            // edge before Adjust can make up what drag took off apoapsis.
            if (!lift_settings.coasting_warp.V)
                return;

            if (!turn_to.finished)
            {
                TimeWarpTools.SetRateIndex(0, false);
                return;
            }

            double vertical_speed = current_vessel.VesselVehicle.VerticalSpeed;
            duration_to_atm = vertical_speed > 0 ? left_m / vertical_speed : 0;
            TimeWarpTools.SetRateIndex(WarpToSettings.compute_wanted_warp_index(duration_to_atm), false);
        }
    }
}
