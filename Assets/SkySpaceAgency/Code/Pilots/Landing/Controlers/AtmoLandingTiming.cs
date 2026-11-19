using System;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // When to start the regular (not precision) landing burn on a body with an atmosphere, for a
    // vessel already falling in the air (K2-D2 1.4.0, IanMealworm).
    //
    // The regular landing times its burn from the airless fall: the speed the vessel would hit the
    // ground at with no air. In air that speed is far higher than the real one, so it lit the
    // engines kilometres up, braked to a stop, fell, and braked again. Instead, while it coasts
    // down, this measures the vessel's drag from how it actually slows, and AtmoBurnPlanner works
    // out the last moment a burn still ends just above the ground.
    //
    // Here (LandingPilot.AirTimingApplies) only in the coasting phases of a vessel already inside
    // the air; the descent from orbit keeps its own coast (AeroCoast, AeroBrake.Find).
    public class AtmoLandingTiming
    {
        // Drag is sampled over this long (a frame's change in speed is too noisy).
        const double sample_s = 0.5;
        const double plan_interval_s = 0.5;
        // The planner's flat-ground fall is only good for the last few tens of kilometres.
        public const double plan_below_m = 35000;

        readonly AtmoBurnPlanner planner;
        ImpactModel model;
        VesselComponent model_vessel;
        CelestialBodyComponent model_body;
        Vector3d previous_velocity;
        double previous_UT = double.NaN, last_plan_UT = double.NaN;

        public AtmoBurnPlanner.Plan plan { get; private set; }
        // UT the braking should start, once there's a plan.
        public double start_UT { get; private set; } = double.NaN;
        public bool has_plan => !double.IsNaN(start_UT);
        public AtmoBurnPlanner Planner => planner;

        public AtmoLandingTiming(AtmoBurnPlanner planner)
        {
            this.planner = planner;
        }

        public void Reset()
        {
            planner.Reset();
            model = null;
            previous_UT = last_plan_UT = double.NaN;
            ClearPlan();
        }

        // Forgets the plan (once its burn has started) but keeps the drag measured so far.
        public void ClearPlan()
        {
            start_UT = double.NaN;
            last_plan_UT = double.NaN;
            plan = default;
        }

        // The game's body for the planner (its density by height, through the game's own pressure
        // and temperature curves), kept per body so the planner's density table is built once.
        static AtmoBurnPlanner.Air cached_air;
        static CelestialBodyComponent cached_air_body;

        public static AtmoBurnPlanner.Air AirOf(CelestialBodyComponent body)
        {
            if (body != cached_air_body || cached_air == null)
            {
                cached_air_body = body;
                cached_air = new AtmoBurnPlanner.Air(body.Name, body.radius, body.gravParameter,
                    body.hasAtmosphere ? body.atmosphereDepth : 0,
                    altitude => body.GetDensity(body.GetPressure(altitude), body.GetTemperature(altitude)));
            }
            return cached_air;
        }

        // Called every frame while coasting. height: above the ground; max_accel: full thrust per
        // unit of mass; final_speed: the speed the braking ends at.
        public void Update(VesselComponent vessel, double height, double max_accel, double final_speed, bool engines_off)
        {
            var body = vessel.mainBody;
            var air = AirOf(body);
            double now = GeneralTools.Current_UT;
            // A different vessel or body (switching vessels, a sphere-of-influence change) starts
            // the measurement over.
            if (vessel != model_vessel || body != model_body)
            {
                Reset();
                model_vessel = vessel;
                model_body = body;
            }
            if (model == null)
                model = new ImpactModel(body, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            Vector3d v_air = model.AirVelocity(r, v);

            if (double.IsNaN(previous_UT) || !engines_off)
            {
                previous_velocity = v;
                previous_UT = now;
            }
            else if (now - previous_UT >= sample_s)
            {
                Vector3d gravity = r * (-body.gravParameter / Math.Pow(r.magnitude, 3));
                Vector3d accel = (v - previous_velocity) / (now - previous_UT) - gravity;
                previous_velocity = v;
                previous_UT = now;

                double air_speed = v_air.magnitude;
                if (air_speed > 10 && vessel.AltitudeFromSeaLevel < body.atmosphereDepth)
                {
                    Vector3d flow = v_air / air_speed;
                    Vector3d up = r.normalized;
                    Vector3d lift_up = up - flow * Vector3d.Dot(up, flow);
                    double lift_up_accel = lift_up.magnitude > 1e-3 ? Vector3d.Dot(accel, lift_up.normalized) : 0;
                    planner.MeasureDrag(air, -Vector3d.Dot(accel, flow), lift_up_accel, vessel.AltitudeFromSeaLevel, air_speed, now);
                }
            }

            if (!planner.has_drag || vessel.AltitudeFromSeaLevel > plan_below_m)
                return;
            if (!double.IsNaN(last_plan_UT) && now - last_plan_UT < plan_interval_s)
                return;
            last_plan_UT = now;

            Vector3d vertical = r.normalized;
            double v_vertical = Vector3d.Dot(v_air, vertical);
            double v_horizontal = (v_air - vertical * v_vertical).magnitude;
            double ground_altitude = vessel.AltitudeFromSeaLevel - height;
            plan = planner.Compute(air, height, ground_altitude, v_horizontal, v_vertical, max_accel,
                AtmoLandingBurn.final_height_m, final_speed, double.NaN);
            // No plan (no thrust): back to the airless timing rather than a stale start.
            start_UT = plan.found ? now + plan.handover_in_s : double.NaN;
        }
    }
}
