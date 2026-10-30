using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Landing.Braking;
using K2D2.Landing.Heating;
using K2D2.Landing.Precision;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;

namespace K2D2.Landing
{
    // Inputs of the normal landing's descent plan (Heating/DescentPlanner) read from the live game
    // once: state, body (the pilot's rotation sign), the vehicle (active engines, stages of a landing
    // with a chosen stage) and, on an atmospheric body, the air tables, the vessel's drag at
    // retrograde and the parts the heating model integrates. Every game call is here, in one
    // try/catch: a failure is a refusal with a reason (or, for the heating parts alone, a plan without
    // heating, said in the log). Not unit-tested (game types).
    //
    // Read in the IL of Ksp2Redux.dll 26w42c (2026-10-30), see Heating/ThermalModel.cs:
    // - the reentry flux's area and Cd are the part's drag cube in the flow direction
    //   (Module_Drag.UpdateCubeData: CubeData.AreaAirstream, CubeData.DragCoeff), so they are read
    //   here with Data_Drag.GetAeroDataForDirection at retrograde, as BuildRetrogradeDragTable does;
    // - ThermalMass, MaxTemp and Temperature are PartComponent's; WettedArea is the ECS ThermalData's
    //   (the game falls back to PartConfigBlob.ThermalArea), the emissivity PartData.emissiveConstant;
    // - the constants are PhysicsSettings' static members the game's own thermal job reads, the body
    //   scalar CelestialBodyComponent.reentryHarshness, the difficulty "HeatScaling" through
    //   SessionManager.TryGetDifficultyOptionState (1 when absent, as the game does).
    public class DescentContext
    {
        public Vector3d R, V;
        public double UT;
        public Precision.Body Body;
        public Precision.Vehicle Vehicle;
        public bool Atmospheric;
        public Atmosphere Air;
        public DescentVehicle Aero;
        public string AeroNote;          // why the heating is not modelled, or what was read (log)
        public string BodyName;

        // Parts integrated by the predictor: the most at risk (Heating.HeatRisk).
        public const int MaxHeatedParts = 12;
        static readonly float[] MachSamples = { 0.5f, 1, 2, 3, 5, 8, 12, 20 };

        public static bool TryBuild(LandingPilot pilot, bool withAero, out DescentContext ctx, out string reason)
        {
            ctx = null;
            try
            {
                reason = Build(pilot, withAero, out ctx);
            }
            catch (Exception e)
            {
                reason = $"Cannot read the game state for the descent ({e.GetType().Name}: {e.Message}).";
            }
            if (reason != null)
                ctx = null;
            return reason == null;
        }

        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        static string Build(LandingPilot pilot, bool withAero, out DescentContext ctx)
        {
            ctx = null;
            VesselComponent vessel = pilot?.current_vessel?.VesselComponent;
            if (vessel?.Orbit == null)
                return "No active vessel.";
            IKeplerPatch orbit = vessel.Orbit;
            CelestialBodyComponent body = orbit.referenceBody;
            if (body == null)
                return "The vessel has no reference body.";
            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            if (!Finite(r.magnitude) || !Finite(v.magnitude) || r.magnitude <= 0)
                return "The vessel's position or velocity is not available.";

            double omega = BodyRotation.AngularSpeed(body.rotationPeriod);
            pilot.UpdateRotationSign(body, r, v, omega);
            var bodyIn = new Precision.Body
            {
                Mu = body.gravParameter,
                Radius = body.radius,
                Omega = BodyRotation.AngularVelocity(omega, pilot.rotation_sign),
                TerrainHeight = p => LandingPilot.TerrainHeightAt(body, p),
            };

            pilot.burn_dV.Compute_Thrust();
            double thrust = pilot.burn_dV.active_thrust;
            if (!(thrust > 0))
                thrust = pilot.burn_dV.full_thrust.magnitude;
            double mass = pilot.burn_dV.mass;
            double isp = pilot.burn_dV.active_isp;
            if (!(thrust > 0))
                return "No engine thrust available.";
            if (!(mass > 0))
                return "The vessel's mass is not available.";
            var vehicle = new Precision.Vehicle
            {
                ThrustKN = thrust,
                MassT = mass,
                Isp = Finite(isp) && isp > 0 ? isp : 0,
                StopHeight = 0,   // the normal landing brakes to BrakeSimulator.SafeAltitude
            };
            if (pilot.StagedBraking)
            {
                vehicle.Stages = pilot.StagesForPlans(out string stageError);
                if (stageError != null)
                    return "The vessel's stages: " + stageError;
                vehicle.DropHeight = StagedBrakePlan.Floor(pilot.PlayerDropHeight, BrakeSimulator.SafeAltitude);
            }

            ctx = new DescentContext
            {
                R = r, V = v, UT = now, Body = bodyIn, Vehicle = vehicle, BodyName = body.Name,
                Atmospheric = body.hasAtmosphere && body.atmosphereDepth > ReentryContext.SampleStep,
            };
            if (ctx.Atmospheric)
            {
                ctx.Air = ReentryContext.AtmosphereTables(body);
                if (withAero)
                {
                    string why = ReadAero(pilot, vessel, body, ctx);
                    if (why != null)
                    {
                        ctx = null;
                        return why;
                    }
                }
            }
            return null;
        }

