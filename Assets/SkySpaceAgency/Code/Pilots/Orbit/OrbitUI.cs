using System;
using System.Text;
using K2D2.KSPService;
using K2UI;
using K2UI.Tabs;
using KSP.Sim;
using KTools;
using UnityEngine.UIElements;

namespace K2D2.OrbitPlanning
{
    // Orbit tab: set a target Ap / Pe / inclination and create every maneuver node needed to
    // reach it. Execution stays with the Node tab. A page without a pilot, like AboutUI.
    public class OrbitUI : K2Page
    {
        const double Rad2Deg = 180 / Math.PI;
        // Both units are accepted by the frame guard because the unit of orbit.inclination is
        // unconfirmed in game (Redux documents radians, stock KSP stores degrees).
        const double FrameGuardDeg = 0.5;

        struct Current
        {
            public Vector3d R;
            public Vector3d V;
            public double UT;
            public BodyInfo Body;
            public OrbitElements El;
            public double GameInclinationRad;
            public string BodyName;
            public double ApKm => (El.ApoapsisRadius - Body.Radius) / 1000;
            public double PeKm => (El.PeriapsisRadius - Body.Radius) / 1000;
            public double IncDeg => El.InclinationRad * Rad2Deg;
        }

        readonly ManeuverCreator maneuver_creator = new ManeuverCreator();
        FloatField ap_field, pe_field, inc_field;
        Label summary;
        // The plan as made by "Create maneuvers"; the progress line of OrbitPlanRun goes under it.
        string plan_summary;

        public OrbitUI()
        {
            code = "orbit";
        }

        public override bool onInit()
        {
            ap_field = panel.Q<FloatField>("ap_km").Bind(OrbitSettings.ap_km);
            pe_field = panel.Q<FloatField>("pe_km").Bind(OrbitSettings.pe_km);
            inc_field = panel.Q<FloatField>("inc_deg").Bind(OrbitSettings.inc_deg);
            panel.Q<K2Toggle>("ap_enabled").Bind(OrbitSettings.ap_enabled);
            panel.Q<K2Toggle>("pe_enabled").Bind(OrbitSettings.pe_enabled);
            panel.Q<K2Toggle>("inc_enabled").Bind(OrbitSettings.inc_enabled);

            // Ticking a target starts it from the current value (spec).
            OrbitSettings.ap_enabled.listeners += on => { if (on && TryReadCurrent(out var c)) OrbitSettings.ap_km.V = (float)Math.Round(c.ApKm, 1); };
            OrbitSettings.pe_enabled.listeners += on => { if (on && TryReadCurrent(out var c)) OrbitSettings.pe_km.V = (float)Math.Round(c.PeKm, 1); };
            OrbitSettings.inc_enabled.listeners += on => { if (on && TryReadCurrent(out var c)) OrbitSettings.inc_deg.V = (float)Math.Round(c.IncDeg, 2); };

            summary = panel.Q<Label>("summary");
            panel.Q<Button>("create_maneuvers").listenClick(CreateManeuvers);
            return true;
        }

        public override bool onUpdateUI()
        {
            if (!base.onUpdateUI())
                return false;
            bool hasOrbit = TryReadCurrent(out var c);
            ShowTarget(ap_field, OrbitSettings.ap_enabled.V, hasOrbit, hasOrbit ? Math.Round(c.ApKm, 1) : 0);
            ShowTarget(pe_field, OrbitSettings.pe_enabled.V, hasOrbit, hasOrbit ? Math.Round(c.PeKm, 1) : 0);
            ShowTarget(inc_field, OrbitSettings.inc_enabled.V, hasOrbit, hasOrbit ? Math.Round(c.IncDeg, 2) : 0);
            if (plan_summary != null)
                summary.text = plan_summary + OrbitPlanRun.Status;
            return true;
        }

        // Disabled targets display the live current value without touching the saved target.
        static void ShowTarget(FloatField field, bool enabled, bool hasOrbit, double current)
        {
            field.SetEnabled(enabled);
            if (!enabled && hasOrbit)
                field.SetValueWithoutNotify((float)current);
        }

        // A closed orbit only: the target fields show its current values.
        static bool TryReadCurrent(out Current c) => TryReadTrajectory(out c) && c.El.IsClosed;

        // Any trajectory, open ones included (the planner captures them first).
        static bool TryReadTrajectory(out Current c)
        {
            c = default;
            if (!TryReadState(out c.R, out c.V, out c.UT, out c.Body, out c.GameInclinationRad))
                return false;
            c.El = OrbitMath.Elements(c.R, c.V, c.Body.Mu);
            c.BodyName = K2D2_Plugin.Instance?.current_vessel?.VesselComponent?.Orbit?.referenceBody?.bodyName ?? "body";
            return true;
        }

