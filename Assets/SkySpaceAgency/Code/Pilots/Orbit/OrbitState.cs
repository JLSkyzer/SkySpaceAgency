using SkySpaceAgency.KSPService;
using KSP.Sim;
using KTools;

namespace SkySpaceAgency.OrbitPlanning
{
    /// <summary>
    /// Reads of the active vessel's trajectory for the planner, shared by the Orbit tab and the Node
    /// pilot (which plans the next step of a plan in progress), and the capture line of a plan summary.
    /// </summary>
    public static class OrbitState
    {
        /// <summary>
        /// The active vessel's state now (Zup frame, relative to its body) and its body, as the
        /// planner takes them.
        /// </summary>
        public static bool TryReadState(out Vector3d r, out Vector3d v, out double ut, out BodyInfo body)
        {
            return TryReadState(out r, out v, out ut, out body, out _);
        }

        /// <summary>As above, with the inclination the game reports (unit unconfirmed: the frame guard accepts radians or degrees).</summary>
        public static bool TryReadState(out Vector3d r, out Vector3d v, out double ut, out BodyInfo body,
            out double gameInclination)
        {
            r = default;
            v = default;
            ut = 0;
            body = default;
            gameInclination = 0;
            var vessel = SkySpaceAgencyPlugin.Instance?.current_vessel?.VesselComponent;
            IKeplerPatch orbit = vessel?.Orbit;
            if (orbit == null)
                return false;
            var reference = orbit.referenceBody;
            var parent = reference.referenceBody;
            ut = GeneralTools.Current_UT;
            r = orbit.GetRelativePositionAtUTZup(ut);
            v = orbit.GetOrbitalVelocityAtUTZup(ut);
            body = new BodyInfo
            {
                Mu = reference.gravParameter,
                Radius = reference.radius,
                AtmosphereDepth = reference.hasAtmosphere ? reference.atmosphereDepth : 0,
                // The star has no parent: its SOI is unbounded.
                SoiRadius = parent == null || ReferenceEquals(parent, reference) ? double.PositiveInfinity : reference.sphereOfInfluence
            };
            gameInclination = orbit.inclination;
            return true;
        }

        public static string CaptureLine(CaptureInfo capture, string bodyName, double bodyRadius)
        {
            string where = capture.AtPeriapsis ? $"at the {bodyName} periapsis" : $"as soon as possible around the {bodyName}";
            return $"Capture {where} ({(capture.Radius - bodyRadius) / 1000:n0} km) first: {capture.DeltaV:n0} m/s.";
        }
    }
}