        // Whole-vessel drag at retrograde and the parts heated. A drag failure refuses (the descent
        // cannot be predicted); a heating-part failure only drops the heating (logged).
        static string ReadAero(LandingPilot pilot, VesselComponent vessel, CelestialBodyComponent body, DescentContext ctx)
        {
            PartOwnerComponent owner = vessel.GetControlOwner()?.PartOwner ?? vessel.SimulationObject?.PartOwner;
            if (owner == null)
                return "The vessel's parts are not available.";
            var ids = new HashSet<string>(owner.Parts.Where(p => p != null).Select(p => p.Guid));
            VesselAeroLookup.SubsetDragTable drag = VesselAeroLookup.BuildRetrogradeDragTable(vessel, ids, null);
            if (!drag.found)
                return "Cannot read the vessel's drag (no drag data on its parts, or the vessel is not moving).";
            double pseudoReynolds = PhysicsSettings.DragCurvePseudoReynolds.Evaluate((float)ReentryContext.RepresentativeRhoV);
            double dragFactor = PhysicsSettings.DragCubeMultiplier * PhysicsSettings.DragMultiplier * pseudoReynolds;
            var table = new DragTable
            {
                Mach = drag.machSamples.Select(m => (double)m).ToArray(),
                CdA = drag.areaDragAttached.Select(a => a * dragFactor).ToArray(),
            };
            if (!(table.At(0.5) > 0))
                return "The vessel's drag area is zero.";

            ThermalConstants constants = ReadConstants(out string constantsNote);
            ThermalPart[] parts;
            string partsNote;
            try
            {
                parts = ReadHeatedParts(vessel, owner, out partsNote);
            }
            catch (Exception e)
            {
                parts = new ThermalPart[0];
                partsNote = $"heating not modelled: the parts' thermal data could not be read ({e.GetType().Name}: {e.Message})";
            }
            double harshness = 1;
            try { harshness = body.reentryHarshness; } catch (Exception) { }
            if (!(harshness > 0) || !Finite(harshness))
                harshness = 1;

            ctx.Aero = new DescentVehicle
            {
                MassT = ctx.Vehicle.MassT,
                Drag = table,
                DragScale = 1,
                Parts = parts,
                Constants = constants,
                Harshness = harshness,
            };
            ctx.AeroNote = $"drag at retrograde CdA {table.At(0.5):0.00} / {table.At(2):0.00} / {table.At(5):0.00} m² at Mach 0.5 / 2 / 5 " +
                $"({drag.partsEvaluated} part(s), {drag.partsSkipped} skipped) | {constants}{constantsNote} | harshness {harshness:0.###} | {partsNote}";
            return null;
        }

        static ThermalConstants ReadConstants(out string note)
        {
            ThermalConstants c = ThermalConstants.Defaults;
            note = "";
            try
            {
                c.FluxScale = PhysicsSettings.GLOBAL_REENTRY_FLUX_SCALE;
                c.ReferenceSpeed = PhysicsSettings.GLOBAL_REENTRY_FLUX_REFERENCE_SPEED;
                c.VelocityExponent = PhysicsSettings.GLOBAL_REENTRY_FLUX_VELOCITY_EXPONENT;
                c.DensityExponent = PhysicsSettings.GLOBAL_REENTRY_FLUX_DENSITY_EXPONENT;
                c.SkinFrictionScale = PhysicsSettings.GLOBAL_REENTRY_SKIN_FRICTION_SCALE;
                c.SkinFrictionExponent = PhysicsSettings.THERMAL_SKIN_FRICTION;
                c.AtmosphereFluxScale = PhysicsSettings.GLOBAL_ATMOSPHERE_FLUX_SCALE;
                c.AtmConductivity = PhysicsSettings.ATMOSPHERIC_THERMAL_CONDUCTIVITY;
                c.WindowSmoothing = PhysicsSettings.THERMAL_WINDOW_SMOOTHING;
                c.StefanBoltzmann = PhysicsSettings.StefanBoltzmanConstant;
                c.RadiationFactor = PhysicsSettings.ThermalRadiationEnabled ? PhysicsSettings.RadiationFactor : 0;
                c.PositiveRateScale = PhysicsSettings.GLOBAL_POSITIVE_PART_TEMPERATURE_CHANGE_RATE_SCALE;
                c.NegativeRateScale = PhysicsSettings.GLOBAL_NEGATIVE_PART_TEMPERATURE_CHANGE_RATE_SCALE;
            }
            catch (Exception e)
            {
                c = ThermalConstants.Defaults;
                note = $" (PhysicsSettings not read: {e.GetType().Name}, the 2026-10-30 defaults used)";
            }
            try
            {
                var session = GeneralTools.Game?.SessionManager;
                if (session != null && session.TryGetDifficultyOptionState("HeatScaling", out float scale) && scale > 0)
                    c.HeatScale = scale;
                note += $" | difficulty HeatScaling {c.HeatScale:0.###}";
            }
            catch (Exception e)
            {
                note += $" | difficulty HeatScaling not read ({e.GetType().Name}): 1";
            }
            return c;
        }

