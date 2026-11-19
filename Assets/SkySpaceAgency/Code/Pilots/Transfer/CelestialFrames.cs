using System;
using System.Collections.Generic;
using SkySpaceAgency.Node;
using SkySpaceAgency.OrbitPlanning;
using KSP.Sim;

namespace SkySpaceAgency.Transfer
{
    // The orientation of the game's Zup vectors, body by body. Pure code: no game API.
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c and 26w46a, PatchedConicsOrbit.
    // GetPositionAtUTInStarSpace and CelestialBodyOrderedUpdateSystem):
    // - an orbit's GetRelativePositionAtUTZup / GetOrbitalVelocityAtUTZup are in the celestial frame
    //   of its reference body, and that frame is turned within its parent's by the body's
    //   CelestialTilt: the rotation of the body's own orbit frame (inclination, longitude of the
    //   ascending node, argument of periapsis) times its axial tilt;
    // - the game sums a chain as Σ L·zup, with L the product of the CelestialTilt.swizzle of the
    //   reference bodies from the root down (the Zup form of the Yup rotation).
    // Kerbin's orbit (inclination, node and periapsis argument all 0) leaves its frame aligned with
    // the root's, which is why the transfers from Kerbin and the Mun never showed it. Minmus (6°,
    // 78°, 38° in stock KSP) turns it by about 116°: a Minmus → Kerbin return planned with Minmus's
    // vectors taken as Kerbin's left on an asymptote turned that much (in game, 2026-11-18: Kerbin
    // periapsis 37 831 km instead of 100 km).
    //
    // The transfer code works in one common frame, the root's celestial frame (the root's own tilt
    // left out): a vector around body b, as the game gives it, times ToCommon(b).
    public static class CelestialFrames
    {
        // The rotation from body's celestial frame to the root's: tilt(c1) · … · tilt(body), c1 the
        // root's child on the way. tilt(b): Zup rotation of b's frame within its parent's
        // (CelestialTilt.swizzle). Identity for the root itself.
        public static QuaternionD ToCommon(int body, Func<int, int> parentOf, Func<int, QuaternionD> tiltOf)
        {
            var q = QuaternionD.identity;
            for (int b = body; b >= 0 && parentOf(b) >= 0; b = parentOf(b))
                q = tiltOf(b) * q;
            return q;
        }

        public static QuaternionD ToCommon(int body, IReadOnlyList<int> parents, IReadOnlyList<QuaternionD> tilts) =>
            ToCommon(body, b => parents[b], b => tilts[b]);

        public static Vector3d ToLocal(QuaternionD toCommon, Vector3d common) => QuaternionD.Inverse(toCommon) * common;

        public static bool IsIdentity(QuaternionD q) => Math.Abs(Math.Abs(q.w) - 1) < 1e-12;

        // The angle (°) the frame is turned by, for the logs.
        public static double AngleDeg(QuaternionD q) =>
            2 * Math.Acos(Math.Min(1, Math.Abs(q.w) / Math.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w))) * 180 / Math.PI;

        // A guidance built in the common frame, for a burn that reads the vessel's state as the game
        // gives it (TargetOrbitBurn: the frame of the vessel's reference body, whose ToCommon is
        // toCommon). A target orbit keeps its type (TargetOrbitBurn validates and logs it as such),
        // its plane turned into that frame; any other guidance is wrapped. Unchanged under an
        // identity rotation.
        public static IBurnGuidance InLocalFrame(IBurnGuidance guidance, QuaternionD toCommon)
        {
            if (guidance == null || IsIdentity(toCommon))
                return guidance;
            if (guidance is OrbitTargetGuidance orbit)
            {
                var t = orbit.Target;
                t.PlaneNormal = ToLocal(toCommon, t.PlaneNormal);
                return new OrbitTargetGuidance(t);
            }
            return new FramedGuidance(guidance, toCommon);
        }
    }

    // A guidance in the common frame, evaluated from a state in a body's frame: the state is turned
    // into the common frame, the answer back.
    public sealed class FramedGuidance : IBurnGuidance, IFixedAtIgnition
    {
        readonly IBurnGuidance inner;
        readonly QuaternionD toCommon, toLocal;

        public FramedGuidance(IBurnGuidance inner, QuaternionD toCommon)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.toCommon = toCommon;
            toLocal = QuaternionD.Inverse(toCommon);
        }

        public IBurnGuidance Inner => inner;

        public Vector3d DeltaVToGo(Vector3d r, Vector3d v, double ut, double mu) =>
            toLocal * inner.DeltaVToGo(toCommon * r, toCommon * v, ut, mu);

        public void FixAtIgnition(Vector3d r, Vector3d v, double ut, double mu) =>
            (inner as IFixedAtIgnition)?.FixAtIgnition(toCommon * r, toCommon * v, ut, mu);

        public string Describe() => inner.Describe() + FormattableString.Invariant($" (frame turned {CelestialFrames.AngleDeg(toCommon):0.0}°)");
    }
}
