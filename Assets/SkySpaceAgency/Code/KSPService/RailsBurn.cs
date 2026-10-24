using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.KSPService
{
    /// <summary>
    /// Drives the game's rails warp for a long burn, as decided by RailsBurnPolicy, checks that
    /// the game's precomputed burn really runs, and reads the state from it.
    ///
    /// Game members used (Ksp2Redux.dll, Redux 26w42c, read with Cecil on 2026-10-25):
    /// - public: TimeWarp.ToggleTimeWarpMode(bool), SetRateIndex(int, bool) (clamped to
    ///   GetMaxRateIndex, true only when the index asked is granted), GetWarpRates(),
    ///   CurrentRateIndex, IsPhysicsTimeWarp; VesselComponent.IsOrbitalPhysicsUnderThrustActive,
    ///   Orbiter, GetEndBurnOrbit(), VesselDeltaV; OrbiterComponent.PatchedOrbit and
    ///   PatchedConicsOrbit; IOrbit.GetRelativePositionAtUTZup / GetOrbitalVelocityAtUTZup /
    ///   referenceBody; IPatchedOrbit.EndUT; VesselDeltaVComponent.TryGetOdeSolverStageComponents
    ///   with DeltaVExtensions.CurrentStage; OdeSolverStageComponent.MaxPossibleBurnTime.
    /// - private, by reflection (cached, a failure fails the guard): VesselComponent._numBurnPatches
    ///   (int), set to the number of solved burn patches by HandleOrbitalPhysicsUnderThrustStart and
    ///   to 0 by HandleOrbitalPhysicsUnderThrustEnd.
    ///
    /// Facts from the IL: HandleOrbitalPhysicsUnderThrustStart sets IsOrbitalPhysicsUnderThrustActive
    /// first, then returns early with no patch when the stage has no OdeSolverStageComponent (the
    /// silent case the guard is for); on success Orbiter.PatchedOrbit becomes PatchedNBodyOrbits[0]
    /// (a PatchedNBodyOrbit, interpolated from the ODE solution) instead of PatchedConicsOrbit,
    /// and OrbiterComponent.PublishOrbitState writes the burn state back into PatchedConicsOrbit
    /// each frame. Its last patch ends at the stage's burnout (StartUT + MaxPossibleBurnTime).
    /// With PhysicsSettings.DisableTimeWarpWhileUnderThrust, GetMaxRateIndexInternal allows only
    /// index 0 under thrust out of physics mode: SetRateIndex then refuses rails.
    /// </summary>
    public class RailsBurn
    {
        static TimeWarp Tw => GameManager.Instance?.Game?.ViewController?.TimeWarp;

        static FieldInfo num_burn_patches;
        static bool reflection_tried;

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
        public bool OnRails => Entered && TimeWarpTools.CurrentRateIndex > K2D2.Node.PhysicsWarpPolicy.MaxPhysicsIndex && !PhysicsMode;

        /// <summary>
        /// Switches physics mode off and asks the game for index (instant, so the vessel goes
        /// Orbital and the game solves its burn right away). True when the game granted that very
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
                EnteredIndex = index;
                FramesSinceEntry = 0;
                return true;
            }
            logger?.LogWarning($"{Prefix} refused by the game: asked index {index}, got index {TimeWarpTools.CurrentRateIndex} " +
                $"(physics mode {(PhysicsMode ? "on" : "off")}; the game's DisableTimeWarpWhileUnderThrust setting?): back to 1x");
            TimeWarpTools.SetRateIndex(0, true);
            return false;
        }

        /// <summary>Counts one real frame on rails.</summary>
        public void Tick()
        {
            if (Entered)
                FramesSinceEntry++;
        }

        /// <summary>
        /// Back to 1x (instant): the vessel goes RigidBody, the game ends its precomputed burn,
        /// and the physics warp takes over again on the next frame. Does nothing when not entered.
        /// </summary>
        public void Leave()
        {
            if (!Entered)
                return;
            Entered = false;
            try { TimeWarpTools.SetRateIndex(0, true); }
            catch (Exception e) { logger?.LogWarning($"{Prefix} leaving failed: {e.GetType().Name}: {e.Message}"); }
        }

        /// <summary>Forgets the state without touching the warp (a new burn, the warp already left).</summary>
        public void Reset()
        {
            Entered = false;
            FramesSinceEntry = 0;
        }

        static int? NumBurnPatches(VesselComponent v)
        {
            if (!reflection_tried)
            {
                reflection_tried = true;
                num_burn_patches = typeof(VesselComponent).GetField("_numBurnPatches", BindingFlags.Instance | BindingFlags.NonPublic);
                if (num_burn_patches == null || num_burn_patches.FieldType != typeof(int))
                    num_burn_patches = null;
            }
            if (num_burn_patches == null || v == null)
                return null;
            try { return (int)num_burn_patches.GetValue(v); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// The three checks: IsOrbitalPhysicsUnderThrustActive, Orbiter.PatchedOrbit is the burn
        /// trajectory (a PatchedNBodyOrbit, not PatchedConicsOrbit), and _numBurnPatches > 0.
        /// Anything unreadable fails. detail: the values, for the log.
        /// </summary>
        public bool GuardOk(out string detail)
        {
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
                IPatchedOrbit patched = orbiter.PatchedOrbit;
                bool trajectory = patched is PatchedNBodyOrbit && !ReferenceEquals(patched, orbiter.PatchedConicsOrbit);
                int? patches = NumBurnPatches(v);
                detail = $"under-thrust {(active ? "active" : "inactive")}, PatchedOrbit {(patched == null ? "null" : patched.GetType().Name)}" +
                    $"{(trajectory ? " (burn trajectory)" : "")}, burn patches {(patches.HasValue ? patches.Value.ToString() : "unreadable")}";
                return active && trajectory && patches.HasValue && patches.Value > 0;
            }
            catch (Exception e)
            {
                detail = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>
        /// State (Zup, relative to the reference body) at ut from Orbiter.PatchedOrbit, the burn
        /// trajectory while on rails. False when it is not a PatchedNBodyOrbit or ut is outside it.
        /// </summary>
        public bool TryReadState(double ut, out Vector3d r, out Vector3d v, out double mu)
        {
            r = v = Vector3d.zero;
            mu = double.NaN;
            try
            {
                OrbiterComponent orbiter = vessel?.Invoke()?.Orbiter;
                if (!(orbiter?.PatchedOrbit is PatchedNBodyOrbit burn) || ut < burn.StartUT || ut > burn.EndUT)
                    return false;
                r = burn.GetRelativePositionAtUTZup(ut);
                v = burn.GetOrbitalVelocityAtUTZup(ut);
                mu = burn.referenceBody.gravParameter;
                return !double.IsNaN(r.x + r.y + r.z + v.x + v.y + v.z) && mu > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>UT at which the game's precomputed burn ends (its last patch: the stage's burnout). NaN when none.</summary>
        public double BurnEndUT()
        {
            try
            {
                VesselComponent v = vessel?.Invoke();
                int? patches = NumBurnPatches(v);
                if (v == null || !(patches > 0))
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
        /// Game seconds the active stage can burn, as the game's precomputed burn would take it:
        /// the first OdeSolverStageComponent of VesselDeltaV's current stage, MaxPossibleBurnTime.
        /// 0 when the game has none (it would then start no burn at all).
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

        /// <summary>The reflection target could be bound (for the logs).</summary>
        public static string ReflectionStatus()
        {
            NumBurnPatches(null);
            return num_burn_patches != null ? "VesselComponent._numBurnPatches bound" : "VesselComponent._numBurnPatches NOT found";
        }
    }
}
