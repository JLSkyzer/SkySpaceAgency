using System;
using System.Collections.Generic;
using System.Globalization;
using K2D2.Lift;
using K2D2.OrbitPlanning;
using K2D2.Rendezvous;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Transfer
{
    // "Transfer + rendezvous (+ dock)" (sub-project 4.5): a target vessel around another body. The
    // chain lives here: FarRendezvous plans the transfer into the target's plane, the rendezvous
    // budget and the docking reserve; this pilot flies the transfer, re-reads the target's plane at
    // each arrival correction, saves the chain with the plan, and in the parking orbit hands over to
    // the Rendezvous pilot (as LiftPilot.ChainToRendezvous does), after it has ended itself.
    public partial class TransferPilot
    {
        // The chain of the plan in hand or in flight, null for a plain transfer.
        public SavedChain Chain { get; private set; }
        // The chain's plan (the Rendezvous tab's rows), null when none.
        public FarRendezvousPlan ChainPlan { get; private set; }
        // The Rendezvous tab shows this pilot's status: a chain was planned, refused, flown or stopped
        // from there. Cleared by a plain transfer plan and by ForgetChainStatus.
        public bool ChainStatusShown { get; private set; }
        // The saved plan of the active vessel is a chain: the Rendezvous tab offers its Resume.
        public bool SavedIsChain => saved?.Chain != null;

        // The correction steps whose unreadable target was logged already (once per step).
        readonly HashSet<int> chain_plane_unread_logged = new HashSet<int>();

        public void ForgetChainStatus() => ChainStatusShown = false;

        void ClearChain()
        {
            Chain = null;
            ChainPlan = null;
            ChainStatusShown = false;
            chain_plane_unread_logged.Clear();
        }

        /// <summary>
        /// Plans the transfer into the target's plane, the rendezvous and (mode) the docking, on a
        /// worker thread; flies it at once unless it is refused or the stages cannot pay for it.
        /// </summary>
        public void PlanChain(string targetId, RvMode mode, bool prepareOnly)
        {
            if (isRunning)
            {
                Status = "Stop the transfer before planning another one.";
                return;
            }
            CancelJob();
            ClearChain();
            ChainStatusShown = true;
            Current = null;
            Budget = null;
            if (!TakeSnapshot(out var snap, out string error, out _))
            {
                Status = "Cannot plan: " + error;
                return;
            }
            var ours = ActiveVessel();
            var target = string.IsNullOrEmpty(targetId) ? null : RendezvousSnapshot.FindVessel(targetId);
            bool isOurs = target != null && ours != null && target.GlobalId.Equals(ours.GlobalId);
            IKeplerPatch orbit = isOurs ? null : target?.Orbit;
            bool exists = orbit != null;
            bool landed = exists && target.LandedOrSplashed;
            string bodyName = orbit?.referenceBody?.bodyName;
            int tb = exists ? IndexOfName(snap.System, bodyName) : -1;
            Vector3d rT = Vector3d.zero, vT = Vector3d.zero;
            var elT = new OrbitElements();
            if (exists && tb >= 0)
            {
                rT = orbit.GetRelativePositionAtUTZup(snap.UT);
                vT = orbit.GetOrbitalVelocityAtUTZup(snap.UT);
                elT = OrbitMath.Elements(rT, vT, snap.System[tb].Mu);
            }
            string refusal = FarRendezvous.TargetRefusal(snap.System, snap.Body, tb, exists && tb >= 0, landed, elT);
            if (refusal != null)
            {
                Status = refusal;
                logger.LogInfo($"[Transfer] chain: refused before planning: {refusal}");
                return;
            }
            RendezvousSnapshot.SetTarget(ours, target.GlobalId, "the rendezvous target");

            var node = snap.System[tb];
            double parking = RendezvousLaunch.ParkingRadius(elT.PeriapsisRadius, node.LowOrbitRadius, node.Radius, out string parkingReason);
            var n = elT.AngularMomentum.normalized;
            bool dock = RendezvousFlight.Docks(mode);
            string name = target.Name;
            string id = target.GlobalId.Guid.ToString("D");
            logger.LogInfo(Inv($"[Transfer] chain: planning transfer + rendezvous{(dock ? " + dock" : "")} with {name} ({id}) around {node.Name}: ") +
                Inv($"target Pe {(elT.PeriapsisRadius - node.Radius) / 1000:F1} km, Ap {(elT.ApoapsisRadius - node.Radius) / 1000:F1} km, ") +
                Inv($"inclination {elT.InclinationRad * 180 / Math.PI:F3}° (game orbit.inclination {orbit.inclination}), ") +
                Inv($"normal ({n.x:F5}, {n.y:F5}, {n.z:F5}) in {node.Name}'s frame; parking {(parking - node.Radius) / 1000:F0} km ({parkingReason}); ") +
                (prepareOnly ? "Prepare only" : "flown"));

            var q = new FarRendezvousRequest
            {
                FromBody = snap.Body, TargetBody = tb, R0 = snap.R, V0 = snap.V, UT0 = snap.UT,
                TargetR = rT, TargetV = vT, TargetName = name, Dock = dock,
            };
            Adopt(snap);
            Status = $"Planning the transfer to {node.Name} and the rendezvous with {name}...";
            var system = snap.System;
            var snapStages = snap.Stages;
            var chain = new SavedChain
            {
                TargetId = id, TargetName = name, TargetBodyName = node.Name, Mode = mode.ToString(),
                PlaneNormal = n, ParkingRadius = parking,
            };
            progress.Report(0);
            RunJob(JobKind.Search, ct => FarRendezvous.Plan(system, q, TransferSearch.Find, ct, progress),
                t => OnChainPlanned(t, system, snapStages, chain, prepareOnly, resume: null));
        }

        // A finished chain plan: shown, then flown when the stages can pay for it (resume: flown
        // from the saved plan's options).
        void OnChainPlanned(global::System.Threading.Tasks.Task<FarRendezvousPlan> t, BodySystem system, List<StageDv> snapStages,
            SavedChain chain, bool prepareOnly, SavedPlan resume)
        {
            if (!PilotJob.Result(t, out var p, out string error, out bool canceled))
            {
                Status = canceled ? "Search cancelled." : "The plan failed: " + error;
                logger.LogInfo($"[Transfer] chain: plan {(canceled ? "cancelled" : "failed: " + error)} after {last_job_ms:F0} ms");
                return;
            }
            System = system;
            stages = snapStages ?? stages;
            ChainStatusShown = true;
            if (p.Transfer != null)
                logger.LogInfo(TransferFlight.Describe(p.Transfer, system, last_job_ms));
            if (!p.Ok)
            {
                Current = null;
                Budget = null;
                Chain = null;
                ChainPlan = null;
                Status = "Refused: " + p.Refusal;
                logger.LogInfo($"[Transfer] chain: refused: {p.Refusal}");
                return;
            }
            var sHat = p.ArrivalAsymptote;
            logger.LogInfo(Inv($"[Transfer] chain: arrival asymptote ({sHat.x:F4}, {sHat.y:F4}, {sHat.z:F4}) ({p.AsymptoteSource}), ") +
                Inv($"plane left after the arrival correction {p.PlaneResidualDeg:F3}°"));
            double pm = 0;
            foreach (var st in p.Rendezvous.Steps)
                if (st.Kind == RvStepKind.PlaneMatch)
                    pm += st.DeltaVMagnitude;
            var node = system[p.Transfer.Request.ToBody];
            logger.LogInfo(Inv($"[Transfer] chain: rendezvous estimate at UT {p.Transfer.ArrivalUT:F0} from a circular {(p.ParkingRadius - node.Radius) / 1000:F0} km orbit: ") +
                Inv($"{p.PhasesPlanned}/{FarRendezvous.PhaseSamples} phases planned, {p.RendezvousBest:F0} to {p.RendezvousWorst:F0} m/s ") +
                Inv($"(plane match {pm:F1} m/s in the worst); planned in {p.PlanSeconds * 1000:F0} ms"));
            logger.LogInfo("[Transfer] chain: " + p.Explanation);

            chain.PlaneNormal = p.TargetPlaneNormal;
            chain.ParkingRadius = p.ParkingRadius;
            chain.RendezvousDeltaV = p.RendezvousWorst;
            chain.DockReserve = p.DockReserve;
            Chain = chain;
            ChainPlan = p;
            Current = p.Transfer;
            Budget = BudgetFor(Current, 0);
            logger.LogInfo(Inv($"[Transfer] chain: total {p.TotalDeltaV:F0} m/s = transfer {p.Transfer.TotalDeltaV:F0} + rendezvous {p.RendezvousWorst:F0} + docking reserve {p.DockReserve:F0}; ") +
                $"stage budget {(Budget.Ok ? "ok" : Budget.Refusal)}; stages: {StageList.Describe(stages)}");
            if (!Budget.Ok)
            {
                Status = Budget.Refusal;
                return;
            }
            if (resume != null)
            {
                if (!TakeSnapshot(out var snap, out string err, out _))
                {
                    Status = "Cannot resume: " + err;
                    return;
                }
                Adopt(snap);
                Begin(Current, 0, resume.PrepareOnly, snap, "resume (transfer + rendezvous planned again)");
                return;
            }
            StartCore(prepareOnly, "start (transfer + rendezvous)");
        }

        // The per-stage check from step 'from': the rendezvous and the docking reserve on top when a
        // chain is set.
        BudgetResult BudgetFor(TransferPlan plan, int from) =>
            Chain == null
                ? TransferFlight.RemainingBudget(plan, from, stages)
                : FarRendezvous.Budget(plan, from, Chain.RendezvousDeltaV, Chain.DockReserve, stages);

        // The plane the correction of step i aims at: on a chain's final target, the target's plane
        // read now (a change above PlaneChangeLogDeg is taken and logged); otherwise the stored one.
        Vector3d ReadTargetPlane(int i, PlanStep step, int n)
        {
            var plan = Current;
            if (Chain == null || plan?.Request == null || step.TargetBody != plan.Request.ToBody || step.TargetBody < 0)
                return step.TargetPlaneNormal;
            var stored = step.TargetPlaneNormal.magnitude > 0 ? step.TargetPlaneNormal : Chain.PlaneNormal;
            string arrival = System[step.TargetBody].Name;
            var target = RendezvousSnapshot.FindVessel(Chain.TargetId);
            IKeplerPatch orbit = target?.Orbit;
            string why = HandoffWhy(target, arrival);
            if (why != null || orbit == null)
            {
                if (chain_plane_unread_logged.Add(i))
                    logger.LogInfo($"[Transfer] chain: correction {n}: target plane not readable ({why ?? "no orbit"}): stored plane kept");
                return stored;
            }
            double ut = GeneralTools.Current_UT;
            var now = Vector3d.Cross(orbit.GetRelativePositionAtUTZup(ut), orbit.GetOrbitalVelocityAtUTZup(ut)).normalized;
            if (!FarRendezvous.PlaneChanged(stored, now, out double deg))
            {
                logger.LogInfo($"[Transfer] chain: correction {n}: target plane re-read, unchanged");
                return stored;
            }
            logger.LogInfo(Inv($"[Transfer] chain: correction {n}: target plane re-read, changed {deg:F3}°"));
            for (int k = i; k < plan.Steps.Count; k++)
                if (plan.Steps[k].TargetBody == plan.Request.ToBody)
                    plan.Steps[k].TargetPlaneNormal = now;
            plan.Request.ArrivalPlaneNormal = now;
            Chain.PlaneNormal = now;
            return now;
        }

        // Null when the target can still be met around 'arrivalBody', else why not.
        static string HandoffWhy(VesselComponent target, string arrivalBody)
        {
            IKeplerPatch orbit = target?.Orbit;
            bool exists = target != null && orbit != null;
            bool landed = exists && target.LandedOrSplashed;
            bool around = exists && orbit.referenceBody?.bodyName == arrivalBody;
            return FarRendezvous.HandoffRefusal(exists, landed, around, arrivalBody);
        }

        // In the parking orbit, after Finish has ended the run: the Rendezvous pilot takes over, or
        // the chain stops with the reason. The transfer has ended first, so the Rendezvous pilot's
        // ResetControllers never resets a running transfer.
        void HandOver(int body, Vector3d r, Vector3d v)
        {
            var chain = Chain;
            ClearChain();
            ChainStatusShown = true;
            if (chain == null)
                return;
            string name = string.IsNullOrEmpty(chain.TargetName) ? "the target" : chain.TargetName;
            RendezvousFlight.TryParse(chain.Mode, out RvMode mode);
            string arrival = System[body].Name;
            var target = RendezvousSnapshot.FindVessel(chain.TargetId);
            string why = HandoffWhy(target, arrival);

            var frame = System[body].FrameToCommon;
            var rl = CelestialFrames.ToLocal(frame, r);
            var vl = CelestialFrames.ToLocal(frame, v);
            string planeText = "plane error unknown", distanceText = "distance unknown";
            if (why == null)
            {
                double ut = GeneralTools.Current_UT;
                var rT = target.Orbit.GetRelativePositionAtUTZup(ut);
                var vT = target.Orbit.GetOrbitalVelocityAtUTZup(ut);
                FarRendezvous.PlaneChanged(Vector3d.Cross(rl, vl), Vector3d.Cross(rT, vT), out double err);
                planeText = Inv($"plane error to {name} {err:F3}°");
                distanceText = Inv($"distance {(rT - rl).magnitude / 1000:F1} km");
            }
            logger.LogInfo($"[Transfer] chain: in orbit {OrbitText(body, r, v)}, {planeText}, {distanceText}; " +
                $"handing over ({mode}{(PrepareOnly ? ", Prepare only" : "")})");
            if (why != null)
            {
                Status = FarRendezvous.ChainStopped("the hand-over", name, why, afterArrival: true);
                logger.LogInfo($"[Transfer] chain: hand-over refused: {why}");
                return;
            }
            var rv = RendezvousPilot.Instance;
            bool started = rv != null && rv.Start(mode, chain.TargetId, PrepareOnly);
            if (started)
            {
                Status = FarRendezvous.HandoffStarted(name, RendezvousFlight.Docks(mode), PrepareOnly);
                logger.LogInfo("[Transfer] chain: hand-over started");
            }
            else
            {
                string rvWhy = rv == null ? "no Rendezvous pilot" : rv.Status;
                Status = FarRendezvous.ChainStopped("the hand-over", name, "the Rendezvous pilot refused: " + rvWhy, afterArrival: true);
                logger.LogInfo($"[Transfer] chain: hand-over refused: the Rendezvous pilot refused: {rvWhy}");
            }
        }

        // The step the chain is at, for its stop texts.
        string ChainStepName()
        {
            var plan = Current;
            if (plan == null || StepIndex < 0 || StepIndex >= plan.Steps.Count)
                return "the transfer";
            switch (plan.Steps[StepIndex].Kind)
            {
                case StepKind.PlaneChange: return "the plane change";
                case StepKind.OrbitChange: return "the orbit change";
                case StepKind.Injection: return "the injection";
                case StepKind.Escape: return "the escape";
                case StepKind.Correction: return "the correction " + TransferFlight.CorrectionNumber(plan, StepIndex);
                case StepKind.Capture: return "the capture";
                default: return "the transfer";
            }
        }

        // A chain stopped before the arrival: the text that says the step, why and what is left.
        void ChainStoppedStatus(string why)
        {
            if (Chain == null)
                return;
            string step = ChainStepName();
            Status = FarRendezvous.ChainStopped(step, string.IsNullOrEmpty(Chain.TargetName) ? "the target" : Chain.TargetName, why, afterArrival: false);
            ChainStatusShown = true;
            logger.LogInfo($"[Transfer] chain: stopped during {step}: {why}");
        }

        // Resume of a saved chain whose plan must be searched again: the chain search when the target
        // can be read (estimate and budget again), else the plain search with the saved plane (the
        // transfer goes on to the parking orbit, the hand-over will stop with the reason).
        // False: not a chain, the caller runs its own re-plan.
        bool ReplanChain(SavedPlan resumed, Snapshot snap, int toBody)
        {
            var chain = Chain;
            if (chain == null || toBody < 0)
                return false;
            var target = RendezvousSnapshot.FindVessel(chain.TargetId);
            string why = HandoffWhy(target, chain.TargetBodyName);
            logger.LogInfo($"[Transfer] chain: resume: target {(why == null ? "found" : "gone (" + why + ")")}, Replan");
            if (why != null)
            {
                // The rendezvous will not follow: not budgeted any more.
                chain.RendezvousDeltaV = 0;
                chain.DockReserve = 0;
                return false;
            }
            double ut = snap.UT;
            var rT = target.Orbit.GetRelativePositionAtUTZup(ut);
            var vT = target.Orbit.GetOrbitalVelocityAtUTZup(ut);
            RendezvousFlight.TryParse(chain.Mode, out RvMode mode);
            var q = new FarRendezvousRequest
            {
                FromBody = snap.Body, TargetBody = toBody, R0 = snap.R, V0 = snap.V, UT0 = ut,
                TargetR = rT, TargetV = vT, TargetName = chain.TargetName, Dock = RendezvousFlight.Docks(mode),
                MaxTripSeconds = resumed.Plan?.Request?.MaxTripSeconds ?? double.PositiveInfinity,
            };
            Status = $"Planning the transfer to {chain.TargetBodyName} and the rendezvous with {chain.TargetName} again from the actual state...";
            ChainStatusShown = true;
            var system = snap.System;
            var snapStages = snap.Stages;
            progress.Report(0);
            RunJob(JobKind.Search, ct => FarRendezvous.Plan(system, q, TransferSearch.Find, ct, progress),
                t => OnChainPlanned(t, system, snapStages, chain, resumed.PrepareOnly, resumed));
            return true;
        }

        // The status after the plain re-plan of a chain whose target is gone (decision 6).
        string ChainGoneText(SavedChain chain, BodySystem s, int toBody)
        {
            string km = toBody >= 0 && toBody < s.Count ? ((chain.ParkingRadius - s[toBody].Radius) / 1000).ToString("F0", CultureInfo.InvariantCulture) : "?";
            return $"{chain.TargetName} is gone: the transfer goes on to the {km} km orbit, the rendezvous will not follow.";
        }
    }
}