        /// <summary>
        /// The active vessel's state now (Zup frame, relative to its body) and its body, as the
        /// planner takes them. Also used by the Node tab to plan the next step of a plan in progress.
        /// </summary>
        public static bool TryReadState(out Vector3d r, out Vector3d v, out double ut, out BodyInfo body)
        {
            return TryReadState(out r, out v, out ut, out body, out _);
        }

        static bool TryReadState(out Vector3d r, out Vector3d v, out double ut, out BodyInfo body,
            out double gameInclination)
        {
            r = default;
            v = default;
            ut = 0;
            body = default;
            gameInclination = 0;
            var vessel = K2D2_Plugin.Instance?.current_vessel?.VesselComponent;
            IKeplerPatch orbit = vessel?.Orbit;
            if (orbit == null)
                return false;
            var reference = orbit.referenceBody;
            var parent = reference.referenceBody;
            ut = GeneralTools.Current_UT;
            r = orbit.GetRelativePositionAtUTZup(ut);
            v = orbit.GetOrbitalVelocityAtUTZup(ut);
            body = new BodyInfo
            {
                Mu = reference.gravParameter,
                Radius = reference.radius,
                AtmosphereDepth = reference.hasAtmosphere ? reference.atmosphereDepth : 0,
                // The star has no parent: its SOI is unbounded.
                SoiRadius = parent == null || ReferenceEquals(parent, reference) ? double.PositiveInfinity : reference.sphereOfInfluence
            };
            gameInclination = orbit.inclination;
            return true;
        }

        void CreateManeuvers()
        {
            plan_summary = null;
            if (!TryReadTrajectory(out var c))
            {
                summary.text = "No trajectory to plan from: be in flight.";
                return;
            }
            bool capture = OrbitPlanner.NeedsCapture(c.R, c.V, c.Body);

            // Frame guard (spec): our inclination from the state vectors must match the game's,
            // in either unit (see FrameGuardDeg).
            double gapRadAsDeg = Math.Abs(c.El.InclinationRad - c.GameInclinationRad) * Rad2Deg;
            double gapDegAsDeg = Math.Abs(c.IncDeg - c.GameInclinationRad);
            if (gapRadAsDeg <= FrameGuardDeg || gapDegAsDeg <= FrameGuardDeg)
            {
                string unit = gapRadAsDeg <= FrameGuardDeg ? "radians" : "degrees";
                K2D2_Plugin.logger.LogInfo($"[OrbitUI] frame guard: game inclination {c.GameInclinationRad} " +
                    $"matched as {unit} (computed {c.IncDeg:n3}°)");
            }
            else
            {
                summary.text = $"Unexpected reference frame: computed inclination {c.IncDeg:n2}°, " +
                    $"game reports {c.GameInclinationRad:n4} (raw value). No node created.";
                K2D2_Plugin.logger.LogWarning($"[OrbitUI] frame guard refused: computed inclination {c.IncDeg:n3}°, " +
                    $"game reports {c.GameInclinationRad} (raw value, matches neither radians nor degrees)");
                return;
            }

            // From an open trajectory, no target means a circular orbit at the capture radius.
            if (!capture && !OrbitSettings.ap_enabled.V && !OrbitSettings.pe_enabled.V && !OrbitSettings.inc_enabled.V)
            {
                summary.text = "Tick at least one target (Ap, Pe or Inclination) first.";
                return;
            }

            var targets = new OrbitTargets
            {
                ApAltitude = OrbitSettings.ap_enabled.V ? OrbitSettings.ap_km.V * 1000.0 : (double?)null,
                PeAltitude = OrbitSettings.pe_enabled.V ? OrbitSettings.pe_km.V * 1000.0 : (double?)null,
                InclinationDeg = OrbitSettings.inc_enabled.V ? OrbitSettings.inc_deg.V : (double?)null
            };
            var plan = OrbitPlanner.Plan(c.R, c.V, c.UT, c.Body, targets);
            double vesselDv = VesselDeltaV();
            var budget = OrbitPlanner.CheckCaptureBudget(plan, vesselDv);
            if (plan.Capture != null)
                K2D2_Plugin.logger.LogInfo($"[OrbitUI] capture: {plan.Burns[0].Label} at {c.BodyName}, " +
                    $"{plan.Capture.DeltaV:n1} m/s (vessel {vesselDv:n1} m/s), then {plan.Burns.Count - 1} burn(s), " +
                    $"trajectory e {c.El.Eccentricity:n4} Pe {(c.El.PeriapsisRadius - c.Body.Radius) / 1000:n1} km, SOI {c.Body.SoiRadius / 1000:n0} km");
            else if (capture)
                K2D2_Plugin.logger.LogInfo($"[OrbitUI] capture refused: {plan.Error}, trajectory e {c.El.Eccentricity:n4} " +
                    $"Pe {(c.El.PeriapsisRadius - c.Body.Radius) / 1000:n1} km at {c.BodyName}");
            if (budget != OrbitPlanError.None)
            {
                summary.text = budget == OrbitPlanError.CaptureBeyondDeltaV
                    ? $"The capture at {c.BodyName} needs {plan.Capture.DeltaV:n0} m/s, the vessel has {vesselDv:n0} m/s. No node created."
                    : Describe(plan.Error, c.BodyName);
                return;
            }
            if (plan.Burns.Count == 0)
            {
                summary.text = "Already on the target orbit: nothing to do.";
                return;
            }

            // Only the first burn gets a node: the game dropped the later nodes of a multi-node
            // plan (2026-10-18). The Node tab plans each next step from the orbit actually reached
            // (OrbitPlanSequence) and creates its node once the previous burn is done.
            var sequence = new OrbitPlanSequence(targets, c.Body, plan.Burns.Count);
            var first = sequence.Next(c.R, c.V, c.UT, c.Body);
            if (first.Outcome != OrbitPlanStepOutcome.Burn)
            {
                summary.text = $"Could not plan the first step ({first.Outcome}).";
                return;
            }

            plan_summary = Summary(plan, c);
            K2D2_Plugin.ResetControllers(); // CreateNodes wipes every node: stop any pilot flying one
            OrbitPlanRun.Start(sequence, $"Step 1/{plan.Burns.Count}: creating the node for '{first.Burn.Label}'...");
            maneuver_creator.Update();
            maneuver_creator.CreateNodes(new[] { first.Burn }, count =>
            {
                if (count == 1)
                    OrbitPlanRun.Status = $"Step 1/{plan.Burns.Count}: node created for '{first.Burn.Label}'. " +
                        "Start in the Node tab flies the whole plan: each next step is planned from the orbit actually reached.";
                else
                    OrbitPlanRun.End($"Warning: the game holds {count} node(s), 1 expected. Plan cancelled.");
            });
        }

