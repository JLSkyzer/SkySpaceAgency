using System.Collections.Generic;
using K2D2.Controller;
using KSP.Sim.DeltaV;
// using KTools.UI;
using UnityEngine;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.KSPService
{
    public class BurndV : BaseController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.SettingsFile");

        KSPVessel current_vessel;

        public BurndV()
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        public float burned_dV = 0;

        public void reset()
        {
            burned_dV = 0;
        }


        public Vector3 actual_thrust;
        public float actual_dv;

        public Vector3 full_thrust;
        public float full_dv;

        // Active engines (ignited, not shut down; see IsActive), at full
        // throttle. Landing uses these; full_dv still counts every engine (other autopilots).
        public float active_thrust;   // kN
        public float active_dv;       // m/s², 0 when nothing is active
        public float active_isp;      // s, combined; 0 when unknown
        public double mass;           // t

        readonly List<double> active_thrusts = new List<double>();
        readonly List<double> active_isps = new List<double>();

        public override void FixedUpdate()
        {
            burned_dV += actual_dv * Time.fixedDeltaTime;
        }

        public override void LateUpdate()
        {
            Compute_Thrust();
        }

        // it is the way engine burning is computed in KSP
        public bool Engine_Running(DeltaVEngineInfo engine_info)
        {
            return (engine_info.Engine.EngineIgnited && engine_info.Engine.RequestedMassFlow > 0f);
        }

        // An active engine is ignited and not shut down. IsOperational and IsPropellantStarved
        // depend on the current throttle, so they would drop idle engines (throttle 0 at start).
        public static bool IsActive(DeltaVEngineInfo engine_info)
        {
            var engine = engine_info.Engine;
            return engine != null && engine.EngineIgnited && !engine.EngineShutdown;
        }

        float compute_full_thrust(DeltaVEngineInfo engineInfo)
        {
            var partref = engineInfo.PartInfo.PartRef;
            float staticPressureAtm = partref.StaticPressureAtm;
            if (staticPressureAtm > 0f)
            {
                return engineInfo.Engine.MaxThrustOutputAtm(runningActive: false, useThrustLimiter: true, staticPressureAtm, partref.AtmosphericTemperature, partref.AtmDensity);
            }
            else if (engineInfo.RequiresAir)
            {
                return 0f;
            }
            else
            {
                return engineInfo.Engine.MaxThrustOutputVac();
            }
        }

        // No stale values from a previous vessel on an early return.
        void ClearActive()
        {
            active_thrust = 0;
            active_dv = 0;
            active_isp = 0;
            mass = 0;
        }

        public void Compute_Thrust()
        {
            if (current_vessel.VesselComponent == null)
            {
                ClearActive();
                return;
            }
            VesselDeltaVComponent delta_v = current_vessel.VesselComponent.VesselDeltaV;
            if (delta_v == null)
            {
                ClearActive();
                return;
            }

            mass = current_vessel.VesselComponent.totalMass;

            actual_thrust = Vector3.zero;
            full_thrust = Vector3.zero;
            Vector3 active_vector = Vector3.zero;
            active_thrusts.Clear();
            active_isps.Clear();

            List<DeltaVEngineInfo> engineInfos = delta_v.EngineInfo;
            for (int i = 0; i < engineInfos.Count; i++)
            {
                DeltaVEngineInfo engineInfo = engineInfos[i];

                Vector3 vector = ((engineInfo.Engine != null) ? engineInfo.Engine.ThrustDirRelativePartWorldSpace : (1f * Vector3.back));

                float full = compute_full_thrust(engineInfo);
                actual_thrust += vector * engineInfo.Engine.FinalThrustValue;
                full_thrust += vector * full;

                if (IsActive(engineInfo))
                {
                    active_vector += vector * full;
                    active_thrusts.Add(full);
                    active_isps.Add(engineInfo.IspActual > 0 ? engineInfo.IspActual : engineInfo.IspVac);
                }
            }

            actual_dv = (float)K2D2.Landing.Braking.ThrustMath.Acceleration(actual_thrust.magnitude, mass);
            full_dv = (float)K2D2.Landing.Braking.ThrustMath.Acceleration(full_thrust.magnitude, mass);

            active_thrust = active_vector.magnitude;
            active_dv = (float)K2D2.Landing.Braking.ThrustMath.Acceleration(active_thrust, mass);
            active_isp = (float)K2D2.Landing.Braking.ThrustMath.CombinedIsp(active_thrusts, active_isps);
        }

        // public override void onGUI()
        // {
        //     if (current_vessel.VesselComponent == null) return;
        //     VesselDeltaVComponent delta_v = current_vessel.VesselComponent.VesselDeltaV;
        //     if (delta_v == null)
        //     {
        //         UI_Tools.Error("NO VesselDeltaVComponent");
        //         return;
        //     }
        //     //List<DeltaVEngineInfo> engineInfos = delta_v.EngineInfo;

        //     var vehicle = current_vessel.VesselVehicle;
        //     if (vehicle == null) return;
        //     var mainThrottle = vehicle.mainThrottle;

        //     //UI_Tools.Console($"nb_engines {engineInfos.Count}  ");


        //     UI_Tools.Console($"mainThrottle {mainThrottle}");
        //     // UI_Tools.Console($"actual_thrust  {Tools.printVector(actual_thrust)}  ");
        //     UI_Tools.Console($"actual_dv  {actual_dv:n5}  ");


        //     // UI_Tools.Console($"full_thrust  {Tools.printVector(full_thrust)}  ");
        //     UI_Tools.Console($"full_dv  {full_dv:n5}  ");
        //     UI_Tools.Console($"burned_dV <b> {burned_dV:n5} </b>");
        //     if (GUILayout.Button("Reset"))
        //         burned_dV = 0;
        // }
    }
}