        // The parts at retrograde: airstream area and Cd of their drag cube against Mach (as attached,
        // the faces covered by neighbours counting less), and their thermal data. The MaxHeatedParts
        // with the highest risk are kept.
        static ThermalPart[] ReadHeatedParts(VesselComponent vessel, PartOwnerComponent owner, out string note)
        {
            var behavior = vessel.SimulationObject?.objVesselBehavior;
            var control = behavior?.ControlTransform;
            if (behavior == null || control == null)
            {
                note = "heating not modelled: no vessel behaviour";
                return new ThermalPart[0];
            }
            var behaviors = new Dictionary<string, PartBehavior>();
            PartBehavior reference = null;
            foreach (PartBehavior b in behavior.parts)
            {
                if (b == null)
                    continue;
                reference ??= b;
                behaviors[b.Guid.ToString()] = b;
            }
            if (reference == null || reference.VelocityRelativeToSOIBodyFrameInPhysicsSpace.sqrMagnitude < 0.01f)
            {
                note = "heating not modelled: the vessel is not moving";
                return new ThermalPart[0];
            }
            Quaternion inverseDelta = Quaternion.Inverse(Quaternion.FromToRotation(control.forward,
                (-reference.VelocityRelativeToSOIBodyFrameInPhysicsSpace).normalized));

            var candidates = new List<(ThermalPart part, double risk)>();
            int read = 0, skipped = 0, noWetted = 0;
            foreach (PartComponent part in owner.Parts)
            {
                if (part == null)
                    continue;
                try
                {
                    double maxTemp = part.MaxTemp, capacity = part.ThermalMass;
                    if (!(maxTemp > 0) || !(capacity > 0)
                        || !behaviors.TryGetValue(part.Guid, out PartBehavior pb) || pb == null
                        || !part.TryGetModuleData<PartComponentModule_Drag, Data_Drag>(out Data_Drag dragData) || dragData == null)
                    {
                        skipped++;
                        continue;
                    }
                    Vector3 flow = inverseDelta * pb.VelocityRelativeToSOIBodyFrameInPhysicsSpace.normalized;
                    Vector3 local = pb.ModelTransform.InverseTransformDirection(flow);
                    var area = new double[MachSamples.Length];
                    var cd = new double[MachSamples.Length];
                    for (int m = 0; m < MachSamples.Length; m++)
                    {
                        var cube = default(Data_Drag.CubeData);
                        dragData.GetAeroDataForDirection(local, MachSamples[m], ref cube);
                        area[m] = Finite(cube.AreaAirstream) && cube.AreaAirstream > 0 ? cube.AreaAirstream : 0;
                        cd[m] = Finite(cube.DragCoeff) && cube.DragCoeff > 0 ? cube.DragCoeff : 0;
                    }
                    KSP.ThermalData td = part.ThermalData;
                    double wetted = Finite(td.WettedArea) && td.WettedArea > 0 ? td.WettedArea : 0;
                    if (wetted == 0)
                        noWetted++;
                    double emissive = part.PartData != null ? part.PartData.emissiveConstant : 0;
                    var machs = MachSamples.Select(x => (double)x).ToArray();
                    var tp = new ThermalPart
                    {
                        Name = part.PartName,
                        ThermalMass = capacity,
                        MaxTemp = maxTemp,
                        Temperature = Finite(part.Temperature) ? part.Temperature : 0,
                        AirstreamArea = new DragTable { Mach = machs, CdA = area },
                        DragCoefficient = new DragTable { Mach = machs, CdA = cd },
                        WettedArea = wetted,
                        Emissivity = emissive,
                    };
                    read++;
                    double risk = HeatRisk(tp);
                    if (risk > 0)
                        candidates.Add((tp, risk));
                }
                catch (Exception)
                {
                    skipped++;
                }
            }
            ThermalPart[] kept = candidates.OrderByDescending(c => c.risk).Take(MaxHeatedParts).Select(c => c.part).ToArray();
            note = $"heated parts: {kept.Length} of {read} read ({candidates.Count} facing the flow, {skipped} skipped, {noWetted} without wetted area: no cooling counted)" +
                (kept.Length > 0 ? ": " + string.Join(", ", kept.Select(p => $"{p.Name} {p.Temperature:n0}/{p.MaxTemp:n0} K, A {p.AirstreamArea.At(5):0.00} m², Cd {p.DragCoefficient.At(5):0.00}, C {p.ThermalMass:n0} kJ/K")) : "");
            return kept;
        }

        // Heating rate per unit of flux factor against the room left under MaxTemp: the parts that
        // would get hot first. 0 for a part with no area facing the flow.
        internal static double HeatRisk(ThermalPart p)
        {
            double area = p.AirstreamArea.At(5);
            double cd = p.DragCoefficient.At(5);
            if (!(area > 0) || !(cd > 0) || !(p.ThermalMass > 0))
                return 0;
            double room = Math.Max(1, p.MaxTemp - p.Temperature);
            return area * Math.Pow(cd, 0.3) / p.ThermalMass / room;
        }
    }
}
