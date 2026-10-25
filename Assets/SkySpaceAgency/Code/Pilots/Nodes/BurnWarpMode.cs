using KTools;

namespace K2D2.Node
{
    /// <summary>
    /// The time warp a closed-loop burn (TargetOrbitBurn) may use while the engines push. The warp
    /// before the burn (waiting for the window) does not depend on it.
    /// - Off: 1x for the whole burn (the default since 2026-10-25: after physics-warped burns
    ///   followed by rails warp, the trajectory of the in-game Kerbin -> Duna test jumped).
    /// - Physics: the game's physics warp, up to x4 (PhysicsWarpPolicy).
    /// - PhysicsAndRails: physics warp, and rails warp (x10, x50) for the long middle of a burn
    ///   (RailsBurnPolicy).
    /// </summary>
    public enum BurnWarpMode { Off, Physics, PhysicsAndRails }

    public static class BurnWarpSettings
    {
        // Saved in skyspaceagency_settings.json; shown in the Node tab, ADVANCED, // WARP.
        public static EnumSetting<BurnWarpMode> mode = new EnumSetting<BurnWarpMode>("burn.warp_mode", BurnWarpMode.Off);
    }

    // What each mode allows. Pure, tested.
    public static class BurnWarpModes
    {
        public const string Labels = "Off (1x);Physics (x4);Physics + rails";

        /// <summary>Physics warp allowed while burning.</summary>
        public static bool AllowsPhysics(BurnWarpMode m) => m == BurnWarpMode.Physics || m == BurnWarpMode.PhysicsAndRails;

        /// <summary>Rails warp allowed for the long middle of a burn.</summary>
        public static bool AllowsRails(BurnWarpMode m) => m == BurnWarpMode.PhysicsAndRails;

        public static string Describe(BurnWarpMode m) =>
            m == BurnWarpMode.Off ? "off (1x for the whole burn)"
            : m == BurnWarpMode.Physics ? "physics (up to x4)"
            : "physics + rails for long burns";
    }
}
