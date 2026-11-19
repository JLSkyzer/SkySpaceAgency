using System;
using System.Collections.Generic;
using SkySpaceAgency.Rendezvous;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The Rendezvous tab's phase timeline, from RendezvousPilot's real phases (RvPhase, then the
    /// docking guidance's DockPhase). Pure, tested in EditMode.
    /// </summary>
    public static class RendezvousTabPhases
    {
        public const string Transfer = "Transfer";

        // Rendezvous + dock: the plan, its burns (Phasing and Intercept are one burn, RendezvousFlight.PhaseOf),
        // the coast (PhasingWait, with the correction), the brake at the 200 m stand-off, the guidance's holds.
        public static readonly string[] Full = { "Plan", "Plane match", "Intercept", "Coast", "Brake 200 m", "Hold 50 m", "Hold 10 m", "Docked" };
        public static readonly string[] RendezvousOnly = { "Plan", "Plane match", "Intercept", "Coast", "Brake 200 m", "Done" };
        public static readonly string[] DockOnly = { "Align", "Hold 50 m", "Hold 10 m", "Docked" };

        /// <summary>The steps of a run in this mode; chain: the Transfer pilot's leg first (4.5).</summary>
        public static IReadOnlyList<string> Steps(RvMode mode, bool chain)
        {
            string[] steps = mode == RvMode.DockOnly ? DockOnly : mode == RvMode.RendezvousOnly ? RendezvousOnly : Full;
            if (!chain) return steps;
            var list = new List<string>(steps.Length + 1) { Transfer };
            list.AddRange(steps);
            return list;
        }

        /// <summary>
        /// The current step for a phase (and the guidance's phase while docking), without the chain's
        /// leading step; -1 when the phase says nothing new (Failed, Aborted: keep the last one).
        /// </summary>
        public static int Index(RvMode mode, RvPhase phase, DockPhase dock)
        {
            if (mode == RvMode.DockOnly)
            {
                switch (phase)
                {
                    case RvPhase.Docked: return 3;
                    case RvPhase.Failed: return -1;
                    case RvPhase.Dock: return dock == DockPhase.Align ? 0 : DockIndex(dock, 1);
                    default: return 0;
                }
            }
            int last = mode == RvMode.RendezvousOnly ? RendezvousOnly.Length - 1 : Full.Length - 1;
            switch (phase)
            {
                case RvPhase.Plan: return 0;
                case RvPhase.PlaneMatch: return 1;
                case RvPhase.Intercept: return 2;
                case RvPhase.PhasingWait:
                case RvPhase.Correction: return 3;
                case RvPhase.Brake: return 4;
                case RvPhase.Dock: return mode == RvMode.RendezvousOnly ? last : DockIndex(dock, 5);
                case RvPhase.Docked:
                case RvPhase.Done: return last;
                default: return -1;
            }
        }

        // Align and the back-off lead to the 50 m hold; the 10 m hold and the final approach to the contact.
        static int DockIndex(DockPhase dock, int hold50)
        {
            switch (dock)
            {
                case DockPhase.Align:
                case DockPhase.Hold50:
                case DockPhase.Backoff: return hold50;
                case DockPhase.Approach10:
                case DockPhase.Hold10:
                case DockPhase.Final: return hold50 + 1;
                case DockPhase.Docked: return hold50 + 2;
                default: return -1;
            }
        }

        /// <summary>The main button's label while the pilot flies ("Docking…", "Planning…", "Running…").</summary>
        public static string RunningLabel(bool planning, bool docking, bool chainLeg) =>
            planning ? "Planning…" : docking ? "Docking…" : chainLeg ? "Transfer…" : "Running…";
    }

    /// <summary>
    /// The approach view's geometry, in the mockup's pixels (docs/design/ui/rendezvous.svg: the
    /// target at 808, 440; rings 10 / 50 / 200 m at radii 70 / 150 / 230; the corridor to x = 1100,
    /// ±80). The scale is piecewise linear between the rings, then logarithmic up to 200 km.
    /// </summary>
    public static class ApproachGeometry
    {
        public const float R10 = 70, R50 = 150, R200 = 230, RMax = 290;
        public const float CorridorLength = 292, CorridorHalfWidth = 80;

        public static float Radius(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return RMax;
            if (metres <= 0) return 0;
            if (metres <= 10) return (float)(R10 * metres / 10);
            if (metres <= 50) return (float)(R10 + (metres - 10) * (R50 - R10) / 40);
            if (metres <= 200) return (float)(R50 + (metres - 50) * (R200 - R50) / 150);
            return (float)(R200 + (RMax - R200) * Math.Min(1, Math.Log10(metres / 200) / 3));
        }

        /// <summary>True beyond the outer ring: the view shows the distance next to the vessel.</summary>
        public static bool OffScale(double metres) => !(metres <= 200);

        /// <summary>
        /// Our vessel's place from the target, in mockup pixels (x right, y down): at Radius(distance),
        /// angle from the axis (radians, positive up).
        /// </summary>
        public static Vector2 Place(double distance, double angle)
        {
            float r = Radius(distance);
            return new Vector2(r * (float)Math.Cos(angle), -r * (float)Math.Sin(angle));
        }

        /// <summary>
        /// The angle from the port axis while docking: the pilot gives the distance to the port face
        /// and the lateral error (a magnitude), so the vessel is drawn above the axis.
        /// </summary>
        public static double PortAngle(double distance, double lateral)
        {
            if (double.IsNaN(distance) || distance <= 0 || double.IsNaN(lateral)) return 0;
            double l = Math.Min(Math.Abs(lateral), distance);
            return Math.Atan2(l, Math.Sqrt(Math.Max(0, distance * distance - l * l)));
        }

        /// <summary>Inside the approach corridor (the cone along the port axis).</summary>
        public static bool InCorridor(double distance, double angle) =>
            !double.IsNaN(distance) && Math.Abs(angle) <= Math.Atan2(CorridorHalfWidth, CorridorLength);
    }

    /// <summary>One line of the CHECKS list (a dot and a sentence).</summary>
    public struct RendezvousCheck
    {
        public StatusKind Kind;
        public string Text;

        public RendezvousCheck(StatusKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    /// <summary>The CHECKS lines from what the tab knows (pure).</summary>
    public static class RendezvousChecks
    {
        public const string NeverCommanded = "Target never commanded";

        /// <param name="portHint">the classic tab's port line: null or "" when no target.</param>
        /// <param name="rcsTotal">RCS thrusters on our vessel, -1 when unknown.</param>
        public static List<RendezvousCheck> Build(bool hasTarget, bool far, string portHint, bool portsOk,
            int rcsTotal, int rcsUsable, string budgetLine, bool budgetOk, string translateLine, string abortLine)
        {
            var list = new List<RendezvousCheck> { new RendezvousCheck(StatusKind.Ok, NeverCommanded) };
            if (!hasTarget) return list;
            if (far)
                list.Add(new RendezvousCheck(StatusKind.Info, "Target around another body: transfer first"));
            if (!string.IsNullOrEmpty(portHint))
                list.Add(new RendezvousCheck(portsOk ? StatusKind.Ok : StatusKind.Alert, portHint));
            if (rcsTotal == 0)
                list.Add(new RendezvousCheck(StatusKind.Info, "No RCS: the main engine docks, less precise"));
            else if (rcsTotal > 0)
                list.Add(new RendezvousCheck(rcsUsable > 0 ? StatusKind.Ok : StatusKind.Alert,
                    rcsUsable > 0 ? $"RCS: {rcsUsable} of {rcsTotal} thruster(s) usable" : $"RCS: none of {rcsTotal} thruster(s) usable (RCS off or no fuel)"));
            if (!string.IsNullOrEmpty(budgetLine))
                list.Add(new RendezvousCheck(budgetOk ? StatusKind.Ok : StatusKind.Alert, budgetLine));
            if (!string.IsNullOrEmpty(translateLine))
                list.Add(new RendezvousCheck(translateLine.Contains("refused") ? StatusKind.Alert : StatusKind.Ok, translateLine));
            if (!string.IsNullOrEmpty(abortLine))
                list.Add(new RendezvousCheck(StatusKind.Alert, abortLine));
            return list;
        }

        /// <summary>The fraction left of a resource, or NaN when the vessel carries none.</summary>
        public static float Fraction(double stored, double capacity) =>
            capacity > 1e-9 && !double.IsNaN(stored) ? Mathf.Clamp01((float)(stored / capacity)) : float.NaN;
    }
}
