using System;
using KSP.Sim;

namespace SkySpaceAgency.Transfer
{
    // One celestial body, as plain numbers: filled from the game at run time (GameBodySystem) or
    // from a test fixture. No game object is kept, so the transfer search can run on a worker thread.
    public class BodyNode
    {
        public string Name;
        public int Index;
        public int ParentIndex = -1; // -1 for the root (the star)

        public double Mu;               // m³/s²
        public double Radius;           // m
        public double Soi;              // m, +∞ for the root
        public double AtmosphereDepth;  // m, 0 without atmosphere
        public double MaxTerrainHeight; // m above Radius

        // State relative to the parent at EpochUT, in the inertial Zup frame shared by all bodies
        // (the root's celestial frame: see CelestialFrames).
        public Vector3d EpochPosition;
        public Vector3d EpochVelocity;
        public double EpochUT;

        // The rotation from this body's own celestial frame (where the game gives the Zup vectors of
        // the orbits around it) to the shared frame. Identity in the test fixtures.
        public QuaternionD FrameToCommon = QuaternionD.identity;

        // Default capture orbit: 10 km above whichever is higher, the atmosphere or the terrain.
        public double LowOrbitRadius => Radius + Math.Max(AtmosphereDepth, MaxTerrainHeight) + 10_000;
    }
}
