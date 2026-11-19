using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SkySpaceAgency.KSPService;
using SkySpaceAgency.Landing.Braking;
using SkySpaceAgency.OrbitPlanning;
using SkySpaceAgency.Rendezvous;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace SkySpaceAgency.Lift
{
    // Rendezvous goal (LiftGoal.Rendezvous, sub-project 4.4), before the ascent: reads the site, the
    // target and the body on the main thread, then RendezvousLaunch.Plan on a worker thread scores the
    // next launch passes. The wait and the launch are the transfer goal's (WaitLaunch.Update).
    //
    // Frames: the site and the target are read from orbits around the launch body, in that body's
    // celestial frame (GetRelativePositionAtUTZup), and are never converted (CelestialFrames, 2026-11-18).
    // Only scalars come from the BodyNode.
    public partial class WaitLaunch
    {
        // No pass in the first minute: the plan takes seconds of game time (logged).
        public const double RendezvousStartDelaySeconds = 60;

        Task<RendezvousLaunchPlan> rv_job;

        // previous: the plan of a missed pass (its staging state and counters are carried over), or null.
        void BeginRendezvousPlanning(LiftTransferPlan previous)
        {
            Cancel();
            // The passes are planned for seconds of game time: at 1x, so that none slips by
            // meanwhile (the first plan too, not only the re-plans).
            int warpIndex = TimeWarpTools.CurrentRateIndex;
            if (warpIndex != 0)
            {
                logger.LogInfo($"[Lift] rendezvous: warp set to 1x while the passes are planned (was index {warpIndex})");
                TimeWarpTools.SetRateIndex(0, false);
            }
            VesselComponent vessel = current_vessel?.VesselComponent;
            if (vessel == null)
            {
                refusal = "No vessel";
                return;
            }
            if (!vessel.LandedOrSplashed)
            {
                refusal = "Rendezvous starts on the launch pad: the vessel is not landed.";
                return;
            }

            IKeplerPatch orbit = vessel.Orbit;
            CelestialBodyComponent cb = orbit?.referenceBody;
            if (cb == null)
            {
                refusal = "Cannot plan: no body under the vessel.";
                return;
            }
            var list = GameBodySystem.GameBodies();
            double now = GeneralTools.Current_UT;
            BodySystem system = GameBodySystem.Build(list, now, out string error);
            int bodyIndex = GameBodySystem.IndexOf(list, cb);
            if (system == null || bodyIndex < 0)
            {
                refusal = "Cannot plan: " + (error ?? "the launch body is not in the game's body list.");
                return;
            }
            BodyNode node = system[bodyIndex];
            double clear = node.Radius + Math.Max(node.AtmosphereDepth, node.MaxTerrainHeight);

            string id = settings.rendezvous_target.V;
            VesselComponent target = string.IsNullOrEmpty(id) ? null : RendezvousSnapshot.FindVessel(id);
            bool isOurs = target != null && target.GlobalId.Equals(vessel.GlobalId);
            IKeplerPatch targetOrbit = target?.Orbit;
            bool exists = target != null && !isOurs && targetOrbit != null;
            Vector3d rT = Vector3d.zero, vT = Vector3d.zero;
            var elT = new OrbitElements();
            string targetBody = targetOrbit?.referenceBody?.bodyName;
            bool sameBody = exists && ReferenceEquals(targetOrbit.referenceBody, cb);
            if (sameBody)
            {
                rT = targetOrbit.GetRelativePositionAtUTZup(now);
                vT = targetOrbit.GetOrbitalVelocityAtUTZup(now);
                elT = OrbitMath.Elements(rT, vT, node.Mu);
            }
            string why = RendezvousLaunch.TargetRefusal(exists, exists && target.LandedOrSplashed,
                sameBody ? node.Name : targetBody ?? "another body", node.Name, elT, clear, node.Radius);
            if (why != null)
            {
                refusal = why;
                refusal_is_plan = true;
                logger.LogInfo($"[Lift] rendezvous refused: {why}");
                return;
            }

            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            double w = BodyRotation.AngularSpeed(cb.rotationPeriod);
            double surfaceSpeed = current_vessel.VesselVehicle.SurfaceSpeed;
            int sign = BodyRotation.ChooseSign(r, v, w, surfaceSpeed);
            bool decisive = BodyRotation.IsDecisive(r, v, w);
            Vector3d omega = BodyRotation.AngularVelocity(w, sign);

            var stored = settings.MeasuredLead(node.Name, lift.run_profile);
            LiftGoalRules.AscentLead(stored.s.V, stored.deg.V, out double lead, out double downrange, out bool measured);

            // Pre-selects the target in the Rendezvous tab; an action on our vessel only.
            RendezvousSnapshot.SetTarget(vessel, target.GlobalId, "the rendezvous target");

            var p = new LiftTransferPlan
            {
                Goal = LiftGoal.Rendezvous,
                TargetName = target.Name,
                TargetId = id,
                Dock = settings.rendezvous_dock.V,
                BodyName = node.Name,
                BodyRadius = node.Radius,
                Mu = node.Mu,
                Omega = omega,
                LeadSeconds = lead,
                DownrangeRad = downrange,
                LeadMeasured = measured,
            };
            if (previous != null)
            {
                // The mod fired the first stage on an earlier pass: never a second one.
                p.Staged = previous.Staged;
                p.Resolves = previous.Resolves + 1;
                p.PlannedLaunchUT = previous.PlannedLaunchUT;
            }
            lift.transfer = p;
            lift.ascent_guided.transfer_plan = p;

            double lat = Math.Asin(Math.Max(-1, Math.Min(1, r.z / r.magnitude))) * 180 / Math.PI;
            double parkingPreview = RendezvousLaunch.ParkingRadius(elT.PeriapsisRadius, node.LowOrbitRadius, node.Radius, out string parkingWhy);
            logger.LogInfo(Inv($"[Lift] rendezvous: planning with {target.Name} ({id}) from {node.Name}: target Pe {(elT.PeriapsisRadius - node.Radius) / 1000:F1} km, ") +
                Inv($"Ap {(elT.ApoapsisRadius - node.Radius) / 1000:F1} km, inclination {elT.InclinationRad * 180 / Math.PI:F3}° (game orbit.inclination {targetOrbit.inclination}), ") +
                Inv($"parking {(parkingPreview - node.Radius) / 1000:F0} km ({parkingWhy}); player's Ap {settings.destination_Ap_km.V} km ignored; ") +
                Inv($"site latitude {lat:F3}°, ω sign {sign}{(decisive ? "" : ", NOT decisive")}, lead {lead:F0} s, downrange {downrange * 180 / Math.PI:F2}° ({(measured ? "measured" : "estimate")})"));

            var q = new RendezvousLaunchRequest
            {
                BodyName = node.Name,
                BodyRadius = node.Radius,
                Mu = node.Mu,
                LowOrbitRadius = node.LowOrbitRadius,
                ClearRadius = clear,
                Omega = omega,
                SiteAtUT0 = r,
                UT0 = now,
                TargetR = rT,
                TargetV = vT,
                LeadSeconds = lead,
                DownrangeRad = downrange,
                StartDelaySeconds = RendezvousStartDelaySeconds,
            };
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            job_watch = Stopwatch.StartNew();
            rv_job = Task.Run(() => RendezvousLaunch.Plan(q, token), token);
            status_line = $"Planning the launch for the rendezvous with {target.Name}...";
        }

        void OnRendezvousPlanned()
        {
            var j = rv_job;
            rv_job = null;
            cts = null;
            var p = lift.transfer;
            if (j.IsCanceled || p == null)
                return;
            if (j.IsFaulted)
            {
                Exception e = j.Exception?.GetBaseException();
                refusal = $"The rendezvous launch plan failed: {e?.GetType().Name}: {e?.Message}";
                logger.LogWarning("[Lift] " + refusal);
                return;
            }
            RendezvousLaunchPlan plan = j.Result;
            double ms = job_watch.Elapsed.TotalMilliseconds;
            int n = plan.Passes.Count;
            for (int k = 0; k < n; k++)
            {
                var pass = plan.Passes[k];
                var w = pass.Window;
                string head = Inv($"[Lift] rendezvous: pass {k + 1}/{n} UT {w.LaunchUT:F0} ({w.Pass}), azimuth {w.AzimuthDeg:F2}°, plane change {w.PlaneErrorDeg:F3}° ({w.PlaneChangeDeltaV:F1} m/s), phase at insertion {pass.PhaseDeg:F1}°, ");
                logger.LogInfo(pass.Rendezvous.Ok
                    ? head + Inv($"rendezvous {pass.DeltaV:F0} m/s ({pass.Rendezvous.Revolutions} rev, arrival UT {pass.Rendezvous.ArrivalUT:F0})")
                    : head + "refused: " + pass.Rendezvous.Refusal);
            }
            p.Rendezvous = plan;
            if (!plan.Ok)
            {
                refusal = $"No rendezvous with {p.TargetName}: {plan.Refusal}";
                refusal_is_plan = true;
                logger.LogInfo(Inv($"[Lift] rendezvous: {plan.Refusal} ({n} pass(es) planned in {ms:F0} ms)"));
                return;
            }
            p.Window = plan.Window;
            p.OrbitRadius = plan.ParkingRadius;
            int apKm = (int)Math.Floor((plan.ParkingRadius - p.BodyRadius) / 1000);
            settings.SetApOverride(apKm); // this run only: the saved setting is never written
            if (double.IsNaN(p.PlannedLaunchUT))
                p.PlannedLaunchUT = plan.Window.LaunchUT;
            double now = GeneralTools.Current_UT;
            double dv1 = plan.Passes[0].DeltaV;
            logger.LogInfo(Inv($"[Lift] rendezvous: chosen pass {plan.Chosen + 1} at UT {plan.Window.LaunchUT:F1} (in {plan.Window.LaunchUT - now:F0} s): ") +
                Inv($"{plan.Rendezvous.TotalDeltaV:F0} m/s, first pass {dv1:F0} m/s, {n} pass(es) planned in {ms:F0} ms; Ap {apKm} km for this run"));
            logger.LogInfo("[Lift] rendezvous: " + plan.Explanation);
        }

        // A missed launch time: the transfer goal solves the next window, the rendezvous goal plans
        // every pass again (the target's phase has moved on).
        void OnMissedLaunch(string why)
        {
            var p = lift.transfer;
            if (p != null && LiftGoalRules.OnMissedLaunch(p.Goal) == LiftGoalRules.ResolveMode.ReplanAll)
            {
                logger.LogInfo(Inv($"[Lift] rendezvous: re-planning every pass ({why}); the missed launch was UT {(p.Window != null ? p.Window.LaunchUT : double.NaN):F1}"));
                stage_pending = false;
                BeginRendezvousPlanning(p); // sets the warp to 1x itself
                return;
            }
            Resolve(why);
        }
    }
}
