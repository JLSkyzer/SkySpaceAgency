using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using Redux.Ecs.Components;
using Unity.Entities;

namespace K2D2.Transfer
{
    /// <summary>
    /// Game glue: what the transfer pilot logs to settle the 2026-10-25 in-game failures (the Kerbin
    /// SOI left ~30 000 s early, an encounter that never switched). Read only, every read guarded.
    ///
    /// Facts from the IL of Ksp2Redux.dll (Redux 26w42c, read with Mono.Cecil):
    /// - the vessel's orbit is a Redux.Ecs.Components.CurrentPatchedConicsOrbit, a view of the ECS
    ///   component PatchedConicsOrbitData of its entity (get_Data); the elements we read come from it;
    /// - OrbitDriverSystem.OrbitDriverJob moves a vessel on rails with PatchedConicsOrbitData.UpdateFromUT
    ///   at SimUniverseTime.UniverseTime, then sets OrbiterMainBody.Body with
    ///   CelestialBodyQueries.GetMainBody: from the root down, the first child whose SOI holds the
    ///   vessel's localPosition (Transformd.ToLocalPosition into that child's celestial frame,
    ///   compared with CelestialBodySimData.SphereOfInfluence). A vessel in RigidBody mode is skipped
    ///   (IsOnRails false): only its main body is computed, from its OrbiterFrame;
    /// - so the SOI the game uses is the one we read, and the main body comes from the ECS positions:
    ///   a mismatch between the orbit's position and the world position shows up here.
    /// </summary>
    internal static class TransferDiagnostics
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // Private members of CurrentPatchedConicsOrbit (read only, for the log).
        static readonly FieldInfo EntityField = typeof(CurrentPatchedConicsOrbit).GetField("_entity", Any);
        static readonly PropertyInfo DataProperty = typeof(CurrentPatchedConicsOrbit).GetProperty("Data", Any);
        static readonly PropertyInfo EmProperty = typeof(CurrentPatchedConicsOrbit).GetProperty("EM", Any);

        static string F(double x, string format = "F0") => x.ToString(format, CultureInfo.InvariantCulture);

        // The managed orbit (what we and the game's patch solver read) and the ECS data behind it.
        public static string Orbit(VesselComponent vessel, double ut, out double energy, out double eccentricity, out double radius)
        {
            energy = eccentricity = radius = double.NaN;
            try
            {
                IKeplerPatch orbit = vessel?.Orbit;
                if (orbit == null)
                    return "no orbit";
                var body = orbit.referenceBody;
                double mu = body.gravParameter;
                Vector3d r = orbit.GetRelativePositionAtUTZup(ut), v = orbit.GetOrbitalVelocityAtUTZup(ut);
                var el = OrbitMath.Elements(r, v, mu);
                energy = 0.5 * v.sqrMagnitude - mu / r.magnitude;
                eccentricity = el.Eccentricity;
                radius = r.magnitude;
                string text = $"managed around {body.bodyName}: r {F(r.magnitude)} m, v {F(v.magnitude, "F2")} m/s, e {F(el.Eccentricity, "F6")}, " +
                              $"a {F(el.SemiMajorAxis)} m, energy {F(energy, "F3")}";
                text += "; " + Ecs(orbit, ut);
                return text;
            }
            catch (Exception e)
            {
                return $"orbit unreadable ({e.GetType().Name}: {e.Message})";
            }
        }

        static string Ecs(IKeplerPatch orbit, double ut)
        {
            if (!(orbit is CurrentPatchedConicsOrbit current))
                return $"ECS: the orbit is a {orbit.GetType().Name}";
            try
            {
                if (!(DataProperty?.GetValue(current) is PatchedConicsOrbitData d))
                    return "ECS: no data";
                var el = d.OrbitalElements;
                string text = $"ECS: radius {F(d.radius)} m, |localPosition| {F(d.localPosition.magnitude)} m, e {F(el.Eccentricity, "F6")}, " +
                              $"a {F(el.SemiMajorAxis)} m, epoch {F(el.Epoch, "F1")}, ObT {F(d.ObT, "F1")}, ObTAtEpoch {F(d.ObTAtEpoch, "F1")}, " +
                              $"UT {F(ut, "F1")}";
                text += ", " + MainBody(current);
                return text;
            }
            catch (Exception e)
            {
                return $"ECS unreadable ({e.GetType().Name}: {e.Message})";
            }
        }

