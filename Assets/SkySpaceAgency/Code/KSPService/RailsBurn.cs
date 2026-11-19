using System;
using System.Collections.Generic;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace SkySpaceAgency.KSPService
{
    /// <summary>
    /// Drives the game's native rails warp during a burn, as decided by RailsBurnPolicy, checks
    /// that the game flies the burn, and reads the state from it.
    ///
    /// Game members used (all public, in the 26w42c imports the mod builds against and in the
    /// 26w46a game): TimeWarp.ToggleTimeWarpMode(bool), SetRateIndex(int, bool) (clamped to
    /// GetMaxRateIndex, true only when the index asked is granted), GetWarpRates(),
    /// CurrentRateIndex, IsPhysicsTimeWarp; VesselComponent.IsOrbitalPhysicsUnderThrustActive,
    /// Orbiter, GetEndBurnOrbit(), VesselDeltaV; OrbiterComponent.PatchedOrbit;
    /// IOrbit.GetRelativePositionAtUTZup / GetOrbitalVelocityAtUTZup / referenceBody;
    /// IPatchedOrbit.StartUT / EndUT; VesselDeltaVComponent.TryGetOdeSolverStageComponents with
    /// DeltaVExtensions.CurrentStage; OdeSolverStageComponent.MaxPossibleBurnTime.
    ///
    /// Facts from the 26w46a IL (2026-11-15): HandleOrbitalPhysicsUnderThrustStart sets
    /// IsOrbitalPhysicsUnderThrustActive only once it has built a burn from the lit engines, so
    /// that flag alone is the guard (in 26w42c it turned true even when nothing was built, hence
    /// the old three-part guard and the reflection on _numBurnPatches). Orbiter.PatchedOrbit is the
    /// conic for at most two startup updates, then the burn's PatchedNBodyOrbit; the flag goes back
    /// to false when no engine pushes or asks to, when the propellant runs out, or when the burn's
    /// trajectory does not start on the conic. GetEndBurnOrbit() is the burn's last patch, which
    /// ends when the lit engines' propellant does. With
    /// PhysicsSettings.DisableTimeWarpWhileUnderThrust, GetMaxRateIndexInternal allows only index 0
    /// under thrust out of physics mode: SetRateIndex then refuses rails.
    /// </summary>
    public class RailsBurn
    {
        static TimeWarp Tw => GameManager.Instance?.Game?.ViewController?.TimeWarp;

        readonly ILogger logger;
        readonly Func<string> log_tag;
        readonly Func<VesselComponent> vessel;

        // TryEnter succeeded and Leave was not called yet.
        public bool Entered { get; private set; }
        // Index set by TryEnter.
        public int EnteredIndex { get; private set; }
        // Real frames since TryEnter (call Tick once per frame while Entered).
        public int FramesSinceEntry { get; private set; }

        public RailsBurn(ILogger logger, Func<string> logTag, Func<VesselComponent> vessel)
        {
            this.logger = logger;
            this.vessel = vessel;
            log_tag = logTag ?? (() => "[RailsBurn]");
        }

        string Prefix => $"{log_tag()} rails burn:";

        public static float[] Rates
        {
            get
            {
                try
                {
                    var levels = Tw?.GetWarpRates();
                    if (levels == null)
                        return null;
                    var rates = new float[levels.Length];
                    for (int k = 0; k < levels.Length; k++)
                        rates[k] = levels[k].TimeScaleFactor;
                    return rates;
                }
                catch (Exception) { return null; }
            }
        }

        static bool PhysicsMode
        {
            get
            {
                try { return Tw != null && Tw.IsPhysicsTimeWarp; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>The warp is still on rails: entered, above the physics range, physics mode off.</summary>
        public bool OnRails => Entered && TimeWarpTools.CurrentRateIndex > SkySpaceAgency.Node.PhysicsWarpPolicy.MaxPhysicsIndex && !PhysicsMode;

        /// <summary>
        /// Switches physics mode off and asks the game for index (instant, so the vessel goes
        /// Orbital and the game builds its burn right away). True when the game granted that very
        /// index with physics mode off. On a refusal the warp goes back to 1x at once (an index
        /// clamped into the physics range without physics mode would be rails warp too).
        /// </summary>
        public bool TryEnter(int index)
        {
            TimeWarp tw = Tw;
            if (tw == null || Entered)
                return false;
            bool granted = false;
            try
            {
                tw.ToggleTimeWarpMode(false);
                granted = tw.SetRateIndex(index, true);
            }
            catch (Exception e)
            {
                logger?.LogWarning($"{Prefix} entering failed: {e.GetType().Name}: {e.Message}");
            }
            if (granted && tw.CurrentRateIndex == index && !PhysicsMode)
            {
                Entered = true;
                WarpGate.RailsBurnAsked = true;
                EnteredIndex = index;
                FramesSinceEntry = 0;
                return true;
            }
            RefusedBySetting = DisableTimeWarpWhileUnderThrust;
            if (RefusedBySetting)
                logger?.LogWarning($"{Prefix} refused: the game's DisableTimeWarpWhileUnderThrust setting is on, so it allows no " +
                    "warp under thrust out of physics mode; the burn stays at 1x");
            else
                logger?.LogWarning($"{Prefix} refused by the game: asked index {index}, got index {TimeWarpTools.CurrentRateIndex} " +
                    $"(physics mode {(PhysicsMode ? "on" : "off")}): back to 1x");
            TimeWarpTools.SetRateIndex(0, true);
            return false;
        }

        // The last TryEnter was refused because of PhysicsSettings.DisableTimeWarpWhileUnderThrust
        // (then no other rails index can be granted either).
        public bool RefusedBySetting { get; private set; }

        // PhysicsSettings.DisableTimeWarpWhileUnderThrust (public static getter, IL: ldsfld
        // _physicsSettings). Unreadable counts as off.
        static bool DisableTimeWarpWhileUnderThrust
        {
            get
            {
                try { return PhysicsSettings.DisableTimeWarpWhileUnderThrust; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>
        /// Lowers the rails rate to index (instant; still rails, the game's burn goes on). True
        /// when the game applied it.
        /// </summary>
        public bool StepDown(int index)
        {
            TimeWarp tw = Tw;
            if (tw == null || !Entered || index <= SkySpaceAgency.Node.PhysicsWarpPolicy.MaxPhysicsIndex)
                return false;
            try { tw.SetRateIndex(index, true); }
            catch (Exception e) { logger?.LogWarning($"{Prefix} step down failed: {e.GetType().Name}: {e.Message}"); }
            return TimeWarpTools.CurrentRateIndex == index;
        }

        /// <summary>Counts one real frame on rails.</summary>
        public void Tick()
        {
            if (Entered)
                FramesSinceEntry++;
        }

        /// <summary>
        /// Back to 1x (instant): the vessel goes RigidBody, the game ends its burn, and the closed
        /// loop steers again on the next frame. Does nothing when not entered.
        /// </summary>
        public void Leave()
        {
            if (!Entered)
                return;
            Entered = false;
            WarpGate.RailsBurnAsked = false;
            try { TimeWarpTools.SetRateIndex(0, true); }
            catch (Exception e) { logger?.LogWarning($"{Prefix} leaving failed: {e.GetType().Name}: {e.Message}"); }
        }

        /// <summary>Forgets the state without touching the warp (a new burn, the warp already left).</summary>
        public void Reset()
        {
            Entered = false;
            WarpGate.RailsBurnAsked = false;
            FramesSinceEntry = 0;
            RefusedBySetting = false;
        }

        /// <summary>
        /// The guard: the game flies the burn (IsOrbitalPhysicsUnderThrustActive, true in 26w46a
        /// only once it built a burn from the lit engines). pending: not yet, the only failure that
        /// may wait a few frames. Unreadable fails. detail: the values, for the log.
        /// </summary>
        public bool GuardOk(out string detail, out bool pending)
        {
            pending = false;
            try
            {
                VesselComponent v = vessel?.Invoke();
                OrbiterComponent orbiter = v?.Orbiter;
                if (orbiter == null)
                {
                    detail = "no vessel or orbiter";
                    return false;
                }
                bool active = v.IsOrbitalPhysicsUnderThrustActive;
                pending = !active;
                IPatchedOrbit patched = orbiter.PatchedOrbit;
                detail = $"under-thrust {(active ? "active" : "inactive")}, PatchedOrbit {(patched == null ? "null" : patched.GetType().Name)}";
                return active;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>
        /// State (Zup, relative to the reference body) at ut from Orbiter.PatchedOrbit: the burn's
        /// trajectory while the game flies it, the conic otherwise (its startup updates, or a game
        /// that does not take the burn, which the measured acceleration then shows). False when ut
        /// is outside it or a value is not finite.
        /// </summary>
        public bool TryReadState(double ut, out Vector3d r, out Vector3d v, out double mu)
        {
            r = v = Vector3d.zero;
            mu = double.NaN;
            try
            {
                IPatchedOrbit orbit = vessel?.Invoke()?.Orbiter?.PatchedOrbit;
                if (orbit == null || ut < orbit.StartUT || ut > orbit.EndUT)
                    return false;
                r = orbit.GetRelativePositionAtUTZup(ut);
                v = orbit.GetOrbitalVelocityAtUTZup(ut);
                mu = orbit.referenceBody.gravParameter;
                return !double.IsNaN(r.x + r.y + r.z + v.x + v.y + v.z) && mu > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>UT at which the game's burn ends (its last patch: the lit engines' propellant). NaN when it flies none.</summary>
        public double BurnEndUT()
        {
            try
            {
                VesselComponent v = vessel?.Invoke();
                if (v == null || !v.IsOrbitalPhysicsUnderThrustActive)
                    return double.NaN;
                IPatchedOrbit end = v.GetEndBurnOrbit();
                return end != null ? end.EndUT : double.NaN;
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        /// <summary>
        /// Game seconds the active stage can burn: the first OdeSolverStageComponent of
        /// VesselDeltaV's current stage, MaxPossibleBurnTime. 0 when the game has none.
        /// </summary>
        public double StageBurnSeconds()
        {
            try
            {
                VesselDeltaVComponent dv = vessel?.Invoke()?.VesselDeltaV;
                if (dv == null)
                    return 0;
                List<OdeSolverStageComponent> stages = dv.TryGetOdeSolverStageComponents(DeltaVExtensions.CurrentStage(dv));
                if (stages == null || stages.Count == 0)
                    return 0;
                double t = stages[0].MaxPossibleBurnTime;
                return t > 0 ? t : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Maneuver nodes on the vessel (ManeuverPlanComponent.GetNodes), -1 when unreadable, for
        /// the entry log. The mod's pilots change nodes only once the burn is finished (rails
        /// already left).
        /// </summary>
        public int ManeuverNodeCount()
        {
            try
            {
                var plan = vessel?.Invoke()?.SimulationObject?.FindComponent<ManeuverPlanComponent>();
                return plan?.GetNodes()?.Count ?? 0;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
