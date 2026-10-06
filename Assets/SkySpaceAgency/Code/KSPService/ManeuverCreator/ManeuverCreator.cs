using System;
using System.Collections;
using System.Collections.Generic;
using KSP.Game;
using KSP.Map;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using K2D2.OrbitPlanning;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine;
using ILogger = ReduxLib.Logging.ILogger;


namespace K2D2.KSPService
{
    public class ManeuverCreator
    {
        // Fields-------------------------------------------------------------------------------------------------------

        #region fields

        private VesselComponent _vesselComponent;

        // True while a CreateNodes sequence is running; guards against a second one (double click)
        // removing the first one's nodes midway.
        private bool _creatingNodes;
        public GameInstance Game => GameManager.Instance == null ? null : GameManager.Instance.Game;

        public KSPVessel kspVessel { get; set; }

        public ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.CircleController");

        public ManeuverCreator()
        {

        }

        public void Update()
        {
            kspVessel = K2D2_Plugin.Instance.current_vessel;
            _vesselComponent = kspVessel.GetActiveSimVessel();
        }

        #endregion

        // Functions----------------------------------------------------------------------------------------------------

        #region CicrularizeOrbit

        public double CircularizeOrbitApoapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;

            if (orbit.eccentricity >= 1)
            {
                logger.LogMessage("Apoapsis Circularization not possible for hyperbolic orbits");
                return CircularizeHyperbolicOrbit();
            }

            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;

            double circularizedVelocity = VisVivaEquation.CalculateVelocity(apoapsis, apoapsis,
                apoapsis, gravitation);

            double apoapsisVelocity = VisVivaEquation.CalculateVelocity(apoapsis,
                apoapsis,
                periapsis,
                gravitation);

            double deltaV = circularizedVelocity - apoapsisVelocity;


            Vector3d burnVector = ProgradeBurnVector(deltaV);

            // ManeuverNodeController.NodeControl.CreateManeuverNodeAtUT(burnVector, GeneralTools.Game.UniverseModel.UniverseTime + orbit.TimeToAp ,0);
            CreateManeuverNode(burnVector, 180);
            return deltaV;
        }

        public double CircularizeOrbitPeriapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            if (orbit.eccentricity >= 1)
            {
                logger.LogMessage("Periapsis Circularization not possible for hyperbolic orbits");
                return CircularizeHyperbolicOrbit();
            }

            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;
            double eccentricity = orbit.eccentricity;
            logger.LogMessage($"AP: {orbit.Apoapsis} PE: {orbit.Periapsis}");
            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            //double gravitation = _vesselComponent.Orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double circularizedVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                periapsis,
                periapsis,
                gravitation);

            double periapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                apoapsis,
                periapsis,
                gravitation);

            double deltaV = periapsisVelocity - circularizedVelocity;

            Vector3d burnVector = RetrogradeBurnVector(deltaV);
            // ManeuverNodeController.NodeControl.CreateManeuverNodeAtUT(burnVector, GeneralTools.Game.UniverseModel.UniverseTime + orbit.TimeToPe, 0);
            CreateManeuverNode(burnVector, 0);
            return deltaV;
        }

        public double CircularizeHyperbolicOrbit()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double periapsis = orbit.Periapsis;
            double orbitalEnergy = orbit.OrbitalEnergy;

            double currentVelocity = VisVivaEquation.CalculateHyperbolicVelocity(periapsis,
                gravitation,
                orbitalEnergy);

            double newPeriapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                periapsis,
                periapsis,
                gravitation);

            double deltaV = newPeriapsisVelocity - currentVelocity;

            Vector3d burnVector = ProgradeBurnVector(deltaV);
            CreateManeuverNode(burnVector, 0);
            return deltaV;
        }

        #endregion

        #region ChangeOrbit



        public void ChangePeriapsis(double OrbitDistance)
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;


            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;
            double eccentricity = orbit.eccentricity;
            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;

            if (eccentricity >= 1)
            {
                logger.LogMessage("Periapsis Change not possible for hyperbolic orbits");
                return;
            }

            double currentPeriapsisVelocity = VisVivaEquation.CalculateVelocity(apoapsis,
                apoapsis,
                periapsis,
                gravitation);

            double newPeriapsisVelocity = VisVivaEquation.CalculateVelocity(apoapsis,
                apoapsis,
                OrbitDistance,
                gravitation);

            double deltaV = newPeriapsisVelocity - currentPeriapsisVelocity;

            Vector3d burnVector = ProgradeBurnVector(deltaV);
            CreateManeuverNode(burnVector, 180);
        }

        public void ChangeApoapsis(double OrbitDistance)
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;


            double periapsis = orbit.Periapsis;
            double apoapsis = orbit.Apoapsis;
            double eccentricity = orbit.eccentricity;
            double newEccentricity = (OrbitDistance - periapsis) / (OrbitDistance + periapsis);

            double gravitation = orbit.ReferenceBodyConstants.StandardGravitationParameter;
            double deltaV = 0;
            double currentApoapsisVelocity;

            if (eccentricity >= 1)
            {
                currentApoapsisVelocity = VisVivaEquation.CalculateHyperbolicVelocity(periapsis,
                    gravitation,
                    orbit.OrbitalEnergy);
            }
            else
            {
                currentApoapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                    apoapsis,
                    periapsis,
                    gravitation);
            }

            double newApoapsisVelocity = VisVivaEquation.CalculateVelocity(periapsis,
                OrbitDistance,
                periapsis,
                gravitation);

            deltaV = newApoapsisVelocity - currentApoapsisVelocity;


            Vector3d burnVector = ProgradeBurnVector(deltaV);
            CreateManeuverNode(burnVector, 0);
        }

        #endregion

        #region InterplanetaryTransfer
        public double HohmannTransfer(double UT, double OrbitDistance)
        {
            CircularizeOrbitApoapsis();
            double deltaV = 0;
            return deltaV;
        }

        #endregion

        // Internal Maneuver Services-----------------------------------------------------------------------------------

        #region Internal Maneuver Services

        private IPatchedOrbit GetLastOrbit()
        {
            List<ManeuverNodeData> patchList =
                Game.SpaceSimulation.Maneuvers.GetNodesForVessel(kspVessel.GetGlobalIDActiveVessel());

            logger.LogMessage(patchList.Count);

            if (patchList.Count == 0)
            {
                logger.LogMessage(_vesselComponent.Orbit);
                return _vesselComponent.Orbit;
            }

            logger.LogMessage(patchList[patchList.Count - 1].ManeuverTrajectoryPatch);
            IPatchedOrbit orbit = patchList[patchList.Count - 1].ManeuverTrajectoryPatch;

            return orbit;
        }

        /// <summary>
        /// Creates a maneuver node at a given true anomaly
        /// </summary>
        /// <param name="burnVector"></param>
        /// <param name="TrueAnomaly"></param>
        private void CreateManeuverNode(Vector3d burnVector, double TrueAnomaly)
        {
            K2D2_Plugin.Instance.StartCoroutine(CreateManeuverNode_Co(burnVector, TrueAnomaly));
        }

        private IEnumerator CreateManeuverNode_Co(Vector3d burnVector, double TrueAnomaly)
        {
            // VesselComponent.Orbit is typed as KSP.Sim.IKeplerPatch, not PatchedConicsOrbit, so an
            // explicit cast is required here, same as every other call site in this file.
            PatchedConicsOrbit referencedOrbit = (PatchedConicsOrbit)_vesselComponent.Orbit;

            double TrueAnomalyRad = TrueAnomaly * Math.PI / 180;
            double UT = referencedOrbit.GetUTforTrueAnomaly(TrueAnomalyRad, 0);

            var SimulationObject = _vesselComponent.SimulationObject;

            // Create Node
            ManeuverNodeData nodeData = new ManeuverNodeData(SimulationObject.GlobalId, false, UT);
            referencedOrbit.PatchEndTransition = PatchTransitionType.Maneuver;
            nodeData.SetManeuverState((PatchedConicsOrbit)referencedOrbit);

            nodeData.BurnVector = burnVector;

            Game.SpaceSimulation.Maneuvers.AddNodeToVessel(nodeData);

            yield return new WaitForFixedUpdate();

            MapCore mapCore = null;
            Game.Map.TryGetMapCore(out mapCore);
            if (mapCore)
            {
                mapCore.map3D.ManeuverManager.GetNodeDataForVessels();
                mapCore.map3D.ManeuverManager.UpdatePositionForGizmo(nodeData.NodeID);
                // mapCore.map3D.ManeuverManager.UpdateAll();
                // mapCore.map3D.ManeuverManager.RemoveAll();
            }


        }

        /// <summary>
        /// New for precision landing (LandingTargeting.cs / DeorbitBurn.cs): creates a real,
        /// visible maneuver node at a specific UT with a pure prograde/retrograde burn, rather
        /// than deriving the UT from a TrueAnomaly like CreateManeuverNode_Co above does. The
        /// deorbit/phasing burn needs to happen at a UT we've already computed ourselves (the
        /// resonance search in LandingTargeting.DeltaVToShiftNodeLongitude), not one implied by
        /// a true anomaly.
        ///
        /// Unlike CreateManeuverNode_Co, this does not cast the vessel's Orbit to
        /// PatchedConicsOrbit: under Redux, the actively-flown vessel's Orbit is a
        /// Redux.Ecs.Components.CurrentPatchedConicsOrbit, which implements the same interfaces
        /// (IKeplerPatch, IPatchedOrbit, etc.) but is not a PatchedConicsOrbit and cannot be cast
        /// to one. SetManeuverState is also skipped here, matching the game's own
        /// Map3DManeuvers.OnAddManeuver(), which only calls it when IsOnManeuverTrajectory is true
        /// (adding a node onto an existing maneuver plan segment) - not the case for a first/only
        /// node (isManeuver: false in the constructor below). What this node needs instead is
        /// nodeData.InitializeTransform() right after construction, which is what
        /// ManeuverPlanComponent.UpdateNodeDetails (called from AddNode/AddNodeToVessel) requires.
        ///
        /// Unlike CreateManeuverNode_Co, this returns the created ManeuverNodeData synchronously
        /// so the caller (DeorbitBurn) can hand it straight to its own TurnTo/WarpTo/BurnManeuver
        /// instances without waiting a frame - only the map/gizmo bookkeeping is deferred to a
        /// coroutine, same as the original.
        /// </summary>
        // normalDeltaV added for precision landing's optional small plane trim (see
        // LandingTargeting.FindBestDeorbitBurn) - defaults to 0 so every existing caller
        // (Circularize.cs, Final.cs) keeps behaving exactly as before, pure prograde/retrograde.
        // radialDeltaV and onManeuverTrajectory added for the Orbit tab's multi-node plans: a node
        // after the first lies on the trajectory produced by the previous node, which the
        // ManeuverNodeData constructor's isOnManeuverTrajectory flag declares (API doc:
        // "True if the node lies on a trajectory produced by a prior maneuver"). Defaults keep
        // every existing caller unchanged.
        public ManeuverNodeData CreateManeuverNodeAtUT(double UT, double progradeDeltaV, double normalDeltaV = 0,
            double radialDeltaV = 0, bool onManeuverTrajectory = false)
        {
            Vector3d burnVector = ProgradeBurnVector(progradeDeltaV) + NormalBurnVector(normalDeltaV)
                + RadialOutBurnVector(radialDeltaV);

            var SimulationObject = _vesselComponent.SimulationObject;

            ManeuverNodeData nodeData = new ManeuverNodeData(SimulationObject.GlobalId, onManeuverTrajectory, UT);
            nodeData.InitializeTransform();
            nodeData.BurnVector = burnVector;

            // IsOnManeuverTrajectory is false for a first/only node, so per
            // Map3DManeuvers.OnAddManeuver() SetManeuverState is correctly skipped - the engine's
            // own maneuver-plan pipeline (ManeuverPlanComponent.AddNode, same path the in-game
            // "add node" UI uses) resolves ManeuverTrajectoryPatch from here. It is true (via
            // onManeuverTrajectory) for the later nodes of an Orbit tab sequence; that behaviour
            // is validated in game.
            Game.SpaceSimulation.Maneuvers.AddNodeToVessel(nodeData);

            K2D2_Plugin.Instance.StartCoroutine(UpdateMapGizmo_Co(nodeData));

            return nodeData;
        }

        /// <summary>
        /// Removes every maneuver node currently on the vessel's plan. Needed before creating a
        /// new node via CreateManeuverNodeAtUT above, since AddNodeToVessel only ever appends (see
        /// that method's own comment) - calling it while a node from a previous phase is still on
        /// the plan (e.g. precision landing's Circularize node, once its burn finishes and
        /// DeorbitBurn starts) would add a second node instead of replacing it, and the vessel
        /// would turn to align with whichever node the game picks instead of the new one.
        /// </summary>
        public void RemoveAllNodes()
        {
            var maneuvers_component = _vesselComponent?.SimulationObject?.FindComponent<ManeuverPlanComponent>();
            if (maneuvers_component == null)
                return;

            List<ManeuverNodeData> nodes = maneuvers_component.GetNodes();
            if (nodes == null || nodes.Count == 0)
                return;

            // GetNodes() returns the component's own live list, not a copy - passing it directly to
            // RemoveNodes() would throw "Collection was modified" since RemoveNodes enumerates the
            // same list it's removing entries from. Pass a copy so it enumerates a snapshot instead.
            maneuvers_component.RemoveNodes(new List<ManeuverNodeData>(nodes));
        }

        /// <summary>
        /// Removes a single maneuver node from the plan, leaving every other node alone - unlike
        /// RemoveAllNodes above, which assumes the whole plan belongs to whichever pilot is
        /// calling it (true for Circularize/DeorbitBurn/FinalCircularize, which own the node they
        /// create start to finish). Added for auto-deleting a node once it's been executed (Lift's
        /// FinalCircularize, and the Node tab's own executor) without also wiping out anything
        /// else the player - or a multi-node Flight Plan - might have queued up behind it. Same
        /// ManeuverPlanComponent.RemoveNodes API as RemoveAllNodes, just handed a single-item list.
        /// </summary>
        public void RemoveNode(ManeuverNodeData node)
        {
            if (node == null)
                return;

            var maneuvers_component = _vesselComponent?.SimulationObject?.FindComponent<ManeuverPlanComponent>();
            if (maneuvers_component == null)
                return;

            maneuvers_component.RemoveNodes(new List<ManeuverNodeData> { node });
        }

        /// <summary>
        /// Removes every node on the plan, then creates a fresh one via CreateManeuverNodeAtUT -
        /// but a fixed update later, not in the same call. Creating the new node in the same frame
        /// as RemoveNodes causes the game to only partially register the removal, so the new node
        /// never shows up; waiting one FixedUpdate (see RemoveAllNodesThenCreate_Co below) avoids
        /// this, consistent with AddNodeToVessel's own gizmo/map update already waiting a
        /// WaitForFixedUpdate rather than touching the map layer in the same frame a node is added.
        /// </summary>
        public void RemoveAllNodesThenCreate(double UT, double progradeDeltaV, System.Action<ManeuverNodeData> onCreated,
            double normalDeltaV = 0, bool centerOnImpulse = false)
        {
            RemoveAllNodes();
            K2D2_Plugin.Instance.StartCoroutine(RemoveAllNodesThenCreate_Co(UT, progradeDeltaV, onCreated, normalDeltaV, centerOnImpulse));
        }

        private IEnumerator RemoveAllNodesThenCreate_Co(double UT, double progradeDeltaV, System.Action<ManeuverNodeData> onCreated,
            double normalDeltaV = 0, bool centerOnImpulse = false)
        {
            yield return new WaitForFixedUpdate();

            // centerOnImpulse: UT is the impulsive instant (e.g. the apsis); the node's own Time is
            // the start of the finite burn, half its duration earlier.
            double nodeUT = centerOnImpulse
                ? CenteredNodeTime(UT, Math.Sqrt(progradeDeltaV * progradeDeltaV + normalDeltaV * normalDeltaV), 0)
                : UT;

            var nodeData = CreateManeuverNodeAtUT(nodeUT, progradeDeltaV, normalDeltaV);

            var maneuvers_component = _vesselComponent?.SimulationObject?.FindComponent<ManeuverPlanComponent>();
            int count_after = maneuvers_component?.GetNodes()?.Count ?? -1;
            logger.LogInfo($"[ManeuverCreator] RemoveAllNodesThenCreate: created node {nodeData?.NodeID} at UT={nodeUT:n1} " +
                $"deltaV={progradeDeltaV:n2} normalDeltaV={normalDeltaV:n2} - {count_after} node(s) now on the plan.");

            onCreated?.Invoke(nodeData);
        }

        // Smallest lead between now and a centered node's start, so the executor has a moment to react.
        public const double MinLead = 5;

        private BurndV _burnDv;

        /// <summary>
        /// Estimated duration in seconds of a burn of deltaV with the active vessel's active
        /// engines at full throttle and its current mass (Tsiolkovsky, see BurnTiming.Duration).
        /// Falls back to every engine's full thrust when none is active (engines may be shut down
        /// while coasting). NaN when there is no usable thrust or mass.
        /// </summary>
        public double EstimateBurnDuration(double deltaV)
        {
            try
            {
                if (_burnDv == null)
                    _burnDv = new BurndV();
                _burnDv.Compute_Thrust();

                double thrust = _burnDv.active_thrust;
                if (thrust <= 0)
                    thrust = _burnDv.full_thrust.magnitude;

                return BurnTiming.Duration(deltaV, thrust, _burnDv.mass, _burnDv.active_isp);
            }
            catch (System.Exception e)
            {
                logger.LogWarning($"[ManeuverCreator] EstimateBurnDuration failed: {e.Message}");
                return double.NaN;
            }
        }

        /// <summary>
        /// Node Time for a burn whose impulse is at impulseUT: half its duration earlier, but not
        /// before earliestUT or now + MinLead. Logs one line per call.
        /// </summary>
        private double CenteredNodeTime(double impulseUT, double deltaVMagnitude, double earliestUT)
        {
            double now = GeneralTools.Current_UT;
            double duration = EstimateBurnDuration(deltaVMagnitude);
            // MinLead gives the executor a moment, but never pushes the start past the impulse
            // itself (an impulse less than MinLead away keeps its old, uncentered time).
            double lead = Math.Max(now, Math.Min(impulseUT, now + MinLead));
            double earliest = Math.Max(earliestUT, lead);
            double start = BurnTiming.CenteredStart(impulseUT, duration, earliest);

            string clamped = "";
            if (!double.IsNaN(duration) && impulseUT - duration / 2 < earliest)
                clamped = earliestUT > lead ? " (clamped after the previous node)" : " (too long to center: starting now)";
            logger.LogInfo($"[ManeuverCreator] centered burn: impulse T+{impulseUT - now:n1}s, " +
                $"duration {duration:n1}s, start T+{start - now:n1}s{clamped}");
            return start;
        }

        // Sign mapping the physics normal (r × v) to the game's BurnVector.y. Same assumption as
        // LandingTargeting's plane trim; if the Orbit tab's inclination burn turns the orbit the
        // wrong way in game, flip this to -1.
        public const double GameNormalSign = 1.0;

        /// <summary>
        /// Replaces the vessel's plan with the given burns (Orbit tab): removes every node, then
        /// creates one node per FixedUpdate in chronological order - never in the same frame as
        /// the removal (see RemoveAllNodesThenCreate). onDone receives how many nodes the plan
        /// holds afterwards, so the caller can tell whether the game accepted all of them. The
        /// burns must be in chronological order (the Orbit planner produces them that way).
        /// onDone always fires (with the real count, or 0 for an unusable request), except when a
        /// sequence is already being created: that request is ignored and onDone is not called.
        /// </summary>
        public void CreateNodes(IReadOnlyList<PlannedBurn> burns, System.Action<int> onDone)
        {
            if (_creatingNodes)
            {
                logger.LogWarning("[ManeuverCreator] CreateNodes: a node sequence is already being created - request ignored.");
                return;
            }

            if (_vesselComponent == null || burns == null || burns.Count == 0)
            {
                onDone?.Invoke(0);
                return;
            }

            _creatingNodes = true;
            try
            {
                RemoveAllNodes();
                K2D2_Plugin.Instance.StartCoroutine(CreateNodes_Co(burns, onDone));
            }
            catch (System.Exception e)
            {
                _creatingNodes = false;
                logger.LogError($"[ManeuverCreator] CreateNodes: could not start the node sequence: {e.Message}");
                onDone?.Invoke(0);
            }
        }

        private IEnumerator CreateNodes_Co(IReadOnlyList<PlannedBurn> burns, System.Action<int> onDone)
        {
            try
            {
                double previousStart = double.NegativeInfinity;
                for (int i = 0; i < burns.Count; i++)
                {
                    yield return new WaitForFixedUpdate();
                    PlannedBurn burn = burns[i];
                    try
                    {
                        // Each burn is centered on its impulse time with its own duration, estimated from
                        // the current mass (an approximation for later burns, flown after mass is lost).
                        // Nodes stay in chronological order: never start before the previous one.
                        double burnMagnitude = Math.Sqrt(burn.Prograde * burn.Prograde + burn.Normal * burn.Normal
                            + burn.Radial * burn.Radial);
                        double nodeUT = CenteredNodeTime(burn.UT, burnMagnitude, previousStart + 1);
                        previousStart = nodeUT;

                        var node = CreateManeuverNodeAtUT(nodeUT, burn.Prograde, burn.Normal * GameNormalSign, burn.Radial, i > 0);
                        logger.LogInfo($"[ManeuverCreator] CreateNodes: node {i + 1}/{burns.Count} {node?.NodeID} at UT={nodeUT:n1} (impulse UT={burn.UT:n1}) " +
                            $"prograde={burn.Prograde:n2} normal={burn.Normal:n2} radial={burn.Radial:n2} ({burn.Label})");
                    }
                    catch (System.Exception e)
                    {
                        logger.LogError($"[ManeuverCreator] CreateNodes: node {i + 1}/{burns.Count} failed: {e.Message}");
                        break;
                    }
                }

                yield return new WaitForFixedUpdate();
                var maneuvers_component = _vesselComponent?.SimulationObject?.FindComponent<ManeuverPlanComponent>();
                int count = maneuvers_component?.GetNodes()?.Count ?? 0;
                logger.LogInfo($"[ManeuverCreator] CreateNodes: {count} node(s) on the plan for {burns.Count} requested.");
                onDone?.Invoke(count);
            }
            finally
            {
                _creatingNodes = false;
            }
        }

        private IEnumerator UpdateMapGizmo_Co(ManeuverNodeData nodeData)
        {
            yield return new WaitForFixedUpdate();

            MapCore mapCore = null;
            Game.Map.TryGetMapCore(out mapCore);
            if (mapCore)
            {
                mapCore.map3D.ManeuverManager.GetNodeDataForVessels();
                mapCore.map3D.ManeuverManager.UpdatePositionForGizmo(nodeData.NodeID);
            }
        }




        private VesselComponent activeVessel
        {
            get
            {

                return KSPVessel.current.VesselComponent;
            }
        }



        #endregion

        // Logging------------------------------------------------------------------------------------------------------

        #region Logging

        public void Log(ILogger logger, string message)
        {
            logger.LogMessage(message);
        }

        public void LogOrbit()
        {
            logger.LogMessage("================= Orbit Log =================");
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            logger.LogMessage($"AP: {orbit.Apoapsis} PE: {orbit.Periapsis}");
            logger.LogMessage($"SemiMajorAxis: {orbit.semiMajorAxis} SemiMinorAxis: {orbit.SemiMinorAxis}");
            logger.LogMessage($"Eccentricity: {orbit.eccentricity} ");
            logger.LogMessage($"Inclination: {orbit.inclination} ArgumentOfPeriapsis: {orbit.argumentOfPeriapsis}");
            logger.LogMessage("epoch: " + orbit.epoch);
            logger.LogMessage("referenceBody: " + orbit.referenceBody);
        }

        #endregion

        // Special Burning Vectors--------------------------------------------------------------------------------------

        #region Special Burning Vectors

        /// <summary>
        /// Burn Vector for a Prograde Maneuver(0,0,1 )* magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Prograde Burn Vector3d </returns>
        public Vector3d ProgradeBurnVector(double magnitude)
        {
            return new Vector3d(0, 0, magnitude);
            ;
        }

        /// <summary>
        /// Burn Vector for a Retrograde Maneuver(0,0,-1) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Retrograde Burn Vector3d</returns>
        public Vector3d RetrogradeBurnVector(double magnitude)
        {
            return new Vector3d(0, 0, -magnitude);
        }

        /// <summary>
        /// Burn Vector for a Normal Maneuver(0,1,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Normal Burn Vector3d</returns>
        public Vector3d NormalBurnVector(double magnitude)
        {
            return new Vector3d(0, magnitude, 0);
            ;
        }

        /// <summary>
        /// Burn Vector for a AntiNormal Maneuver(0,-1,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Anti Normal Burn Vector3d </returns>
        public Vector3d AntiNormalBurnVector(double magnitude)
        {
            return new Vector3d(0, -magnitude, 0);
            ;
        }

        /// <summary>
        /// Radial Out Burn Vector(1,0,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Radial Out Burn Vector3d</returns>
        public Vector3d RadialOutBurnVector(double magnitude)
        {
            return new Vector3d(magnitude, 0, 0);
            ;
        }

        /// <summary>
        /// Radial In Burn Vector(-1,0,0) * magnitude
        /// </summary>
        /// <param name="magnitude"></param>
        /// <returns>Radial In Burn Vector3d</returns>
        public Vector3d RadialInBurnVector(double magnitude)
        {
            return new Vector3d(-magnitude, 0, 0);
        }

        #endregion

        // Custom Functions---------------------------------------------------------------------------------------------

        #region Custom Functions

        public bool IsApoapsisFirst()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            double timePE = orbit.GetUTforTrueAnomaly(0, 0);
            double timeAP = orbit.GetUTforTrueAnomaly(Math.PI, 0);
            return timePE > timeAP;
        }

        public bool IsOrbitElliptic()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return orbit.eccentricity < 1;
        }

        public double AddRadiusOfBody(double radius)
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return radius + orbit.ReferenceBodyConstants.Radius;
        }

        public double GetCurrentPeriapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return orbit.Periapsis;
        }

        public double GetCurrentApoapsis()
        {
            PatchedConicsOrbit orbit = GetLastOrbit() as PatchedConicsOrbit;
            return orbit.Apoapsis;
        }

        public Vector3d GetOrbitalVelocityAtUT(double UT)
        {
            double inclination = _vesselComponent.Orbit.inclination;
            double longitudeOfAscendingNode = _vesselComponent.Orbit.longitudeOfAscendingNode;
            Vector3d
                normalVector =
                    _vesselComponent.Orbit
                        .GetRelativeOrbitNormal(); //GetOrbitalNormalVector(UT, inclination, longitudeOfAscendingNode);
            // VERIFIED during Redux port verification: ReferenceBodyConstants exists only on the concrete
            // PatchedConicsOrbit class, not on the IKeplerPatch interface VesselComponent.Orbit is typed
            // as - needs an explicit cast, unlike inclination/eccentricity/semiMajorAxis/etc. above which
            // are genuinely on the interface and compile fine as-is.
            Vector3d velocity = GetOrbitalPerifocalVelocityVector(UT, _vesselComponent.Orbit.eccentricity,
                _vesselComponent.Orbit.semiMajorAxis, _vesselComponent.Orbit.meanAnomalyAtEpoch,
                ((PatchedConicsOrbit)_vesselComponent.Orbit).ReferenceBodyConstants.StandardGravitationParameter);

            Vector3d orbitalVelocity = Vector3d.Cross(normalVector, velocity);
            return orbitalVelocity;
        }

        /// <summary>
        /// Returns the orbital normal vector in the ECI frame
        /// Use VesselComponent.Orbit.GetRelativeOrbitNormal() instead of this function if you can
        /// </summary>
        /// <param name="UT"></param>
        /// <param name="inclination"></param>
        /// <param name="longitudeOfAscendingNode"></param>
        /// <returns></returns>
        public Vector3d GetOrbitalNormalVector(double UT, double inclination, double longitudeOfAscendingNode)
        {
            // Calculate the normal vector components in the perifocal frame
            double nx = Math.Cos(inclination) * Math.Cos(longitudeOfAscendingNode);
            double ny = Math.Cos(inclination) * Math.Sin(longitudeOfAscendingNode);
            double nz = Math.Sin(inclination);

            // Convert the normal vector from the perifocal frame to the ECI frame
            double cosRAAN = Math.Cos(longitudeOfAscendingNode);
            double sinRAAN = Math.Sin(longitudeOfAscendingNode);
            double cosI = Math.Cos(inclination);
            double sinI = Math.Sin(inclination);
            double cosTA = Math.Cos(UT);
            double sinTA = Math.Sin(UT);

            double ex = cosRAAN * cosTA - sinRAAN * sinTA * cosI;
            double ey = sinRAAN * cosTA + cosRAAN * sinTA * cosI;
            double ez = sinTA * sinI;

            return new Vector3d(ex, ey, ez);
        }

        /// <summary>
        /// Returns the orbital velocity vector in the ECI frame (Earth-centered inertial)
        /// </summary>
        /// <param name="semiMajorAxis"></param>
        /// <param name="eccentricity"></param>
        /// <param name="trueAnomaly"></param>
        /// <param name="inclination"></param>
        /// <param name="longitudeOfAscendingNode"></param>
        /// <returns></returns>
        public Vector3d GetOrbitalPerifocalVelocityVector(double semiMajorAxis, double eccentricity, double trueAnomaly,
            double inclination, double longitudeOfAscendingNode)
        {
            // Calculate the magnitude of the velocity vector
            double r = semiMajorAxis * (1 - eccentricity * eccentricity) / (1 + eccentricity * Math.Cos(trueAnomaly));
            // VERIFIED during Redux port verification: same ReferenceBodyConstants cast fix as above.
            double gravitation = ((PatchedConicsOrbit)_vesselComponent.Orbit).ReferenceBodyConstants.StandardGravitationParameter;
            // Calculate the magnitude of the velocity vector
            double v = Math.Sqrt(gravitation * (2 / r - 1 / semiMajorAxis));

            // Calculate the velocity vector components in the perifocal frame
            double vx = v * Math.Sin(trueAnomaly);
            double vy = v * (Math.Cos(trueAnomaly) + eccentricity);
            double vz = 0;

            // Convert the velocity vector from the perifocal frame to the ECI frame
            double cosRAAN = Math.Cos(longitudeOfAscendingNode);
            double sinRAAN = Math.Sin(longitudeOfAscendingNode);
            double cosArgPeriapsis = Math.Cos(_vesselComponent.Orbit.argumentOfPeriapsis);
            double sinArgPeriapsis = Math.Sin(_vesselComponent.Orbit.argumentOfPeriapsis);
            double cosInclination = Math.Cos(inclination);
            double sinInclination = Math.Sin(inclination);

            double x = cosRAAN * cosArgPeriapsis - sinRAAN * sinArgPeriapsis * cosInclination;
            double y = sinRAAN * cosArgPeriapsis + cosRAAN * sinArgPeriapsis * cosInclination;
            double z = sinArgPeriapsis * sinInclination;

            Vector3d perifocalVelocity = new Vector3d(vx, vy, vz);
            QuaternionD rotation = new QuaternionD(
                -sinRAAN * cosArgPeriapsis - cosRAAN * sinArgPeriapsis * cosInclination,
                cosRAAN * cosArgPeriapsis - sinRAAN * sinArgPeriapsis * cosInclination,
                sinRAAN * sinInclination,
                sinRAAN * cosArgPeriapsis * cosInclination + cosRAAN * sinArgPeriapsis);

            Vector3d velocityVector = rotation * perifocalVelocity;
            return velocityVector;
        }

        #endregion

        // Currently Not Implemented Functions--------------------------------------------------------------------------

        #region Unimplemented Functions

        public void deleteAllManeuvers()
        {
            throw new NotImplementedException();
        }

        #endregion

    }
}
