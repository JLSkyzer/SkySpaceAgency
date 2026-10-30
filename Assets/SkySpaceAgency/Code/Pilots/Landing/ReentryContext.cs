using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using K2D2.Landing.Precision;
using KSP.Modules;
using KSP.Sim.impl;
using UnityEngine;

namespace K2D2.Landing
{
    // Inputs of the reentry predictor (Precision/ReentryPredictor) read from the live game once:
    // the PrecisionContext (state, body, rotation sign, reference, target, terrain), the
    // atmosphere tables, and the capsule (part set, mass, drag at retrograde, parachutes, drag
    // calibration). Every game call is here, in one try/catch: a failure is a refusal with a
    // reason. Not unit-tested (game types); the capsule-side choice is (Precision/CapsuleSide).
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c), not from the API comments:
    // - Module_Parachute.ShouldDeployWhenArmed compares staticPressurekPa * 0.00986923266716013
    //   (kPa -> atm) to minAirPressureToOpen: that field is in atm, not kPa as documented. The
    //   pressure table is therefore in atm.
    // - It compares the velocity's sqrMagnitude to minimumSpeedToDeploy: the speed bound is its
    //   square root.
    // - It and the semi -> full switch read VesselComponent.AltitudeFromSurface against
    //   deployAltitude: above the ground, not above sea level as documented.
    // - SAFE mode (DeploymentMode 0) opens only while the safety state is SAFE, which
    //   UpdateDeploySafety grants when Mach < MachSafetyScalar (and the heat flux is under
    //   FluxSafetyScalar), and also, at any Mach, while the chute is cooling down. Only the
    //   Mach limit is modelled (Chutes.MaxDeployMach, against the local sound speed); the
    //   thermal terms are not, and the log says so. RISKY / IMMEDIATE have no Mach limit, only
    //   the thermal one, not modelled either. Module_Parachute.MaxSafeVelocityAtLocalBody() is
    //   a different, display formula evaluated at the vessel's current pressure (0 in vacuum).
    // - Data_Drag.GetEstimatedChuteDragForCube (the part manager's chute drag) is
    //   ComputeAreaDragSimple(cube, up, Mach) * DragCubeMultiplier * DragCurvePseudoReynolds(ρ·100)
    //   * DragMultiplier at 1000 m and 100 m/s: the chute drag area below uses the same formula.
    public class ReentryContext
    {
        public PrecisionContext Precision;
        public Atmosphere Air;
        public ReentryVehicle Capsule;
        public HashSet<string> CapsulePartIds;
        public bool CapsuleIsWholeVessel;    // no decoupler separates the chutes from the rest
        public double VesselMassT;           // Σ TotalMass of every part

        public const double SampleStep = 250;   // m
        // kPa -> atm, the game's own constant (ShouldDeployWhenArmed).
        public const double KPaToAtm = 0.00986923266716013;
        // ρ·v (kg/m²/s) at which DragCurvePseudoReynolds is read for the capsule: the drag table
        // is against Mach only, so the curve is folded in at one value. 100 is the game's own
        // reference for its chute drag estimate (ρ at 1000 m × 100 m/s, about 110 on Kerbin);
        // DragScale absorbs the rest. The log prints the curve from 1 to 1000.
        public const double RepresentativeRhoV = 100;
        static readonly float[] LoggedRhoV = { 1, 10, 30, 100, 300, 1000 };

        public static bool TryBuild(LandingPilot pilot, out ReentryContext ctx, out string reason, bool log = true)
        {
            ctx = null;
            if (!PrecisionContext.TryBuild(pilot, out PrecisionContext precision, out reason, log))
                return false;
            try
            {
                reason = Build(pilot, precision, log, out ctx);
            }
            catch (Exception e)
            {
                reason = $"Cannot read the game state for the reentry ({e.GetType().Name}: {e.Message}).";
            }
            if (reason != null)
            {
                ctx = null;
                if (log)
                    pilot?.logger.LogWarning($"[Landing] reentry: refused: {reason}");
                return false;
            }
            return true;
        }

        static string Build(LandingPilot pilot, PrecisionContext precision, bool log, out ReentryContext ctx)
        {
            ctx = null;
            VesselComponent vessel = pilot.current_vessel.VesselComponent;
            CelestialBodyComponent body = vessel.Orbit.referenceBody;
            if (!body.hasAtmosphere || !(body.atmosphereDepth > SampleStep))
                return $"{body.Name} has no atmosphere.";

            Atmosphere air = AtmosphereTables(body);

            // Capsule part set.
            PartOwnerComponent owner = vessel.GetControlOwner()?.PartOwner;
            if (owner == null)
                return "The vessel's parts are not available.";
            var chuteData = new Dictionary<string, Data_Parachute>();
            var names = new Dictionary<string, string>();
            List<CapsulePart> parts = ReadParts(owner, chuteData, names, out _, out int unknownNodes);
            CapsuleSideResult side = CapsuleSide.Find(parts);
            if (!side.Ok)
                return side.Reason;

            // Drag area at retrograde (m², Cd × area), multipliers folded in.
            VesselAeroLookup.SubsetDragTable drag = VesselAeroLookup.BuildRetrogradeDragTable(vessel, side.Ids, side.BoundaryIds);
            if (!drag.found)
                return "Cannot read the capsule's drag (no drag data on its parts, or the vessel is not moving).";
            double pseudoReynolds = PhysicsSettings.DragCurvePseudoReynolds.Evaluate((float)RepresentativeRhoV);
            double dragFactor = PhysicsSettings.DragCubeMultiplier * PhysicsSettings.DragMultiplier * pseudoReynolds;
            var table = new DragTable
            {
                Mach = drag.machSamples.Select(m => (double)m).ToArray(),
                CdA = drag.areaDrag.Select(a => a * dragFactor).ToArray(),
            };
            if (!(table.At(0.5) > 0))
                return "The capsule's drag area is zero.";

            Chutes chutes = ReadChutes(vessel, body, air, precision, side.Ids, chuteData, names, out string chuteLog, out string mixedSources);
            if (!(chutes.Count > 0) || !(chutes.AreaDeployed > 0))
                return "The capsule's parachutes have no drag area.";

            float dragScale = pilot.settings_atmo.reentry_drag_scale.V;
            var capsule = new ReentryVehicle { MassT = side.MassT, Drag = table, Chutes = chutes, DragScale = dragScale };
            double vesselMass = parts.Sum(p => p.MassT);

            if (log)
            {
                var logger = pilot.logger;
                logger.LogInfo($"[Landing] reentry: atmosphere of {body.Name}: depth {air.Depth:n0} m, {air.Density.Length} samples every {SampleStep:n0} m; " +
                    $"density {Rho(air, 0)} / {Rho(air, 10000)} / {Rho(air, 30000)} / {Rho(air, 50000)} kg/m³ at 0 / 10 / 30 / 50 km; " +
                    $"pressure {air.PressureAt(0):0.0000} atm ({air.PressureAt(0) / KPaToAtm:0.00} kPa) and sound speed {air.SoundSpeedAt(0):n0} m/s at sea level");
                string capsuleParts = string.Join(", ", side.Ids.Select(id => names[id] + (side.BoundaryIds.Contains(id) ? " (boundary)" : "")));
                logger.LogInfo($"[Landing] reentry: capsule {side.Ids.Count} of {parts.Count} part(s), {side.MassT:n3} t of {vesselMass:n3} t: {capsuleParts}" +
                    (side.WholeVessel ? " - no decoupler separates the parachutes: the whole vessel" : "") +
                    (unknownNodes > 0 ? $"; {unknownNodes} decoupler(s) with an unknown explosive node, cut on every side" : ""));
                logger.LogInfo($"[Landing] reentry: capsule drag at retrograde CdA {table.At(0.5):0.000} / {table.At(2):0.000} / {table.At(5):0.000} m² at Mach 0.5 / 2 / 5 " +
                    $"(Σ AreaDrag {Sample(drag.areaDrag, drag.machSamples, 0.5f):0.000} with the boundary faces exposed, {Sample(drag.areaDragAttached, drag.machSamples, 0.5f):0.000} as attached, at Mach 0.5; " +
                    $"{drag.partsEvaluated} part(s), {drag.partsExposed} exposed, {drag.partsSkipped} skipped) | DragCubeMultiplier {PhysicsSettings.DragCubeMultiplier:0.###}, " +
                    $"DragMultiplier {PhysicsSettings.DragMultiplier:0.###}, pseudo-Reynolds {pseudoReynolds:0.###} at ρ·v {RepresentativeRhoV:n0} " +
                    $"(curve: {string.Join(", ", LoggedRhoV.Select(x => $"{x:n0} -> {PhysicsSettings.DragCurvePseudoReynolds.Evaluate(x):0.###}"))}) | drag scale {dragScale:0.00}");
                logger.LogInfo("[Landing] reentry: " + chuteLog);
                if (mixedSources != null)
                    logger.LogWarning("[Landing] reentry: chute drag areas come from mixed sources (" + mixedSources +
                        "): the drag cube and areaDeployed may not be in the same scale, and the semi area fraction counts the cube chutes only");
            }

            ctx = new ReentryContext
            {
                Precision = precision,
                Air = air,
                Capsule = capsule,
                CapsulePartIds = side.Ids,
                CapsuleIsWholeVessel = side.WholeVessel,
                VesselMassT = vesselMass,
            };
            return null;
        }

        // The vessel's part tree as CapsuleSide reads it (also used by Jettison): parent, mass,
        // usable (not cut) parachutes, decouplers and their explosive node. Fills the chute data
        // and the part names by id; components holds the PartComponents in the order of the list.
        internal static List<CapsulePart> ReadParts(PartOwnerComponent owner, Dictionary<string, Data_Parachute> chuteData,
            Dictionary<string, string> names, out List<PartComponent> components, out int unknownNodes)
        {
            var parts = new List<CapsulePart>();
            components = new List<PartComponent>();
            unknownNodes = 0;
            foreach (PartComponent part in owner.Parts)
            {
                if (part == null)
                    continue;
                var p = new CapsulePart { Id = part.Guid, ParentId = part.ParentPart?.Guid, MassT = part.TotalMass };
                names[part.Guid] = part.PartName;
                if (part.TryGetModuleData<PartComponentModule_Parachute, Data_Parachute>(out Data_Parachute chute) && chute != null
                    && chute.deployState.GetValue() != Data_Parachute.DeploymentStates.CUT)
                {
                    p.IsParachute = true;
                    chuteData[part.Guid] = chute;
                }
                if (part.TryGetModuleData<PartComponentModule_Decouple, Data_Decouple>(out Data_Decouple decouple) && decouple != null)
                {
                    p.IsDecoupler = true;
                    p.DetachesFromId = DetachesFrom(part, decouple);
                    if (p.DetachesFromId == null)
                        unknownNodes++;
                }
                parts.Add(p);
                components.Add(part);
            }
            return parts;
        }

        // Pressure (atm), density, sound speed and temperature (K) every SampleStep m above sea level, up to the depth.
        internal static Atmosphere AtmosphereTables(CelestialBodyComponent body)
        {
            double depth = body.atmosphereDepth;
            int n = (int)Math.Floor(depth / SampleStep) + 1;
            var air = new Atmosphere
            {
                Depth = depth,
                SampleStep = SampleStep,
                Density = new double[n],
                Pressure = new double[n],
                SoundSpeed = new double[n],
                Temperature = new double[n],
            };
            for (int i = 0; i < n; i++)
            {
                double altitude = i * SampleStep;
                double kPa = body.GetPressure(altitude);
                double temperature = body.GetTemperature(altitude);
                double rho = body.GetDensity(kPa, temperature);
                air.Pressure[i] = Finite(kPa) && kPa > 0 ? kPa * KPaToAtm : 0;
                air.Density[i] = Finite(rho) && rho > 0 ? rho : 0;
                air.Temperature[i] = Finite(temperature) && temperature > 0 ? temperature : 0;
                double a = AeroUtilities.GetSoundSpeed(body.atmosphereAdiabaticIndex, kPa, rho);
                air.SoundSpeed[i] = Finite(a) && a > 0 ? a : 0;
            }
            return air;
        }

        // The id of the part at the decoupler's explosive node, "" when that node holds nothing,
        // null for an omni decoupler or when it cannot be read (CapsuleSide's convention).
        static string DetachesFrom(PartComponent part, Data_Decouple decouple)
        {
            if (decouple.isOmniDecoupler || string.IsNullOrEmpty(decouple.explosiveNodeID))
                return null;
            try
            {
                var attached = part.FindAttachNodeAttachedPart(decouple.explosiveNodeID) as PartComponent;
                return attached != null ? attached.Guid : "";
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The capsule's parachutes: drag area from their DEPLOYED and SEMIDEPLOYED drag cubes, as
        // the game estimates it (else areaDeployed), opening gates from the largest chute.
        static Chutes ReadChutes(VesselComponent vessel, CelestialBodyComponent body, Atmosphere air, PrecisionContext precision,
            HashSet<string> capsuleIds, Dictionary<string, Data_Parachute> chuteData, Dictionary<string, string> names, out string log,
            out string mixedSources)
        {
            mixedSources = null;
            // Resolved here, inside TryBuild's try: a reflection failure is a refusal, not a type
            // initializer exception. Null = not found: areaDeployed is used.
            MethodInfo computeAreaDragSimple =
                typeof(Data_Drag).GetMethod("ComputeAreaDragSimple", BindingFlags.Instance | BindingFlags.NonPublic);
            var behaviors = new Dictionary<string, PartBehavior>();
            foreach (PartBehavior b in vessel.SimulationObject.objVesselBehavior.parts)
                if (b != null && b.Model != null)
                    behaviors[b.Model.Guid] = b;
            PartOwnerComponent owner = vessel.GetControlOwner().PartOwner;
            var components = owner.Parts.Where(p => p != null).ToDictionary(p => p.Guid);

            // The game's reference condition for its chute drag estimate: 1000 m, 100 m/s.
            double rho1k = air.DensityAt(1000), a1k = air.SoundSpeedAt(1000);
            float mach1k = a1k > 0 ? (float)(100 / a1k) : 0;
            double estimateFactor = PhysicsSettings.DragCubeMultiplier * PhysicsSettings.DragMultiplier
                * PhysicsSettings.DragCurvePseudoReynolds.Evaluate((float)(rho1k * 100));

            var lines = new List<string>();
            double totalFull = 0, totalSemi = 0, largest = -1;
            int count = 0, fromCube = 0;
            Data_Parachute main = null;
            double mainMaxSafe = double.NaN;
            foreach (string id in capsuleIds)
            {
                if (!chuteData.TryGetValue(id, out Data_Parachute d))
                    continue;
                double full = double.NaN, semi = double.NaN;
                string source = "areaDeployed";
                if (components.TryGetValue(id, out PartComponent part)
                    && part.TryGetModuleData<PartComponentModule_Drag, Data_Drag>(out Data_Drag dragData) && dragData?.cubes != null
                    && computeAreaDragSimple != null)
                {
                    try
                    {
                        foreach (DragCube cube in dragData.cubes)
                        {
                            if (cube == null)
                                continue;
                            if (cube.Name == "DEPLOYED")
                                full = Convert.ToDouble(computeAreaDragSimple.Invoke(dragData, new object[] { cube, Vector3.up, (double)mach1k })) * estimateFactor;
                            else if (cube.Name == "SEMIDEPLOYED")
                                semi = Convert.ToDouble(computeAreaDragSimple.Invoke(dragData, new object[] { cube, Vector3.up, (double)mach1k })) * estimateFactor;
                        }
                    }
                    catch (Exception e)
                    {
                        source = $"areaDeployed (drag cubes threw {e.GetType().Name})";
                        full = semi = double.NaN;
                    }
                }
                double area = Finite(full) && full > 0 ? full : d.areaDeployed;
                if (Finite(full) && full > 0)
                    source = "DEPLOYED cube";
                if (!(area > 0))
                    continue;
                count++;
                if (Finite(full) && full > 0)
                    fromCube++;
                totalFull += area;
                if (Finite(semi) && semi > 0 && Finite(full) && full > 0)
                    totalSemi += semi;

                double maxSafe = double.NaN;
                if (behaviors.TryGetValue(id, out PartBehavior behavior))
                {
                    try { maxSafe = behavior.GetModule<Module_Parachute>()?.MaxSafeVelocityAtLocalBody() ?? double.NaN; }
                    catch (Exception) { }
                }
                lines.Add($"{names[id]}: CdA {area:0.00} m² ({source}; areaDeployed {d.areaDeployed:0.00}, semi cube {semi:0.00}), " +
                    $"mode {d.DeploymentMode.GetValue()}, deployAltitude {d.deployAltitude.GetValue():n0} m, minAirPressureToOpen {d.minAirPressureToOpen.GetValue():0.000} atm, " +
                    $"minimumSpeedToDeploy {d.minimumSpeedToDeploy:0.##} (m²/s², compared to v²), MachSafetyScalar {d.MachSafetyScalar:0.###}, " +
                    $"FluxSafetyScalar {d.FluxSafetyScalar:0.###}, MaxSafeVelocityAtLocalBody {maxSafe:n0} m/s now");
                if (area > largest)
                {
                    largest = area;
                    main = d;
                    mainMaxSafe = maxSafe;
                }
            }

            var chutes = new Chutes { Count = count };
            if (count == 0 || main == null)
            {
                log = "no usable parachute: " + string.Join("; ", lines);
                return chutes;
            }
            chutes.AreaDeployed = totalFull / count;
            chutes.SemiAreaFraction = totalSemi > 0 ? totalSemi / totalFull : 0;
            chutes.MinPressureToOpen = main.minAirPressureToOpen.GetValue();
            chutes.DeployAltitude = main.deployAltitude.GetValue();
            chutes.MinDeploySpeed = main.minimumSpeedToDeploy > 0 ? Math.Sqrt(main.minimumSpeedToDeploy) : 0;

            if (fromCube > 0 && fromCube < count)
                mixedSources = $"{fromCube} of {count} from the DEPLOYED cube, the others from areaDeployed";

            // SAFE: Mach < MachSafetyScalar, checked by the predictor against the sound speed at the
            // current altitude (the game's own test). Not modelled: the heat flux limit
            // (FluxSafetyScalar) and the SAFE state granted at any Mach while the chute cools.
            // RISKY / IMMEDIATE: no Mach limit; their thermal limit is not modelled.
            Data_Parachute.DeployMode mode = main.DeploymentMode.GetValue();
            double terrain = Math.Max(0, precision.TargetTerrainM);
            double gate = PressureGateAltitude(air, chutes.MinPressureToOpen);
            chutes.MaxDeploySpeed = 0;
            chutes.MaxDeployMach = mode == Data_Parachute.DeployMode.SAFE && main.MachSafetyScalar > 0 ? main.MachSafetyScalar : 0;

            bool mixed = chuteData.Where(kv => capsuleIds.Contains(kv.Key)).Select(kv => kv.Value).Any(d =>
                d.minAirPressureToOpen.GetValue() != main.minAirPressureToOpen.GetValue() || d.deployAltitude.GetValue() != main.deployAltitude.GetValue()
                || d.DeploymentMode.GetValue() != mode || d.minimumSpeedToDeploy != main.minimumSpeedToDeploy || d.MachSafetyScalar != main.MachSafetyScalar);
            log = $"{count} chute(s), CdA {chutes.AreaDeployed:0.00} m² each ({totalFull:0.00} total), semi area fraction " +
                (chutes.SemiAreaFraction > 0 ? $"{chutes.SemiAreaFraction:0.000} (cubes)" : "default") +
                $"; gates from the largest: open at pressure >= {chutes.MinPressureToOpen:0.000} atm (reached at {gate:n0} m above sea level), " +
                $"full at {chutes.DeployAltitude:n0} m above the ground (the game reads AltitudeFromSurface; " +
                $"{terrain + chutes.DeployAltitude:n0} m above sea level at the target, {chutes.DeployAltitude:n0} m if it were sea level), " +
                $"air speed >= {chutes.MinDeploySpeed:0.0} m/s, " +
                (chutes.MaxDeployMach > 0
                    ? $"Mach <= {chutes.MaxDeployMach:0.###} at the local sound speed (mode {mode}: MachSafetyScalar; heat flux limit (FluxSafetyScalar " +
                      $"{main.FluxSafetyScalar:0.###}) and the SAFE state while the chute cools not modelled; MaxSafeVelocityAtLocalBody now {mainMaxSafe:n0} m/s, not used)"
                    : mode == Data_Parachute.DeployMode.RISKY
                        ? $"no Mach limit (mode {mode}: thermal limit not modelled)"
                        : $"no upper limit (mode {mode}: thermal limit not modelled)") +
                (mixed ? " - chutes with different settings: drogues and mains are modelled as one" : "") +
                " | " + string.Join("; ", lines);
            return chutes;
        }

        // Highest table altitude whose pressure reaches minPressure (NaN if none).
        static double PressureGateAltitude(Atmosphere air, double minPressure)
        {
            for (int i = air.Pressure.Length - 1; i >= 0; i--)
                if (air.Pressure[i] > 0 && air.Pressure[i] >= minPressure)
                    return i * air.SampleStep;
            return double.NaN;
        }

        static double Sample(double[] values, float[] mach, float at)
        {
            var t = new DragTable { Mach = mach.Select(m => (double)m).ToArray(), CdA = values };
            return t.At(at);
        }

        static string Rho(Atmosphere air, double altitude) => air.DensityAt(altitude).ToString("0.####");

        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    }
}