        // OrbiterMainBody (the body the game's ECS puts the vessel in) and SimUniverseTime.
        static string MainBody(CurrentPatchedConicsOrbit current)
        {
            try
            {
                if (!(EmProperty?.GetValue(current) is EntityManager em))
                    return "main body unknown (no entity manager)";
                object boxed = EntityField?.GetValue(current);
                if (!(boxed is Entity entity))
                    return "main body unknown (no entity)";
                string text = "";
                if (em.HasComponent<OrbiterMainBody>(entity))
                {
                    Entity main = em.GetComponentData<OrbiterMainBody>(entity).Body;
                    text = $"main body {BodyName(main)}";
                }
                try
                {
                    var query = em.CreateEntityQuery(ComponentType.ReadOnly<SimUniverseTime>());
                    if (query.CalculateEntityCount() == 1)
                        text += $", SimUniverseTime {F(query.GetSingleton<SimUniverseTime>().UniverseTime, "F1")}";
                }
                catch (Exception e)
                {
                    text += $", SimUniverseTime unreadable ({e.GetType().Name})";
                }
                return text;
            }
            catch (Exception e)
            {
                return $"main body unreadable ({e.GetType().Name}: {e.Message})";
            }
        }

        static string BodyName(Entity entity)
        {
            if (entity == Entity.Null)
                return "none";
            var bodies = GameBodySystem.GameBodies();
            if (bodies != null)
                foreach (var b in bodies)
                    if (b?.SimulationObject != null && b.SimulationObject.entity == entity)
                        return b.bodyName;
            return $"entity {entity.Index}";
        }

        // The vessel's distance to its reference body from the game's world positions, against the
        // radius of the orbit: they differ when the orbit does not follow the physical vessel.
        public static double WorldVsOrbit(VesselComponent vessel, double ut, out string text)
        {
            text = "";
            try
            {
                IKeplerPatch orbit = vessel.Orbit;
                var body = orbit.referenceBody;
                double world = Position.Distance(vessel.SimulationObject.Position, body.Position);
                double fromOrbit = orbit.GetRelativePositionAtUTZup(ut).magnitude;
                text = $"distance to {body.bodyName}: world {F(world)} m, orbit {F(fromOrbit)} m (diff {F(world - fromOrbit)} m), physics {vessel.Physics}";
                return Math.Abs(world - fromOrbit);
            }
            catch (Exception e)
            {
                text = $"world vs orbit unreadable ({e.GetType().Name}: {e.Message})";
                return double.NaN;
            }
        }

        // The distance to each body, from the game's world positions and from our BodySystem.
        public static string Distances(VesselComponent vessel, IReadOnlyList<CelestialBodyComponent> bodies, BodySystem s,
            int vesselBody, Vector3d r, double ut, params int[] which)
        {
            var parts = new List<string>();
            foreach (int b in which)
            {
                if (b < 0 || bodies == null || b >= bodies.Count || s == null || b >= s.Count)
                    continue;
                string world;
                try
                {
                    world = F(Position.Distance(vessel.SimulationObject.Position, bodies[b].Position));
                }
                catch (Exception e)
                {
                    world = $"unreadable ({e.GetType().Name})";
                }
                string ours;
                try
                {
                    ours = F(OurDistance(s, vesselBody, r, b, ut, out _));
                }
                catch (Exception e)
                {
                    ours = $"unreadable ({e.GetType().Name})";
                }
                parts.Add($"{s[b].Name}: world {world} m, ours {ours} m (SOI {F(s[b].Soi)} m)");
            }
            return string.Join("; ", parts);
        }

        // From our BodySystem: the vessel (r around vesselBody) to body b, through the root.
        public static double OurDistance(BodySystem s, int vesselBody, Vector3d r, int b, double ut, out Vector3d relative)
        {
            var path = s.PathToRoot(vesselBody);
            int root = path[path.Count - 1];
            s.StateRelativeTo(vesselBody, root, ut, out var rb, out _);
            s.StateRelativeTo(b, root, ut, out var rt, out _);
            relative = rb + r - rt;
            return relative.magnitude;
        }
    }
}