        // The vessel's total Δv (m/s), NaN when the game does not give it.
        static double VesselDeltaV()
        {
            var dv = K2D2_Plugin.Instance?.current_vessel?.VesselComponent?.VesselDeltaV;
            return dv != null ? dv.TotalDeltaVActual : double.NaN;
        }

        public static string CaptureLine(CaptureInfo capture, string bodyName, double bodyRadius)
        {
            string where = capture.AtPeriapsis ? $"at the {bodyName} periapsis" : $"as soon as possible around the {bodyName}";
            return $"Capture {where} ({(capture.Radius - bodyRadius) / 1000:n0} km) first: {capture.DeltaV:n0} m/s.";
        }

        static string Summary(OrbitPlan plan, Current c)
        {
            var text = new StringBuilder();
            if (plan.Capture != null)
                text.AppendLine(CaptureLine(plan.Capture, c.BodyName, c.Body.Radius));
            for (int i = 0; i < plan.Burns.Count; i++)
            {
                var burn = plan.Burns[i];
                text.AppendLine($"{i + 1}. T+{Duration(burn.UT - c.UT)}  {burn.Label}: {burn.DeltaV.magnitude:n1} m/s");
            }
            text.AppendLine($"Total Δv: {plan.TotalDeltaV:n1} m/s");
            text.AppendLine($"Final orbit: Ap {(plan.Final.ApoapsisRadius - c.Body.Radius) / 1000:n1} km, " +
                $"Pe {(plan.Final.PeriapsisRadius - c.Body.Radius) / 1000:n1} km, " +
                $"inclination {plan.Final.InclinationRad * Rad2Deg:n2}°");
            return text.ToString();
        }

        static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        static string Describe(OrbitPlanError error, string bodyName = "body")
        {
            switch (error)
            {
                case OrbitPlanError.CaptureBelowSurface: return $"The trajectory hits the {bodyName}: its periapsis is below the surface and no capture above it is possible any more.";
                case OrbitPlanError.CaptureInAtmosphere: return $"The trajectory enters the {bodyName} atmosphere: no capture above it is possible any more.";
                case OrbitPlanError.CaptureLeavesSoi: return $"No capture possible: the vessel leaves the {bodyName} SOI first, or the target Ap is beyond it.";
                case OrbitPlanError.EscapeTrajectory: return "Current trajectory escapes the body: planning needs a closed orbit.";
                case OrbitPlanError.UnstableCurrentOrbit: return "Current periapsis is inside the atmosphere or below the surface: circularize first (Lift or Node tab).";
                case OrbitPlanError.PeBelowSurface: return "Target Pe is below the surface.";
                case OrbitPlanError.PeInAtmosphere: return "Target Pe is inside the atmosphere.";
                case OrbitPlanError.PeAboveAp: return "Target Pe cannot be higher than the target Ap.";
                case OrbitPlanError.InclinationOutOfRange: return "Target inclination must be between 0° and 180°.";
                default: return error.ToString();
            }
        }
    }
}
