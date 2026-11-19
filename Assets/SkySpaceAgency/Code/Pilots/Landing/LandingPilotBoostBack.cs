using System;
using System.Globalization;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;
using KTools;

namespace K2D2.Landing
{
    // IanMealworm's boost-back (K2-D2 1.4.0), carried for the K2-D2 flavour (plan 2026-11-19 §4.4,
    // user decision 2): compiled and tested here, flown only when ModIdentity.IsK2D2. In this mod
    // BoostBackStart always answers NotBoostBack, so none of these phases is ever entered and the
    // Atmo profile's precision keeps our meaning.
    //
    // K2-D2 flavour: Atmo profile, land.mode Propulsive, precision on, a stage coming back down
    // inside the atmosphere, Redux's prediction settings right -> BoostBack (BoostBackBurn) ->
    // AtmoReentry (AtmoEntryGuidance) -> Brake (AtmoLandingBurn, steered onto the target) -> Touch
    // Down (our final descent, legs and standing check). 1x warp all the way. Our heat guard stays
    // the authority in the entry (UpdateBoostBack). Stop releases the attitude hold (ReleaseAtmo).
    public partial class LandingPilot
    {
        internal BoostBackBurn boost_back;
        internal AtmoEntryGuidance atmo_guidance;
        // This run is a boost-back (decided by CheckCanStart).
        internal bool boost_back_run = false;

        void CreateBoostBack()
        {
            boost_back = new BoostBackBurn();
            atmo_guidance = new AtmoEntryGuidance(this);
        }

        internal static bool BoostBackMode(Mode m) => m == Mode.BoostBack || m == Mode.AtmoReentry;

        // CheckCanStart: null when this start is not a boost-back (always in this mod), otherwise
        // whether it may start (last_error set when not).
        bool? CheckCanStartBoostBack()
        {
            var atmo = settings_atmo;
            if (!BoostBackStart.Requested(ModIdentity.IsK2D2, LandingProfile.IsAtmospheric, atmo.mode.V, atmo.precision_landing.V))
                return null;

            var vessel = current_vessel.VesselComponent;
            var body = vessel.Orbit.referenceBody;
            var state = new BoostBackState
            {
                atmospheric_profile = LandingProfile.IsAtmospheric,
                method = atmo.mode.V,
                precision = atmo.precision_landing.V,
                landed = vessel.LandedOrSplashed,
                staged = landing_stage_choice > 0,
                suborbital = IsSuborbital(),
                altitude_m = vessel.AltitudeFromSeaLevel,
                atmosphere_depth_m = body.hasAtmosphere ? body.atmosphereDepth : 0,
                redux_settings_problem = ReduxImpactPrediction.AtmoSettingsProblem(),
            };
            var decision = BoostBackStart.Check(state, ModIdentity.IsK2D2, out string reason);
            logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Landing] boost-back: start check: {0} | suborbital {1}, altitude {2:0} m (atmosphere {3:0} m), landed {4}, staged {5}, Redux settings {6}",
                decision, state.suborbital, state.altitude_m, state.atmosphere_depth_m, state.landed, state.staged,
                state.redux_settings_problem ?? "ok") + (reason != null ? " -> " + reason : ""));
            if (decision == BoostBackDecision.NotBoostBack)
                return null;
            if (decision == BoostBackDecision.Refused)
            {
                last_error = reason;
                return false;
            }
            boost_back_run = true;
            return true;
        }

        // A coast that comes down through the body's surface (ImpactModel's axes).
        internal bool IsSuborbital()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return false;
            var model = new ImpactModel(vessel.mainBody, GeneralTools.Current_UT);
            model.StateAt(vessel.Orbit, model.epoch_UT, out Vector3d r, out Vector3d v);
            return ImpactModel.PeriapsisRadius(r, v, vessel.mainBody.gravParameter) < vessel.mainBody.radius;
        }

        // In orbit, or above the atmosphere: a boost-back is refused there (LandingUI's note).
        internal bool LandsFromOrbit()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return false;
            return !IsSuborbital() || vessel.AltitudeFromSeaLevel > vessel.mainBody.atmosphereDepth;
        }

        // Lets go of the boost-back's attitude hold and throttle (stop, or a phase change by hand).
        void ReleaseAtmo()
        {
            atmo_guidance.Release();
            boost_back.Release();
        }

        // setMode(Brake) of a boost-back: AtmoLandingBurn steered onto the target, with the drag the
        // entry guidance measured (air_planner). False when this run is not a boost-back.
        bool StartBoostBackBrake()
        {
            if (!boost_back_run)
                return false;
            air_brake = true;
            logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[Landing] boost-back: landing burn at {0:0} m, {1:0} m/s | drag k {2:e3}",
                altitude, current_vessel.VesselVehicle.SurfaceSpeed, air_planner.drag_k));
            current_executor.setController(atmo_landing_burn);
            atmo_landing_burn.Start(steer_to_target: true);
            return true;
        }

        // Every frame in BoostBack or AtmoReentry, before the controller: 1x, and our heat guard's
        // throttle handed to the entry guidance while the guard is on.
        void UpdateBoostBack()
        {
            TimeWarpTools.SetRateIndex(0, false);
            atmo_guidance.heat_guard_throttle = mode == Mode.AtmoReentry && heat_guard.Active ? heat_guard.Throttle : double.NaN;
        }

        // A boost-back phase finished: the burn hands over to the entry guidance, the entry to the
        // landing burn.
        void OnBoostBackPhaseEnd()
        {
            if (mode == Mode.BoostBack)
                setMode(Mode.AtmoReentry);
            else
                setMode(Mode.Brake);
        }
    }
}
