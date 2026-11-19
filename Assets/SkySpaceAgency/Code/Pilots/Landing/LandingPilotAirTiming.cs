using System;
using System.Globalization;
using K2D2.KSPService;
using KSP.Sim.impl;
using KTools;
using UnityEngine;

namespace K2D2.Landing
{
    // The landing burn of a vessel already falling inside the air, on a regular propulsive landing
    // (K2-D2 1.4.0, IanMealworm; plan 2026-11-19 item L4). The braking simulation
    // (BrakeSimulator.FindStart) has no drag: in air it lit the engines far too early, braked to a
    // stop kilometres up, fell, and braked again. Here, while the vessel coasts down (Pause, QuickWarp,
    // RotationWarp, Waiting), AtmoLandingTiming measures its drag and AtmoBurnPlanner gives the last
    // ignition whose braking still ends at AtmoLandingBurn.final_height_m; until a drag is measured
    // the vacuum start stands in (always earlier). The Brake phase then flies AtmoLandingBurn, which
    // re-solves its throttle to end at that height, and hands over to Touch Down there (final
    // descent, legs, standing check unchanged).
    //
    // Not used: the descent from orbit (its atmospheric coast predicts the brake with drag already,
    // AeroCoast/AeroBrake.Find, and its first brake stays as it was), airless bodies, the parachute
    // modes, precision landings, and a landing with a chosen stage (its plan has no drag).
    public partial class LandingPilot
    {
        internal readonly AtmoBurnPlanner air_planner = new AtmoBurnPlanner();
        internal AtmoLandingTiming atmo_timing;
        internal AtmoLandingBurn atmo_landing_burn;

        // The Brake phase in progress flies AtmoLandingBurn (decided when it starts).
        internal bool air_brake = false;
        // On the last compute_startBurn: the air timing applied (air_eligible), and its plan gave the
        // start (air_timed).
        bool air_eligible = false;
        bool air_timed = false;
        float next_air_log = 0;
        const float AirLogInterval = 5;

        void CreateAirTiming()
        {
            atmo_timing = new AtmoLandingTiming(air_planner);
            atmo_landing_burn = new AtmoLandingBurn(this);
        }

        static bool InAir(VesselComponent vessel)
        {
            var body = vessel.Orbit.referenceBody;
            return body.hasAtmosphere && vessel.AltitudeFromSeaLevel < body.atmosphereDepth && !vessel.LandedOrSplashed;
        }

        static bool AirCoastingMode(Mode m) => m == Mode.Pause || m == Mode.QuickWarp || m == Mode.RotationWarp || m == Mode.Waiting;

        // A regular propulsive landing coasting inside the air (or no landing running: the tab's
        // readout of the start).
        bool AirTimingApplies()
        {
            if (!LandingProfile.IsAtmospheric || parachute_mode || PrecisionActive || reentry_run || StagedBraking)
                return false;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null || !InAir(vessel))
                return false;
            return !isRunning || AirCoastingMode(mode);
        }

        static double AirAccel(BurndV burn) => burn.active_dv > 0 ? burn.active_dv : burn.full_dv;

        // computeValues, every frame before compute_startBurn: the drag measurement and the plan.
        void UpdateAirTiming()
        {
            if (!AirTimingApplies() || !collision_detected)
                return;
            atmo_timing.Update(current_vessel.VesselComponent, altitude, AirAccel(burn_dV),
                AtmoLandingBurn.FinalSpeed(settings), current_vessel.GetThrottle() == 0);
        }

        // compute_startBurn: the air plan's start replaces the vacuum one once there is a plan. The
        // player's "burn before" stays an extra margin, as with the vacuum start.
        void ApplyAirTiming(double now_ut)
        {
            air_eligible = AirTimingApplies();
            air_timed = air_eligible && atmo_timing.has_plan;
            if (!air_timed)
                return;
            double vacuum_start = startBurn_UT;
            startBurn_UT = atmo_timing.start_UT - settings.burn_before.V;
            LogAirTiming(now_ut, vacuum_start);
        }

        void LogAirTiming(double now, double vacuum_start)
        {
            float now_real = Time.realtimeSinceStartup;
            if (!isRunning || now_real < next_air_log)
                return;
            next_air_log = now_real + AirLogInterval;
            var plan = atmo_timing.plan;
            var vessel = current_vessel.VesselComponent;
            logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Landing] air timing: in {0}: brake in {1:0.0} s (vacuum start in {2:0.0} s), braking planned to end {3:0} m up | " +
                "drag k {4:e3}, lift/drag {5:0.00} | height {6:0} m, sea altitude {7:0} m, speed {8:0} m/s, thrust accel {9:0.00} m/s²",
                mode, startBurn_UT - now, vacuum_start - now, plan.stop_height_m, air_planner.drag_k, air_planner.lift_over_drag,
                altitude, vessel.AltitudeFromSeaLevel, current_vessel.VesselVehicle.SurfaceSpeed, AirAccel(burn_dV)));
        }

        // setMode(Brake): AtmoLandingBurn instead of Touch Down's full brake, when the vessel coasted
        // down inside the air with the air timing.
        bool StartAirBrake()
        {
            var vessel = current_vessel?.VesselComponent;
            if (!air_eligible || vessel == null || !InAir(vessel))
                return false;
            air_brake = true;
            logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Landing] air timing: brake at {0:0} m ({1:0} m above sea), {2:0} m/s, Mach {3:0.00}, {4} | drag k {5:e3}",
                altitude, vessel.AltitudeFromSeaLevel, current_vessel.VesselVehicle.SurfaceSpeed, vessel.MachNumber,
                air_timed ? "timed with drag" : "no drag measured yet: vacuum start", air_planner.drag_k));
            atmo_timing.ClearPlan();
            air_timed = false;
            current_executor.setController(atmo_landing_burn);
            atmo_landing_burn.Start(steer_to_target: false);
            return true;
        }
    }
}
