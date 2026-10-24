using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing.Braking;
using K2D2.Node;
using K2D2.Transfer;
using K2D2.UI;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Lift
{
    // Transfer goal (LiftGoal.PrepareTransfer), before the ascent: plans the parking orbit and the
    // launch window, then waits on the pad until the launch time and fires the first stage.
    //
    // 1. Game reads on the main thread (bodies, vessel, rotation), then ParkingPlane.Plan and
    //    LaunchWindow.Find on a worker thread (pure code). The result is polled in Update.
    // 2. The wait (LiftGoalRules.Decide): warp until LaunchUT − 30 s (LaunchUT already includes the
    //    ascent lead), hold at 1×, then launch: full throttle and the next stage, once per run.
    // 3. A launch time passed by more than 5 s while still on the pad (the player warped past it,
    //    or the launch did not lift off) is solved again for the next pass, never flown late. The
    //    plane is inertially fixed, so only the window is solved again (on the main thread: cheap).
    //
    // The rotation sign of ω is measured on the pad, where the surface speed is 0
    // (BodyRotation.ChooseSign, as the Landing tab does).
    public class WaitLaunch : ExecuteController
    {
        public ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Lift");

        readonly LiftPilot lift;
        readonly LiftSettings settings;
        KSPVessel current_vessel;

        Task<(ParkingPlan parking, LaunchWindowResult window)> job;
        CancellationTokenSource cts;
        Stopwatch job_watch;
        string refusal;

        // A first stage fired and not yet confirmed: the staging read just before it.
        bool stage_pending;
        int stage_count_before;
        bool last_active_before;

        public WaitLaunch(LiftPilot lift, LiftSettings settings)
        {
            this.lift = lift;
            this.settings = settings;
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // A refusal here is ended in Update: ending the pilot from inside its status setter (which
        // calls Start) would nest two status changes (same rule as FinalCircularize).
        public override void Start()
        {
            base.Start();
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            refusal = null;
            status_line = "";
            stage_pending = false;
            var p = lift.transfer;
            if (p != null && p.Ready)
            {
                // Back from the ascent: no liftoff within 5 s of the launch time.
                Resolve("no liftoff within 5 s of the launch time");
                return;
            }
            BeginPlanning();
        }

        public void Cancel()
        {
            if (cts != null)
                cts.Cancel();
            cts = null;
            job = null;
        }

        void BeginPlanning()
        {
            Cancel();
            VesselComponent vessel = current_vessel?.VesselComponent;
            if (vessel == null)
            {
                refusal = "No vessel";
                return;
            }
            if (!vessel.LandedOrSplashed)
            {
                refusal = "Prepare transfer starts on the launch pad: the vessel is not landed.";
                return;
            }
            string target = settings.transfer_target.V;
            if (string.IsNullOrEmpty(target))
            {
                refusal = "Choose the transfer's target body.";
                return;
            }

            var list = GameBodySystem.GameBodies();
            double ut = GeneralTools.Current_UT;
            BodySystem system = GameBodySystem.Build(list, ut, out string error);
            if (system == null)
            {
                refusal = "Cannot plan: " + error;
                return;
            }
            if (!VesselSnapshot.TryRead(list, out int body, out Vector3d r, out Vector3d v, out double t, out _, out error))
            {
                refusal = "Cannot plan: " + error;
                return;
            }
            // The target by name, resolved against this snapshot's bodies (as the Transfer tab does).
            if (!new TransferInput { TargetName = target }.Resolve(system, out TransferRequest template, out error))
            {
                refusal = error;
                return;
            }
            if (template.ToBody == body)
            {
                refusal = $"The vessel is already at {target}: choose another body.";
                return;
            }

            BodyNode node = system[body];
            int apKm = LiftGoalRules.ParkingApKm(node.LowOrbitRadius, node.Radius, settings.destination_Ap_km.V);
            if (apKm != settings.destination_Ap_km.V)
            {
                // For this run only: the player's saved Ap stays as it is.
                logger.LogInfo($"[Lift] Ap raised from {settings.destination_Ap_km.V} km to {apKm} km for this run: the low orbit of {node.Name}");
                settings.SetApOverride(apKm);
            }
            double radius = node.Radius + apKm * 1000.0;

            CelestialBodyComponent cb = vessel.Orbit.referenceBody;
            double w = BodyRotation.AngularSpeed(cb.rotationPeriod);
            double surfaceSpeed = current_vessel.VesselVehicle.SurfaceSpeed;
            int sign = BodyRotation.ChooseSign(r, v, w, surfaceSpeed);
            bool decisive = BodyRotation.IsDecisive(r, v, w);
            Vector3d omega = BodyRotation.AngularVelocity(w, sign);

            var stored = settings.MeasuredLead(node.Name);
            LiftGoalRules.AscentLead(stored.s.V, stored.deg.V, out double lead, out double downrange, out bool measured);

            var p = new LiftTransferPlan
            {
                TargetName = target,
                BodyName = node.Name,
                BodyRadius = node.Radius,
                Mu = node.Mu,
                OrbitRadius = radius,
                Omega = omega,
                LeadSeconds = lead,
                DownrangeRad = downrange,
                LeadMeasured = measured,
            };
            lift.transfer = p;
            lift.ascent.transfer_plan = p;

            double lat = Math.Asin(Math.Max(-1, Math.Min(1, r.z / r.magnitude)));
            double horizon = Horizon(omega);
            int targetIndex = template.ToBody;
            logger.LogInfo(Inv($"[Lift] planning a transfer to {target} from {node.Name}: parking orbit {apKm} km, site latitude {lat * 180 / Math.PI:F3}°, ") +
                Inv($"ω {w:e4} rad/s sign {sign} (surface speed {surfaceSpeed:F1} m/s{(decisive ? "" : ", NOT decisive: +1 assumed")}), ") +
                Inv($"ascent lead {lead:F0} s, downrange {downrange * 180 / Math.PI:F2}° ({(measured ? "measured" : "estimate")})"));

            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            job_watch = Stopwatch.StartNew();
            job = Task.Run(() =>
            {
                ParkingPlan parking = ParkingPlane.Plan(system, body, targetIndex, lat, t, radius, template, token);
                LaunchWindowResult window = parking.Ok
                    ? LaunchWindow.Find(node.Radius, node.Mu, omega, r, t, parking.PlaneNormal, radius, lead, downrange, horizon)
                    : null;
                return (parking, window);
            }, token);
            status_line = $"Planning the parking orbit for {target}...";
        }

        // Two turns of the body: four passes at least.
        static double Horizon(Vector3d omega) =>
            omega.magnitude > 0 ? 2 * 2 * Math.PI / omega.magnitude : double.PositiveInfinity;

        void OnPlanned()
        {
            var j = job;
            job = null;
            cts = null;
            var p = lift.transfer;
            if (j.IsCanceled || p == null)
                return;
            if (j.IsFaulted)
            {
                Exception e = j.Exception?.GetBaseException();
                refusal = $"The transfer plan failed: {e?.GetType().Name}: {e?.Message}";
                logger.LogWarning("[Lift] " + refusal);
                return;
            }
            var (parking, window) = j.Result;
            p.Parking = parking;
            p.Window = window;
            logger.LogInfo($"[Lift] plan done in {job_watch.Elapsed.TotalMilliseconds:F0} ms");
            if (!parking.Ok)
            {
                refusal = $"No transfer to {p.TargetName}: {parking.Refusal}";
                logger.LogInfo("[Lift] " + refusal);
                return;
            }
            if (!window.Ok)
            {
                refusal = window.Refusal;
                logger.LogInfo("[Lift] " + refusal);
                return;
            }
            p.PlannedLaunchUT = window.LaunchUT;
            Vector3d n = parking.PlaneNormal;
            double now = GeneralTools.Current_UT;
            logger.LogInfo("[Lift] plan: " + parking.Explanation);
            logger.LogInfo(Inv($"[Lift] plan: plane inclination {Math.Acos(Math.Max(-1, Math.Min(1, n.z))) * 180 / Math.PI:F3}°, normal ({n.x:F5}, {n.y:F5}, {n.z:F5}); ") +
                Inv($"launch at UT {window.LaunchUT:F1} (in {window.LaunchUT - now:F0} s), {window.Pass} pass, azimuth {window.AzimuthDeg:F2}°, ") +
                Inv($"plane change left {window.PlaneErrorDeg:F3}° ({window.PlaneChangeDeltaV:F1} m/s); ") +
                Inv($"window UT {parking.WindowUT:F0} ({KerbinCalendar.Date(parking.WindowUT)}), transfer {parking.TransferDeltaV:F0} m/s"));
        }

        // The next pass from the site where it is now; the plane stays the planned one.
        void Resolve(string why)
        {
            var p = lift.transfer;
            VesselComponent vessel = current_vessel?.VesselComponent;
            if (p == null || !p.Ready || vessel == null)
            {
                refusal = "No transfer plan to solve again.";
                return;
            }
            double now = GeneralTools.Current_UT;
            IKeplerPatch orbit = vessel.Orbit;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            double missed = p.Window.LaunchUT;
            stage_pending = false;
            var window = LaunchWindow.Find(p.BodyRadius, p.Mu, p.Omega, r, now, p.Parking.PlaneNormal, p.OrbitRadius,
                p.LeadSeconds, p.DownrangeRad, Horizon(p.Omega));
            p.Resolves++;
            logger.LogInfo(Inv($"[Lift] launch time missed ({why}): launch was UT {missed:F1}, now UT {now:F1}; ") +
                (window.Ok
                    ? Inv($"next pass UT {window.LaunchUT:F1} (in {window.LaunchUT - now:F0} s), {window.Pass}, azimuth {window.AzimuthDeg:F2}°")
                    : "no next pass: " + window.Refusal));
            if (!window.Ok)
            {
                refusal = window.Refusal;
                return;
            }
            p.Window = window;
        }

        public override void Update()
        {
            if (finished)
                return;
            if (refusal == null && job != null)
            {
                if (!job.IsCompleted)
                    return;
                OnPlanned();
            }
            if (refusal != null)
            {
                string why = refusal;
                refusal = null;
                TimeWarpTools.SetRateIndex(0, false);
                lift.EndLiftPilot(false, why);
                return;
            }

            var p = lift.transfer;
            VesselComponent vessel = current_vessel?.VesselComponent;
            if (p == null || !p.Ready || vessel == null)
                return;

            double now = GeneralTools.Current_UT;
            double launch = p.Window.LaunchUT;
            switch (LiftGoalRules.Decide(now, launch, vessel.LandedOrSplashed))
            {
                case PadAction.Warp:
                {
                    double dt = LiftGoalRules.WarpEndUT(launch) - now;
                    TimeWarpTools.SetRateIndex(WarpToSettings.compute_wanted_warp_index(dt), false);
                    status_line = $"Waiting for the launch window: launch in {KerbinCalendar.Duration(launch - now)}";
                    break;
                }
                case PadAction.Hold:
                    if (TimeWarpTools.CurrentRateIndex != 0)
                        TimeWarpTools.SetRateIndex(0, false);
                    status_line = $"Launch in {KerbinCalendar.Duration(launch - now)}: the mod stages at T-0";
                    break;
                case PadAction.Launch:
                    if (TimeWarpTools.CurrentRateIndex != 0)
                    {
                        // Staging does nothing on rails: wait for 1× (within the 5 s).
                        TimeWarpTools.SetRateIndex(0, true);
                        return;
                    }
                    if (!Launch(p, vessel, now))
                        return;
                    logger.LogInfo(Inv($"[Lift] launch: UT {now:F1} against the planned UT {launch:F1} (T{now - launch:+0.0;-0.0;+0.0} s), ") +
                        Inv($"first plan UT {p.PlannedLaunchUT:F1}, {p.Resolves} pass(es) solved again, azimuth {p.Window.AzimuthDeg:F2}°"));
                    status_line = "Launch!";
                    finished = true;
                    break;
                case PadAction.Resolve:
                    Resolve("the clock passed the launch time on the pad");
                    break;
                case PadAction.LiftedOff:
                    p.EarlyLiftoff = true;
                    logger.LogWarning(Inv($"[Lift] lifted off at UT {now:F1}, before the launch time UT {launch:F1} (T{now - launch:+0.0;-0.0;+0.0} s): ") +
                        "the plane will be off, the ascent steering and the trim take it out");
                    finished = true;
                    break;
            }
        }

        // Staging at T-0. True once the vessel is under way: the stage fired (confirmed a frame
        // later), or engines already lit (throttle only). A refusal sets 'refusal'.
        bool Launch(LiftTransferPlan p, VesselComponent vessel, double now)
        {
            StagingComponent staging = vessel.SimulationObject?.Staging;
            int count = staging?.StageCount ?? 0;
            bool lastActive = count > 0 && staging.AvailableStages[count - 1].IsActive;

            if (stage_pending)
            {
                if (LiftGoalRules.StageFired(stage_count_before, last_active_before, count, lastActive))
                {
                    stage_pending = false;
                    p.Staged = true;
                    logger.LogInfo($"[Lift] launch: first stage fired ({stage_count_before} -> {count} stage(s), last stage active {lastActive})");
                    return true;
                }
                // ActivateNextStage did nothing (paused, or warp on rails): it fires nothing, so
                // trying again cannot fire a second stage.
                var universe = GeneralTools.Game?.UniverseModel;
                if (universe != null && universe.Time.IsPaused)
                    return false;
                vessel.ActivateNextStage();
                return false;
            }

            int engines = ActiveEngines(vessel);
            switch (LiftGoalRules.LaunchStagingFor(p.Staged, count, lastActive, engines))
            {
                case LaunchStaging.Stage:
                    current_vessel.SetThrottle(settings.max_throttle.V);
                    stage_pending = true;
                    stage_count_before = count;
                    last_active_before = lastActive;
                    vessel.ActivateNextStage();
                    status_line = "Staging...";
                    return false;
                case LaunchStaging.ThrottleOnly:
                    logger.LogInfo($"[Lift] launch: {engines} engine(s) already lit ({count} stage(s), last stage active {lastActive}): " +
                        "lift-off on the throttle, no stage fired");
                    current_vessel.SetThrottle(settings.max_throttle.V);
                    return true;
                default:
                    logger.LogInfo($"[Lift] launch refused: {count} stage(s), last stage active {lastActive}, {engines} engine(s) lit");
                    refusal = LiftGoalRules.StagedRefusal;
                    return false;
            }
        }

        static int ActiveEngines(VesselComponent vessel)
        {
            var infos = vessel.VesselDeltaV?.EngineInfo;
            if (infos == null)
                return 0;
            int n = 0;
            foreach (var e in infos)
                if (e != null && BurndV.IsActive(e))
                    n++;
            return n;
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status(job != null ? "Planning the transfer..." : "Waiting on the pad");
            st.Console(status_line);
        }

        // The plan's readout is shown once, by the tab's transfer table (LiftUI).
        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Status", "Waiting on the pad");
        }

        static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
