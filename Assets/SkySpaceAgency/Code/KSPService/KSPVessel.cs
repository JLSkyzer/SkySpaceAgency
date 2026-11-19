using System;
using System.Collections.Generic;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using KSP.Sim.State;
using UnityEngine;

namespace SkySpaceAgency.KSPService
{
    public class KSPVessel
    {
        public GameInstance Game => GameManager.Instance == null ? null : GameManager.Instance.Game;

        private TelemetryDataProvider telemetryDataProvider;

        public VesselComponent VesselComponent;

        public VesselVehicle VesselVehicle;

        static KSPVessel _current;

        public static KSPVessel current
        {
            get
            {
                return _current;
            }
        }

        public void Update()
        {
            _current = this;
            VesselComponent = GetActiveSimVessel();
            VesselVehicle = GetActiveSimVehicle();
            if (Game == null || Game.ViewController == null) return;
            telemetryDataProvider = this.Game.ViewController.DataProvider.TelemetryDataProvider;
        }

        //==================================================================================================================

        /// <summary>
        /// Retrieve the active vessel from the game
        /// </summary>
        /// <returns> VesselComponent</returns>
        public VesselComponent GetActiveSimVessel()
        {
            if (Game == null) return null;
            if (Game.ViewController == null) return null;
            if (!Game.ViewController.TryGetActiveSimVessel(out var vessel)) return null;
            return vessel as VesselComponent;
        }

        public VesselVehicle GetActiveSimVehicle()
        {
            if (Game == null) return null;
            if (Game.ViewController == null) return null;
            if (!Game.ViewController.TryGetActiveVehicle(out var vehicle)) return null;
            return vehicle as VesselVehicle;
        }

        ///////////////////////// from former Vessel Infos /////////////

        public CelestialBodyComponent currentBody()
        {
            if (VesselComponent == null) return null;
            return VesselComponent.mainBody;
        }

        public void SetThrottle(float throttle)
        {
            VesselVehicle = GetActiveSimVehicle();
            // WARNING can only be called from Update not FixedUpdate
            if (VesselVehicle == null) return;

            throttle = Mathf.Clamp01(throttle);

            var update = new FlightCtrlStateIncremental
            {
                mainThrottle = throttle
            };

            VesselVehicle.AtomicSet(update);
        }

        public Vector GetAngularSpeed()
        {
            if (VesselVehicle == null) return new Vector();

            return VesselVehicle.AngularVelocity.relativeAngularVelocity;
        }

        public Rotation GetRotation()
        {
            if (VesselVehicle == null) return new Rotation();
            return VesselVehicle.VehicleTelemetry.Rotation;
        }

        public ManeuverNodeData GetNextManeuveurNode()
        {
            var activeNodes = GetManeuverNodes();
            ManeuverNodeData next_node = (activeNodes != null && activeNodes.Count > 0) ? activeNodes[0] : null;
            return next_node;
        }

        // The vessel's maneuver plan, in the game's order (the next node first); null when unavailable.
        public List<ManeuverNodeData> GetManeuverNodes()
        {
            var maneuvers = Game.SpaceSimulation?.Maneuvers;
            if (maneuvers == null) return null;

            var current_vehicle = VesselVehicle;
            if (current_vehicle == null) return null;

            return maneuvers.GetNodesForVessel(current_vehicle.Guid);
        }

        public ManeuverPlanSolver GetPlanSolver()
        {
            ManeuverPlanSolver solver = null;
            solver = VesselComponent?.Orbiter?.ManeuverPlanSolver;
            return solver;
        }

        public void SetSpeedMode(SpeedDisplayMode mode)
        {
            if (VesselVehicle == null) return;
            VesselVehicle.SetSpeedDisplayMode(mode);
        }

        // Control inputs===================================================================================================

