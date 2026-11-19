using System.Collections.Generic;
using SkySpaceAgency.Controller;
using KSP.Sim.DeltaV;
// using KTools.UI;
using UnityEngine;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace SkySpaceAgency.KSPService
{
    public class BurndV : BaseController
    {
        public ILogger logger = ModIdentity.Logger("BurndV");

        KSPVessel current_vessel;

        public BurndV()
        {
            current_vessel = SkySpaceAgencyPlugin.Instance.current_vessel;
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

        // K2-D2 1.4.0's names for the same thing (Node uses them): only the engines lit now, by
        // our definition of active (IsActive), not upstream's IsOperational.
        public float active_full_dv => active_dv;
        public bool has_active_engine => active_dv > 0;

        // A crash: the vessel whose engines already threw once (logged once per vessel).
        object nre_logged_for;

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
                if (engineInfo?.Engine == null || engineInfo.PartInfo?.PartRef == null)
                    continue;

                // The delta-v list can still hold an engine for a frame after it is destroyed (in a
                // crash, say), and the game's thrust getters then throw on its missing data. That
                // engine has no thrust to count either way (K2-D2 1.4.0).
                Vector3 vector, actual;
                float full;
                bool active;
                try
                {
                    vector = engineInfo.Engine.ThrustDirRelativePartWorldSpace;
                    full = compute_full_thrust(engineInfo);
                    actual = vector * engineInfo.Engine.FinalThrustValue;
                    active = IsActive(engineInfo);
                }
                catch (System.NullReferenceException)
                {
                    var vessel = current_vessel.VesselComponent;
                    if (!ReferenceEquals(nre_logged_for, vessel))
                    {
                        nre_logged_for = vessel;
                        logger.LogWarning($"[BurndV] an engine of {vessel?.Name} could not be read (destroyed?): skipped");
                    }
                    continue;
                }

                actual_thrust += actual;
                full_thrust += vector * full;
                if (active)
                {
                    active_vector += vector * full;
                    active_thrusts.Add(full);
                    active_isps.Add(engineInfo.IspActual > 0 ? engineInfo.IspActual : engineInfo.IspVac);
                }
            }

            actual_dv = (float)SkySpaceAgency.Landing.Braking.ThrustMath.Acceleration(actual_thrust.magnitude, mass);
            full_dv = (float)SkySpaceAgency.Landing.Braking.ThrustMath.Acceleration(full_thrust.magnitude, mass);

            active_thrust = active_vector.magnitude;
            active_dv = (float)SkySpaceAgency.Landing.Braking.ThrustMath.Acceleration(active_thrust, mass);
            active_isp = (float)SkySpaceAgency.Landing.Braking.ThrustMath.CombinedIsp(active_thrusts, active_isps);
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
