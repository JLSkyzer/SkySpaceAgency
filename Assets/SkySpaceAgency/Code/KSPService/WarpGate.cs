using System;
using KSP.Game;
using KSP.Modules;
using KSP.Sim.impl;
using UnityEngine;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2
{
    /// <summary>
    /// When rails warp may start, and the decision of the watchdog. Pure, tested.
    ///
    /// Root cause of the 2026-10-25 transfer drift (log Player-2026-10-25b.log, 14535-14552): rails
    /// warp was asked right after a burn ended, while an engine still produced force (throttle
    /// response, spool-down). The game then ran HandleOrbitalPhysicsUnderThrustStart and flew a
    /// precomputed burn with that flow under warp: +8.5 m/s nobody commanded, until it cut the warp.
    /// </summary>
    public sealed class EngineQuietTimer
    {
        public const double MinSeconds = 1;
        public const int MinFrames = 10;

        double since = double.NaN;
        int frames;

        public void Reset()
        {
            since = double.NaN;
            frames = 0;
        }

        /// <summary>
        /// Once per frame: quiet = throttle 0 and no engine force. True once quiet has held for at
        /// least MinSeconds of real time and MinFrames frames; any push restarts the wait.
        /// </summary>
        public bool Update(bool quiet, double realTime)
        {
            if (!quiet)
            {
                Reset();
                return false;
            }
            if (double.IsNaN(since) || realTime < since)
                since = realTime;
            frames++;
            return Quiet(realTime);
        }

        public bool Quiet(double realTime) =>
            !double.IsNaN(since) && frames >= MinFrames && realTime - since >= MinSeconds;
    }

    public static class WarpGatePolicy
    {
        /// <summary>
        /// The warp index actually allowed: lowering is always allowed, physics warp too; rails warp
        /// (any index above 0 without physics mode: the vessel cannot turn there either) only once
        /// the engines have been quiet. Held at 1x otherwise.
        /// </summary>
        public static int AllowedIndex(int wanted, int current, bool physicsMode, bool enginesQuiet)
        {
            if (wanted <= 0 || wanted <= current || physicsMode || enginesQuiet)
                return wanted;
            return 0;
        }

        /// <summary>
        /// The game flies a burn under rails warp (orbital physics under thrust) that no rails burn of
        /// ours asked for: drop to 1x.
        /// </summary>
        public static bool DropUnaskedRailsBurn(int index, bool physicsMode, bool underThrustOnRails, bool railsBurnAsked) =>
            index > 0 && !physicsMode && underThrustOnRails && !railsBurnAsked;
    }

    /// <summary>
    /// Game glue: watches the active vessel's throttle and engine forces every frame (K2D2_Plugin.Update)
    /// and gates every rails warp the mod asks for (TimeWarpTools.SetRateIndex). Also the watchdog
    /// against a burn the game starts under rails warp on its own.
    ///
    /// Engine force: as VesselComponent.IsUnderEngineThrust does in the IL, any part's rigidbody force
    /// that is a Data_Engine.EngineForce with a non-zero RelativeForce.
    /// </summary>
    public static class WarpGate
    {
        static readonly ILogger log = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Warp");
        static readonly EngineQuietTimer timer = new EngineQuietTimer();
        static int last_frame = -1;
        static int forces;
        static float throttle;
        static float next_wait_log;

        /// <summary>Set by RailsBurn while it flies a rails burn it asked for.</summary>
        public static bool RailsBurnAsked { get; set; }

        static TimeWarp Tw => GameManager.Instance?.Game?.ViewController?.TimeWarp;

        static VesselComponent Vessel()
        {
            try
            {
                return K2D2_Plugin.Instance?.current_vessel?.GetActiveSimVessel();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Once per frame: the engine state, then the watchdog.</summary>
        public static void Tick()
        {
            if (last_frame == Time.frameCount)
                return;
            last_frame = Time.frameCount;
            var vessel = Vessel();
            if (vessel == null)
            {
                timer.Reset();
                return;
            }
            ReadEngines(vessel, out throttle, out forces);
            timer.Update(throttle <= 0 && forces == 0, Time.realtimeSinceStartupAsDouble);

            try
            {
                var tw = Tw;
                if (tw == null)
                    return;
                bool physics = tw.IsPhysicsTimeWarp;
                if (WarpGatePolicy.DropUnaskedRailsBurn(tw.CurrentRateIndex, physics, vessel.IsOrbitalPhysicsUnderThrustActive, RailsBurnAsked))
                {
                    tw.SetRateIndex(0, true);
                    log.LogWarning($"[Warp] the game started a burn under rails warp that we did not ask for: dropped (throttle {throttle:0.##}, {forces} engine force(s))");
                }
            }
            catch (Exception)
            {
                // No time warp object (scene change).
            }
        }

        static void ReadEngines(VesselComponent vessel, out float mainThrottle, out int count)
        {
            mainThrottle = 0;
            count = 0;
            try
            {
                mainThrottle = vessel.flightCtrlState.mainThrottle;
                var parts = vessel.SimulationObject?.PartOwner?.Parts;
                if (parts == null)
                    return;
                foreach (var part in parts)
                {
                    var rb = part?.SimulationObject?.Rigidbody;
                    if (rb?.Forces == null)
                        continue;
                    foreach (var f in rb.Forces)
                        if (f is Data_Engine.EngineForce ef && !ef.RelativeForce.IsZero())
                            count++;
                }
            }
            catch (Exception)
            {
                // Parts not readable this frame: counted as quiet only if the throttle is 0.
            }
        }

        /// <summary>The index allowed for a request (lowering always; rails only with quiet engines).</summary>
        public static int Allowed(int wanted, int current, bool physicsMode)
        {
            Tick();
            int allowed = WarpGatePolicy.AllowedIndex(wanted, current, physicsMode, timer.Quiet(Time.realtimeSinceStartupAsDouble));
            if (allowed != wanted && Time.realtimeSinceStartup >= next_wait_log)
            {
                next_wait_log = Time.realtimeSinceStartup + 5;
                log.LogInfo($"[Warp] waiting for the engines to stop before rails warp (throttle {throttle:0.##}, {forces} force(s)); held at 1x instead of index {wanted}");
            }
            return allowed;
        }
    }
}
