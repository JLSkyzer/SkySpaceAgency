using KTools;

namespace K2D2.Node
{
    /// <summary>
    /// The time warp a closed-loop burn (TargetOrbitBurn) may use while the engines push. The warp
    /// before the burn (waiting for the window) does not depend on it.
    /// - Off: 1x for the whole burn.
    /// - Physics: the game's physics warp, up to x4 (PhysicsWarpPolicy).
    /// - Rails: the game's native rails warp (x10 to x100) for the middle of a burn, like a native
    ///   maneuver node: ignition and the end of the burn at 1x in closed loop (RailsBurnPolicy).
    ///   The default since Redux 26w46a, where the game's rails burn follows the engines
    ///   (TryBuildTrackedEngineBurn, read in the IL on 2026-11-15). Before, it was a fixed burn
    ///   to the stage's burnout, and Off was the default.
    /// A settings file from before 26w46a may hold "PhysicsAndRails": it no longer parses and reads
    /// as the default (Rails).
    /// </summary>
    public enum BurnWarpMode { Off, Physics, Rails }

    public static class BurnWarpSettings
    {
        // Saved in this mod's main settings file; shown in the Node tab, ADVANCED, // WARP.
        public static EnumSetting<BurnWarpMode> mode = new EnumSetting<BurnWarpMode>("burn.warp_mode", BurnWarpModes.Default);
    }

    // What each mode allows. Pure, tested.
    public static class BurnWarpModes
    {
        public const BurnWarpMode Default = BurnWarpMode.Rails;

        public const string Labels = "Off (1x);Physics (x4);Rails (native)";

        /// <summary>Physics warp allowed while burning.</summary>
        public static bool AllowsPhysics(BurnWarpMode m) => m == BurnWarpMode.Physics;

        /// <summary>The game's rails warp allowed for the middle of a burn (1x around it).</summary>
        public static bool AllowsRails(BurnWarpMode m) => m == BurnWarpMode.Rails;

        public static string Describe(BurnWarpMode m) =>
            m == BurnWarpMode.Off ? "off (1x for the whole burn)"
            : m == BurnWarpMode.Physics ? "physics (up to x4)"
            : "rails (native warp for the middle of the burn, 1x at both ends)";
    }
}
