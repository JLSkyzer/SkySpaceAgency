using System;
using K2D2.Node;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using UnityEngine;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.KSPService
{
    /// <summary>
    /// Drives the game's physics time warp for a burn, as decided by PhysicsWarpPolicy, and gives
    /// the warp back as the player had it.
    ///
    /// Game members used (Ksp2Redux.dll, Redux 26w42c, all public): TimeWarp.ToggleTimeWarpMode(bool)
    /// writes _isPhysicsWarp; TimeWarp.IsPhysicsTimeWarp reads it; TimeWarp.SetRateIndex (through
    /// TimeWarpTools) keeps it while warping and clears it back at index 0 (instant, or once the
    /// ramp ends); VesselComponent.Physics (PhysicsMode.RigidBody = 3) of the burn's own vessel.
    /// The physics the game forces (atmosphere, near the surface) is re-derived by every
    /// SetRateIndex and never stored at index 0, so at 1x _isPhysicsWarp is only ever a toggle:
    /// the player's, or ours.
    ///
    /// One burn owns the warp at a time. Update must run every frame of the burn: a frame without
    /// it (the pilot stopped, the scene changed, the controllers paused for staging) makes
    /// ReleaseIfAbandoned, called by the plugin after the controllers, leave the warp.
    /// </summary>
    public class PhysicsWarp
    {
        static TimeWarp Tw => GameManager.Instance?.Game?.ViewController?.TimeWarp;

        static PhysicsWarp owner;
        static int owner_frame = -1;

        readonly ILogger logger;
        readonly Func<string> log_tag;
        readonly Func<VesselComponent> vessel;

        // The burn has taken the warp over (from its first Update to Leave).
        public bool Active { get; private set; }
        public bool PlayerModeWasPhysics { get; private set; }
        // Highest index applied by a rise, for the leaving log.
        public int HighestIndex { get; private set; }

        bool toggled_last_frame;
        bool refusal_logged;
        bool entered_logged;

        // vessel: the burn's own vessel (its RigidBody state is checked every frame).
        public PhysicsWarp(ILogger logger, Func<string> logTag, Func<VesselComponent> vessel)
        {
            this.vessel = vessel;
            this.logger = logger;
            log_tag = logTag ?? (() => "[PhysicsWarp]");
        }

        string Prefix => $"{log_tag()} physics warp:";

        public bool IsPhysicsMode
        {
            get
            {
                try { return Tw != null && Tw.IsPhysicsTimeWarp; }
                catch (Exception) { return false; }
            }
        }

        public bool VesselIsRigidBody
        {
            get
            {
                try
                {
                    VesselComponent v = vessel?.Invoke();
                    return v != null && v.Physics == PhysicsMode.RigidBody;
                }
                catch (Exception) { return false; }
            }
        }

        public int CurrentIndex => TimeWarpTools.CurrentRateIndex;

        // Physics mode is on whenever this raises the index (not gated: the engines push in a burn).
        public void SetIndex(int index, bool instant) => TimeWarpTools.SetRateIndexPhysics(index, instant);

        /// <summary>
        /// Switches physics-warp mode on, at index 0 only (the game's own toggle works only while
        /// not warping). True when the mode reads back on.
        /// </summary>
        public bool TryEnter()
        {
            TimeWarp tw = Tw;
            if (tw == null || CurrentIndex != 0)
                return false;
            try
            {
                tw.ToggleTimeWarpMode(true);
            }
            catch (Exception e)
            {
                logger?.LogWarning($"{Prefix} ToggleTimeWarpMode(true) failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
            return IsPhysicsMode;
        }

        void SetMode(bool physics)
        {
            try { Tw?.ToggleTimeWarpMode(physics); }
            catch (Exception e) { logger?.LogWarning($"{Prefix} ToggleTimeWarpMode({physics}) failed: {e.GetType().Name}: {e.Message}"); }
        }

        string State() =>
            $"index {CurrentIndex}, x{TimeWarpTools.CurrentRate:0.##}, physics mode {(IsPhysicsMode ? "on" : "off")}, " +
            $"vessel {(VesselIsRigidBody ? "RigidBody" : "not RigidBody")}";

        /// <summary>
        /// One frame of the burn: reads the game, asks PhysicsWarpPolicy.Decide, applies the mode
        /// change then the index (lowering instant). Takes the warp over on the first call.
        /// </summary>
        public WarpStep Update(double capRate, bool aligned, double burnLeftGameSeconds, bool allowRaise)
        {
            owner = this;
            owner_frame = Time.frameCount;
            if (!Active)
            {
                Active = true;
                // Only the player's own toggle, read at 1x before we touch it (above 1x the mode
                // may be physics the game forced).
                PlayerModeWasPhysics = PhysicsWarpPolicy.PlayerToggledPhysics(CurrentIndex, IsPhysicsMode);
                HighestIndex = 0;
                toggled_last_frame = false;
                refusal_logged = false;
                entered_logged = false;
            }

            var inputs = new WarpInputs
            {
                CurrentIndex = CurrentIndex,
                PhysicsModeOn = IsPhysicsMode,
                VesselRigidBody = VesselIsRigidBody,
                CapRate = capRate,
                Aligned = aligned,
                BurnLeftGameSeconds = burnLeftGameSeconds,
                AllowRaise = allowRaise,
                PlayerModeWasPhysics = PlayerModeWasPhysics,
            };

            if (toggled_last_frame && !inputs.PhysicsModeOn && inputs.CurrentIndex == 0 && !refusal_logged)
            {
                refusal_logged = true;
                logger?.LogWarning($"{Prefix} refused: the game turned physics mode off again, the burn stays at 1x ({State()})");
            }
            toggled_last_frame = false;

            WarpStep step = PhysicsWarpPolicy.Decide(inputs);

            if (step.SetPhysicsMode == true)
            {
                toggled_last_frame = true;
                bool on = TryEnter();
                if (!on && !refusal_logged)
                {
                    refusal_logged = true;
                    logger?.LogWarning($"{Prefix} refused: physics mode did not switch on, the burn stays at 1x ({State()})");
                }
                else if (on && !entered_logged)
                {
                    entered_logged = true;
                    logger?.LogInfo($"{Prefix} entering ({step.Reason}; player's mode {(PlayerModeWasPhysics ? "physics" : "normal")}; {State()})");
                }
            }
            else if (step.SetPhysicsMode == false)
                SetMode(false);

            if (step.WantedIndex != inputs.CurrentIndex)
            {
                bool lowering = step.WantedIndex < inputs.CurrentIndex;
                SetIndex(step.WantedIndex, lowering || step.ForcedDrop);
                if (step.ForcedDrop)
                    logger?.LogWarning($"{Prefix} forced drop to 1x: {step.Reason} (was index {inputs.CurrentIndex}, " +
                        $"physics mode {(inputs.PhysicsModeOn ? "on" : "off")}, vessel {(inputs.VesselRigidBody ? "RigidBody" : "not RigidBody")}; now {State()})");
                else if (!lowering && step.WantedIndex > HighestIndex)
                {
                    HighestIndex = step.WantedIndex;
                    logger?.LogInfo($"{Prefix} raised to index {step.WantedIndex} (x{PhysicsWarpPolicy.RateOf(step.WantedIndex):0}): {step.Reason}; " +
                        $"burn left {burnLeftGameSeconds:n1} s; {State()}");
                }
            }
            else if (step.ForcedDrop)
            {
                // Already at the drop index (cannot happen above 0, kept for safety).
                logger?.LogWarning($"{Prefix} forced drop: {step.Reason} ({State()})");
            }
            return step;
        }

        /// <summary>
        /// A frame of the burn on rails warp (RailsBurn), where Decide must not run (it drops any
        /// rails index): the burn still owns the warp, so ReleaseIfAbandoned leaves it (index 0,
        /// which also ends the rails warp) when the burn stops being updated.
        /// </summary>
        public void KeepAlive()
        {
            if (!Active)
                return;
            owner = this;
            owner_frame = Time.frameCount;
        }

        /// <summary>
        /// Gives the warp back: index 0 first (instant), then the mode the player had before the
        /// burn (its own toggle at 1x, otherwise off). With
        /// restorePlayerMode false the mode is only switched off. Does nothing when the warp was
        /// not taken over.
        /// </summary>
        public void Leave(bool restorePlayerMode)
        {
            if (!Active)
                return;
            Active = false;
            if (owner == this)
                owner = null;
            try
            {
                if (Tw == null)
                {
                    logger?.LogInfo($"{Prefix} leaving: no time warp object (scene change)");
                    return;
                }
                for (int pass = 0; pass < 3; pass++)
                {
                    var inputs = new WarpInputs
                    {
                        CurrentIndex = CurrentIndex,
                        PhysicsModeOn = IsPhysicsMode,
                        PlayerModeWasPhysics = restorePlayerMode && PlayerModeWasPhysics,
                    };
                    WarpStep step = PhysicsWarpPolicy.DecideLeave(inputs);
                    if (step.WantedIndex != inputs.CurrentIndex)
                    {
                        SetIndex(step.WantedIndex, true);
                        continue;
                    }
                    if (step.SetPhysicsMode.HasValue)
                    {
                        SetMode(step.SetPhysicsMode.Value);
                        continue;
                    }
                    break;
                }
                logger?.LogInfo($"{Prefix} leaving: highest index {HighestIndex} (x{PhysicsWarpPolicy.RateOf(HighestIndex):0}), " +
                    $"player's mode {(PlayerModeWasPhysics ? "physics" : "normal")}{(restorePlayerMode ? " restored" : "")}; {State()}");
            }
            catch (Exception e)
            {
                logger?.LogWarning($"{Prefix} leaving failed: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Called by the plugin every frame after the controllers: a burn that owns the warp but
        /// was not updated this frame was stopped from outside (pilot stopped, failed, scene or
        /// vessel change, staging pause): its warp is left and the player's mode restored.
        /// </summary>
        public static void ReleaseIfAbandoned()
        {
            PhysicsWarp o = owner;
            if (o == null || owner_frame == Time.frameCount)
                return;
            if (o.Active)
            {
                o.logger?.LogInfo($"{o.Prefix} the burn was not updated this frame (stopped, failed, scene change or staging pause): leaving");
                o.Leave(true);
            }
            owner = null;
        }
    }
}