        public float X
        {
            get
            {
                if (VesselVehicle == null) return 0;
                return VesselVehicle.X;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    X = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }
        public float Y
        {
            get
            {
                if (VesselVehicle == null) return 0;
                // Y is mapped to Z yes
                return VesselVehicle.Z;
            }
            set
            {
                if (VesselVehicle == null) return;
                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    Z = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        public float Z
        {
            get
            {
                if (VesselVehicle == null) return 0;
                // Z is mapped to Y
                return -VesselVehicle.Y;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    Y = -value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        public float Pitch
        {
            get
            {
                if (VesselVehicle == null) return 0;

                return VesselVehicle.pitch;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    pitch = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        public float Roll
        {
            get
            {
                if (VesselVehicle == null) return 0;
                return VesselVehicle.roll;
            }
            set
            {
                if (VesselVehicle == null) return;

                value = Mathf.Clamp(value, -1, 1);

                var update = new FlightCtrlStateIncremental
                {
                    roll = value
                };

                VesselVehicle.AtomicSet(update);
            }
        }

        // AutoPilot========================================================================================================

        public VesselAutopilot Autopilot => VesselComponent == null ? null : VesselComponent.Autopilot;

        // Telemetry Data===================================================================================================

        /// <summary>
        /// Retrieve the current activation value of throttle
        /// </summary>
        public double GetThrottle()
        {
            return VesselComponent.flightCtrlState.mainThrottle;
        }

        // =================================================================================================================

        // General Vessel Data===================================================================================================

        public AltimeterDisplayMode GetAltimeterDisplayMode()
        {
            // CORRECTED: an earlier verification pass wrongly changed .GetValue() to .Value here. A
            // live build against the real assemblies proved that wrong (see the former KSPVesselInformation.cs's (deleted in 2026-11)
            // GetManeuverNodeVector for the full explanation) - PropertyExternal<T> has no Value
            // member, only GetValue(). Reverted.
            return Game.ViewController.DataProvider.TelemetryDataProvider.AltimeterDisplayMode.GetValue();
        }

        public bool Landed()
        {
            return VesselComponent.Landed;
        }

        /// Correct the altitude unsing the vessel radius
        public double GetApproxAltitude()
        {
            var altitude_from_ground = GetGlobalGroundAltitude();
            // VesselComponent.SimulationObject.objVesselBehavior.ShowCenterOfMass;
            // var center = VesselComponent.SimulationObject.objVesselBehavior.BoundingBox.center.y - VesselComponent.SimulationObject.objVesselBehavior.BoundingBox.extents.z;
            //var bounding_sphere = VesselComponent.SimulationObject.objVesselBehavior.BoundingSphere;
            var result = altitude_from_ground - VesselComponent.SimulationObject.objVesselBehavior.BoundingSphere.radius;
            return result;
        }

        public double GetGroundAltitude()
        {
            return telemetryDataProvider.GetAltitudeDisplayValue(AltimeterDisplayMode.GroundLevel);
        }

        public double GetSeaAltitude()
        {
            return telemetryDataProvider.GetAltitudeDisplayValue(AltimeterDisplayMode.SeaLevel);
        }

        public double GetGlobalGroundAltitude()
        {
            return Math.Min(GetSeaAltitude(), GetGroundAltitude());
        }

        // K2-D2 1.4.0's ground height (IanMealworm): GetGlobalGroundAltitude, with the vessel's own
        // terrain height (VesselComponent.AltitudeFromTerrain) standing in low down when the ground
        // reading fell back to the sea-level one (see AltitudeReading). Logged only for now
        // ("[Landing] altitude:"); no caller uses it yet.
        public double GetGlobalGroundAltitudeWithTerrain()
        {
            return AltitudeReading.GroundWithTerrainFallback(GetSeaAltitude(), GetGroundAltitude(),
                VesselComponent.AltitudeFromTerrain);
        }

        // K2-D2 1.4.0's GetApproxAltitude: 0 while landed or splashed, never below 0, on the
        // terrain-aware ground height. Logged only for now; GetApproxAltitude keeps its meaning
        // for its callers (landing, WarpTo, drone).
        public double GetApproxAltitudeGrounded()
        {
            return AltitudeReading.Grounded(VesselComponent.Landed || VesselComponent.Splashed,
                GetGlobalGroundAltitudeWithTerrain(), VesselComponent.SimulationObject.objVesselBehavior.BoundingSphere.radius);
        }

        // =================================================================================================================

        public IGGuid GetGlobalIDActiveVessel()
        {
            return VesselComponent.SimulationObject.GlobalId;
        }

    }
}