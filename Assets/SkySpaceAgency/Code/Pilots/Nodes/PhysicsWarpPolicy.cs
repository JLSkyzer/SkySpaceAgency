using System;

namespace K2D2.Node
{
    /// <summary>What the game reports and what the burn allows, read once per frame.</summary>
    public struct WarpInputs
    {
        // TimeWarp.CurrentRateIndex (0 = 1x).
        public int CurrentIndex;
        // TimeWarp.IsPhysicsTimeWarp.
        public bool PhysicsModeOn;
        // VesselComponent.Physics == PhysicsMode.RigidBody.
        public bool VesselRigidBody;
        // Highest warp rate the burn allows (BurnWarp.MaxBurnRate, or 1).
        public double CapRate;
        // Within the burn's thrust angle.
        public bool Aligned;
        // Seconds of game time left at full thrust (remaining Δv / acceleration).
        public double BurnLeftGameSeconds;
        // allow_warp: the mod may raise the warp. False: it only lowers the player's.
        public bool AllowRaise;
        // TimeWarp.IsPhysicsTimeWarp when the burn took the warp over.
        public bool PlayerModeWasPhysics;
        // The game would force physics warp by itself here (atmosphere, near the surface, see
        // TimeWarp.GetMaxRateIndexInternal). Only used on leaving.
        public bool GameForcesPhysics;
    }

    /// <summary>The warp to apply this frame.</summary>
    public struct WarpStep
    {
        // Warp index wanted (lowering is instant, raising goes through the game's ramp).
        public int WantedIndex;
        // true: call ToggleTimeWarpMode(true); false: ToggleTimeWarpMode(false); null: no change.
        // Applied before WantedIndex.
        public bool? SetPhysicsMode;
        public string Reason;
        // A safety drop to index 0 (rails warp, physics mode off, vessel off RigidBody): instant
        // and logged as such.
        public bool ForcedDrop;
    }

    /// <summary>
    /// Physics time warp while the engines push. Pure, tested in the EditMode tests
    /// (PhysicsWarpPolicyTests).
    ///
    /// In the IL (Redux 26w42c): TimeWarp.ToggleTimeWarpMode(true) at index 0 makes indices 1 and 2
    /// physics warp (x2, x4, MAX_PHYSICS_WARP_RATE_INDEX = 2), the vessel stays RigidBody and our
    /// steering and throttle keep working. Without that mode, indices above 0 in vacuum are rails
    /// warp, where the game replaces thrust by a fixed burn we cannot steer. Back at index 0
    /// (SetRateIndex instant, or the ramp completing) the game clears the mode, so every rise from
    /// 1x goes through the mode again: on at index 0 on one frame, the rise on the next frame once
    /// the mode reads back on.
    /// </summary>
    public static class PhysicsWarpPolicy
    {
        // TimeWarp.MAX_PHYSICS_WARP_RATE_INDEX.
        public const int MaxPhysicsIndex = 2;
        // Rates of the physics warp indices (the game's first three warp levels).
        static readonly double[] PhysicsRates = { 1, 2, 4 };
        // Under this much game time of burn left, x2 at most.
        public const double X4MinBurnLeft = 30;

        public static double RateOf(int index) =>
            index >= 0 && index < PhysicsRates.Length ? PhysicsRates[index] : double.NaN;

        /// <summary>
        /// Warp for this frame while burning. Safety first: an index above 2, an index above 0
        /// without physics mode (both rails warp) or a vessel no longer RigidBody drop to index 0
        /// (ForcedDrop). Otherwise the target is the highest physics index under CapRate, 1x when
        /// not aligned, x2 at most under X4MinBurnLeft s of burn. Above the target: lowered. Below:
        /// raised only with AllowRaise, after switching physics mode on at index 0.
        /// </summary>
        public static WarpStep Decide(WarpInputs i)
        {
            int current = Math.Max(0, i.CurrentIndex);
            if (current > MaxPhysicsIndex)
                return Drop($"rails index {current} (physics warp stops at {MaxPhysicsIndex})");
            if (current > 0 && !i.PhysicsModeOn)
                return Drop($"physics mode off at index {current} (rails warp)");
            if (!i.VesselRigidBody)
            {
                if (current > 0)
                    return Drop("vessel no longer RigidBody");
                return Hold(0, "vessel not RigidBody");
            }

            string why;
            int target = Target(i, out why);

            if (target < current)
                return Hold(target, why);
            if (target == current || !i.AllowRaise)
                return Hold(current, target == current ? why : "allow_warp off: the player's warp is kept");

            // Raise: physics mode first, at index 0; the rise waits for it to read back on.
            if (!i.PhysicsModeOn)
                return new WarpStep { WantedIndex = 0, SetPhysicsMode = true, Reason = "entering physics mode at 1x" };
            return Hold(target, why);
        }

        /// <summary>
        /// Warp to leave the burn with: index 0 first (mode untouched); at index 0, the mode the
        /// player had before the burn (off, unless the game forces physics here; on if the player
        /// had it on). Call again until it asks nothing.
        /// </summary>
        public static WarpStep DecideLeave(WarpInputs i)
        {
            if (i.CurrentIndex != 0)
                return new WarpStep { WantedIndex = 0, Reason = "leaving: back to 1x" };
            if (i.PlayerModeWasPhysics)
                return new WarpStep { WantedIndex = 0, SetPhysicsMode = i.PhysicsModeOn ? (bool?)null : true,
                    Reason = "leaving: the player's physics mode" };
            if (i.PhysicsModeOn && !i.GameForcesPhysics)
                return new WarpStep { WantedIndex = 0, SetPhysicsMode = false, Reason = "leaving: physics mode off" };
            return new WarpStep { WantedIndex = 0, Reason = i.PhysicsModeOn ? "leaving: physics owned by the game" : "leaving: done" };
        }

        // Highest physics index allowed by the cap, the alignment and the burn time left.
        static int Target(WarpInputs i, out string why)
        {
            if (!i.Aligned)
            {
                why = "not aligned";
                return 0;
            }
            int index = 0;
            for (int k = 1; k <= MaxPhysicsIndex; k++)
                if (PhysicsRates[k] <= i.CapRate)
                    index = k;
            why = $"cap x{i.CapRate:0.##}";
            if (index > 1 && !(i.BurnLeftGameSeconds >= X4MinBurnLeft))
            {
                index = 1;
                why = $"{i.BurnLeftGameSeconds:n1} s of burn left";
            }
            return index;
        }

        static WarpStep Drop(string reason) =>
            new WarpStep { WantedIndex = 0, Reason = reason, ForcedDrop = true };

        static WarpStep Hold(int index, string reason) =>
            new WarpStep { WantedIndex = index, Reason = reason };
    }
}
