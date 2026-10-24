using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Transfer
{
    // Game glue: the active vessel's state and stages for the transfer search.
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c), see the Task 9 report:
    // - VesselComponent.Orbit is always OrbiterComponent.PatchedConicsOrbit, and a coasting
    //   vessel moves on it (OrbiterComponent.OnFixedUpdate → PatchedOrbit.UpdateFromUT, with
    //   PatchedOrbit = PatchedConicsOrbit). The orbiter switches PatchedOrbit to a
    //   PatchedNBodyOrbit only while "orbital physics under thrust" is active (a burn on rails),
    //   and back at its end (VesselComponent.HandleOrbitalPhysicsUnderThrustEnd).
    // - DeltaVStageInfo.Stage, DeltaVPartInfo.ActivationStage and DecoupleStage share one
    //   numbering (DeltaVStageInfo.PartsActivateInStage / PartsDecoupledInStage compare them).
    public static class VesselSnapshot
    {
        static string lastStages;
        static string lastNaNWarning;

        // The state comes from the same calls as OrbitUI.TryReadState; body is the index of the
        // vessel's reference body in GameBodySystem.GameBodies() (and so in a built BodySystem).
        public static bool TryRead(out int body, out Vector3d r, out Vector3d v, out double ut,
            out List<StageDv> stages, out string error) =>
            TryRead(GameBodySystem.GameBodies(), out body, out r, out v, out ut, out stages, out error);

        // The same, with body an index into 'bodies': the list a BodySystem was built from
        // (GameBodySystem.Build(bodies, …)), so the index refers to that system.
        public static bool TryRead(IReadOnlyList<CelestialBodyComponent> bodies, out int body, out Vector3d r, out Vector3d v,
            out double ut, out List<StageDv> stages, out string error)
        {
            body = -1;
            r = default;
            v = default;
            ut = 0;
            stages = null;
            try
            {
                VesselComponent vessel = K2D2_Plugin.Instance?.current_vessel?.VesselComponent;
                IKeplerPatch orbit = vessel?.Orbit;
                if (orbit == null)
                {
                    error = "No active vessel in flight.";
                    return false;
                }
                ut = GeneralTools.Current_UT;
                r = orbit.GetRelativePositionAtUTZup(ut);
                v = orbit.GetOrbitalVelocityAtUTZup(ut);
                body = GameBodySystem.IndexOf(bodies, orbit.referenceBody);
                if (body < 0)
                {
                    error = "The vessel's reference body is not in the game's body list.";
                    return false;
                }
                stages = ReadStages(vessel, out error);
                return stages != null;
            }
            catch (Exception e)
            {
                error = $"Cannot read the vessel: {e.GetType().Name}: {e.Message}";
                K2D2_Plugin.logger.LogInfo("[Transfer] " + error);
                return false;
            }
        }

        // The stages in firing order (Stage descending), from VesselDeltaV.StageInfo (vacuum Δv).
        static List<StageDv> ReadStages(VesselComponent vessel, out string error)
        {
            VesselDeltaVComponent dv = vessel.VesselDeltaV;
            if (dv == null || dv.StageInfo == null)
            {
                error = "The game gives no Δv data for this vessel.";
                return null;
            }
            if (!dv.IsReady)
            {
                error = "The game's Δv data is not ready yet: try again in a moment.";
                return null;
            }

            var stageValues = new List<(int, double)>();
            var parts = new List<StagePart>();
            var seen = new HashSet<string>();
            foreach (DeltaVStageInfo s in dv.StageInfo)
            {
                if (s == null)
                    continue;
                double dvVac = s.DeltaVinVac;
                if (double.IsNaN(dvVac) || double.IsInfinity(dvVac))
                {
                    // Counted as 0, so one stage cannot poison the budget's sums.
                    string warning = $"[Transfer] stage {s.Stage}: the game gives a vacuum Δv of {dvVac}, counted as 0 m/s";
                    if (warning != lastNaNWarning)
                    {
                        lastNaNWarning = warning;
                        K2D2_Plugin.logger.LogWarning(warning);
                    }
                    dvVac = 0;
                }
                stageValues.Add((s.Stage, dvVac));
                if (s.Parts == null)
                    continue;
                foreach (DeltaVPartInfo p in s.Parts)
                {
                    if (p?.PartRef == null || !seen.Add(p.PartRef.GlobalId.Guid.ToString("D")))
                        continue;
                    var part = p.PartRef as PartComponent;
                    parts.Add(new StagePart
                    {
                        Name = p.PartRef.PartName,
                        ActivationStage = p.ActivationStage,
                        DecoupleStage = p.DecoupleStage,
                        IsDockingPort = p.IsDockingPort || (part != null
                            && part.TryGetModuleData<PartComponentModule_DockingNode, Data_DockingNode>(out Data_DockingNode port) && port != null),
                        IsParachute = part != null
                            && part.TryGetModuleData<PartComponentModule_Parachute, Data_Parachute>(out Data_Parachute chute) && chute != null,
                        IsCommand = part != null
                            && part.TryGetModuleData<PartComponentModule_Command, Data_Command>(out Data_Command command) && command != null,
                    });
                }
            }

            List<StageDv> stages = StageList.InFiringOrder(stageValues, parts, out error);
            if (stages == null)
                return null;
            string line = $"[Transfer] stages in firing order: {StageList.Describe(stages)}";
            if (line != lastStages)
            {
                lastStages = line;
                K2D2_Plugin.logger.LogInfo(line);
            }
            return stages;
        }

        // The start-of-transfer checks of the IL findings, logged by the pilot:
        // (a) the vessel's position relative to the root, summed up the hierarchy from the game's
        //     relative Zup vectors, against the game's world positions reframed into the root's
        //     celestial frame, and against the BodySystem (built at the same UT, or null);
        // (b) which trajectory model moves the vessel right now.
        public static void LogStartChecks(BodySystem system, IReadOnlyList<CelestialBodyComponent> bodyList = null)
        {
            try
            {
                VesselComponent vessel = K2D2_Plugin.Instance?.current_vessel?.VesselComponent;
                IKeplerPatch orbit = vessel?.Orbit;
                if (orbit == null)
                {
                    K2D2_Plugin.logger.LogInfo("[Transfer] start checks: no active vessel.");
                    return;
                }
                double ut = GeneralTools.Current_UT;

                Vector3d chain = orbit.GetRelativePositionAtUTZup(ut);
                CelestialBodyComponent root = orbit.referenceBody;
                while (root.referenceBody != null && !ReferenceEquals(root.referenceBody, root))
                {
                    chain += root.Orbit.GetRelativePositionAtUTZup(ut);
                    root = root.referenceBody;
                }

                Position world = Position.Reframed(vessel.SimulationObject.Position, root.SimulationObject.transform.celestialFrame);
                Vector3d yup = world.localPosition;
                var worldZup = new Vector3d(yup.x, yup.z, yup.y); // Yup -> Zup, as in LandingPilot
                double distance = Position.Distance(vessel.SimulationObject.Position, root.Position);
                string line = $"[Transfer] frame check at UT {F(ut)}: vessel from {root.bodyName}, Zup chain |r| {F(chain.magnitude)} m, " +
                              $"world distance {F(distance)} m (diff {F(chain.magnitude - distance)} m), " +
                              $"chain vs world (Zup swap) {F((chain - worldZup).magnitude)} m";

                if (system != null)
                {
                    IReadOnlyList<CelestialBodyComponent> bodies = bodyList ?? GameBodySystem.GameBodies();
                    int b = GameBodySystem.IndexOf(bodies, orbit.referenceBody);
                    int rootIndex = GameBodySystem.IndexOf(bodies, root);
                    if (b >= 0 && rootIndex >= 0 && b < system.Count && rootIndex < system.Count)
                    {
                        system.StateRelativeTo(b, rootIndex, ut, out Vector3d rb, out _);
                        line += $", chain vs BodySystem {F((rb + orbit.GetRelativePositionAtUTZup(ut) - chain).magnitude)} m";
                    }
                }
                K2D2_Plugin.logger.LogInfo(line);

                IPatchedOrbit moving = vessel.Orbiter?.PatchedOrbit;
                bool nBody = moving is PatchedNBodyOrbit;
                K2D2_Plugin.logger.LogInfo($"[Transfer] game trajectory model: " +
                    (nBody ? "n-body (PatchedNBodyOrbit)" : "patched conics") +
                    $", PatchedOrbit is {moving?.GetType().Name ?? "null"}, " +
                    $"orbital physics under thrust {vessel.IsOrbitalPhysicsUnderThrustActive}");
            }
            catch (Exception e)
            {
                K2D2_Plugin.logger.LogInfo($"[Transfer] start checks failed: {e.GetType().Name}: {e.Message}");
            }
        }

        static string F(double x) => x.ToString("F0", CultureInfo.InvariantCulture);
    }
}
