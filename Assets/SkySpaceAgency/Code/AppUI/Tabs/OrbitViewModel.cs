using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using SkySpaceAgency.OrbitPlanning;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// What the Orbit tab's view draws (docs/design/ui/orbit.png), in drawn km on the view plane (radii
    /// shortened by DisplayRadius, directions kept): the body,
    /// the current orbit (or open trajectory), the orbit the plan ends on, a marker where each burn of
    /// the plan happens and the vessel. "Top view" is the view along the current orbit's normal, so the
    /// current orbit is seen true and a plane change shows as a flattened target. The burn positions
    /// come from the plan: the state is propagated from burn to burn (each burn's Δv added), as the
    /// planner itself does. Pure maths, tested in the EditMode tests (OrbitTabTests).
    /// </summary>
    public sealed class OrbitViewModel
    {
        public struct Marker
        {
            public Vector2 At;     // km, view plane
            public string Label;   // "1 · set Ap 250.0 km · 412 m/s"
        }

        public const int Samples = 160;

        public float BodyRadiusKm;   // drawn radius (see DisplayRadius)
        public readonly List<Vector2> Current = new();
        public bool CurrentClosed;
        public readonly List<Vector2> Target = new();
        public readonly List<Marker> Markers = new();
        public Vector2? Vessel;
        public string CurrentText = "";
        public string TargetText = "";
        /// <summary>Half the side of the square that holds everything, in km (the view fits it).</summary>
        public float ExtentKm;

        public bool IsEmpty => Current.Count == 0 && Target.Count == 0;

        // ---- Basis of the view plane ----

        public static void Basis(Vector3d normal, out Vector3d u, out Vector3d w)
        {
            Vector3d n = normal.magnitude > 1e-9 ? normal.normalized : OrbitMath.North;
            u = Vector3d.Cross(OrbitMath.North, n);
            if (u.magnitude < 1e-6)
                u = new Vector3d(1, 0, 0);
            u = u.normalized;
            w = Vector3d.Cross(n, u).normalized;
        }

        // The view is drawn by altitude, as the mockup: every radius loses BodyShrink x the body's radius
        // (directions kept), so the body is a smaller disc and the orbits stand clear of it. Without it a
        // low orbit hugs a planet that fills the view.
        public const double BodyShrink = 0.75;

        /// <summary>The drawn radius (m) of a point at radius r around a body of radius bodyRadius.</summary>
        public static double DisplayRadius(double r, double bodyRadius) =>
            Math.Max(r - BodyShrink * bodyRadius, 0.02 * bodyRadius);

        static Vector2 Project(Vector3d p, Vector3d u, Vector3d w, double bodyRadius)
        {
            double m = p.magnitude;
            if (m > 1e-9) p = p * (DisplayRadius(m, bodyRadius) / m);
            return new Vector2((float)(Vector3d.Dot(p, u) / 1000), (float)(Vector3d.Dot(p, w) / 1000));
        }

        /// <summary>
        /// Points of the conic of the state (r, v) around a body of gravitational parameter mu, in 3D
        /// (m). A closed orbit is sampled all round; an open one between its asymptotes, cut where the
        /// radius passes clipRadius.
        /// </summary>
        public static List<Vector3d> Conic(Vector3d r, Vector3d v, double mu, double clipRadius, int samples = Samples)
        {
            var points = new List<Vector3d>();
            var el = OrbitMath.Elements(r, v, mu);
            Vector3d h = el.AngularMomentum;
            if (h.magnitude < 1e-6 || mu <= 0)
                return points;
            Vector3d hn = h.normalized;
            double e = el.Eccentricity;
            Vector3d p = e > 1e-7 ? el.EccentricityVector.normalized : r.normalized;
            Vector3d q = Vector3d.Cross(hn, p).normalized;
            double semiLatus = h.magnitude * h.magnitude / mu;

            if (el.IsClosed)
            {
                for (int i = 0; i <= samples; i++)
                {
                    double nu = 2 * Math.PI * i / samples;
                    double rad = semiLatus / (1 + e * Math.Cos(nu));
                    points.Add(rad * (Math.Cos(nu) * p + Math.Sin(nu) * q));
                }
                return points;
            }

            // Open: true anomaly within the asymptotes, radius cut at clipRadius.
            double limit = e > 1 ? Math.Acos(-1 / e) : Math.PI;
            double nuMax = limit * 0.995;
            if (clipRadius > 0 && clipRadius > semiLatus / (1 + e))
            {
                // r(nu) = clip  =>  cos nu = (p / clip - 1) / e
                double c = (semiLatus / clipRadius - 1) / e;
                if (c > -1 && c < 1)
                    nuMax = Math.Min(nuMax, Math.Acos(c));
            }
            for (int i = 0; i <= samples; i++)
            {
                double nu = -nuMax + 2 * nuMax * i / samples;
                double rad = semiLatus / (1 + e * Math.Cos(nu));
                if (rad <= 0 || double.IsInfinity(rad) || double.IsNaN(rad))
                    continue;
                points.Add(rad * (Math.Cos(nu) * p + Math.Sin(nu) * q));
            }
            return points;
        }

        /// <summary>The state right before each burn of the plan, then the state after the last one.</summary>
        public static List<(Vector3d r, Vector3d v)> BurnStates(Vector3d r0, Vector3d v0, double ut0, double mu,
            IReadOnlyList<PlannedBurn> burns, out Vector3d rEnd, out Vector3d vEnd)
        {
            var states = new List<(Vector3d, Vector3d)>();
            Vector3d r = r0, v = v0;
            double ut = ut0;
            if (burns != null)
                foreach (var burn in burns)
                {
                    double dt = burn.UT - ut;
                    if (dt > 0)
                        KeplerPropagator.Propagate(r, v, mu, dt, out r, out v);
                    states.Add((r, v));
                    v = v + burn.DeltaV;
                    ut = burn.UT;
                }
            rEnd = r;
            vEnd = v;
            return states;
        }

        /// <summary>The view of the vessel's state (r, v at ut, relative to the body) and of a plan (may be null or refused).</summary>
        public static OrbitViewModel Build(BodyInfo body, Vector3d r, Vector3d v, double ut, OrbitPlan plan)
        {
            var model = new OrbitViewModel { BodyRadiusKm = (float)(DisplayRadius(body.Radius, body.Radius) / 1000) };
            double mu = body.Mu;
            if (mu <= 0 || r.magnitude < 1)
                return model;

            var current = OrbitMath.Elements(r, v, mu);
            Basis(current.AngularMomentum, out var u, out var w);
            model.CurrentClosed = current.IsClosed;
            model.CurrentText = "current " + OrbitText.Shape(current, body.Radius);

            var burns = plan != null && plan.Ok ? plan.Burns : null;
            var states = BurnStates(r, v, ut, mu, burns, out var rEnd, out var vEnd);
            bool hasTarget = burns != null && burns.Count > 0;
            OrbitElements final = hasTarget ? OrbitMath.Elements(rEnd, vEnd, mu) : default;

            // The cut of an open trajectory: beyond everything else that is drawn, within the SOI.
            double largest = Math.Max(r.magnitude, body.Radius * 1.5);
            if (current.IsClosed) largest = Math.Max(largest, current.ApoapsisRadius);
            if (hasTarget && final.IsClosed) largest = Math.Max(largest, final.ApoapsisRadius);
            foreach (var (rb, _) in states) largest = Math.Max(largest, rb.magnitude);
            double clip = largest * 1.6;
            if (body.SoiRadius > 0 && !double.IsInfinity(body.SoiRadius))
                clip = Math.Min(clip, body.SoiRadius);
            clip = Math.Max(clip, r.magnitude * 1.05);

            foreach (var p in Conic(r, v, mu, clip)) model.Current.Add(Project(p, u, w, body.Radius));
            if (hasTarget)
            {
                foreach (var p in Conic(rEnd, vEnd, mu, clip)) model.Target.Add(Project(p, u, w, body.Radius));
                model.TargetText = "target " + OrbitText.Shape(final, body.Radius);
                for (int i = 0; i < burns.Count; i++)
                    model.Markers.Add(new Marker
                    {
                        At = Project(states[i].r, u, w, body.Radius),
                        Label = OrbitText.MarkerLabel(i, burns[i])
                    });
            }
            model.Vessel = Project(r, u, w, body.Radius);

            float extent = model.BodyRadiusKm;
            foreach (var p in model.Current) extent = Mathf.Max(extent, Mathf.Abs(p.x), Mathf.Abs(p.y));
            foreach (var p in model.Target) extent = Mathf.Max(extent, Mathf.Abs(p.x), Mathf.Abs(p.y));
            model.ExtentKm = extent * 1.08f;
            return model;
        }

        // ---- The mockup's sample: Kerbin, 100 x 100 km at 0°, to 250 x 250 km at 45° in two burns ----

        // EXTERNAL DATA (KSP wiki, stock values): Kerbin.
        public static readonly BodyInfo SampleBody = new BodyInfo
        {
            Mu = 3.5316e12, Radius = 600000, AtmosphereDepth = 70000, SoiRadius = 84159286
        };

        /// <summary>
        /// The state and plan of the mockup: the vessel at the top of a circular 100 km orbit, burn 1
        /// a quarter turn before it comes back (right) raises Ap to 250 km, burn 2 at that apoapsis
        /// (left) circularizes and tilts the plane to 45°. Real geometry, made by propagation.
        /// </summary>
        public static void Sample(out Vector3d r, out Vector3d v, out double ut, out OrbitPlan plan)
        {
            double mu = SampleBody.Mu;
            double r1 = SampleBody.Radius + 100000, r2 = SampleBody.Radius + 250000;
            double vc = Math.Sqrt(mu / r1);
            r = new Vector3d(0, r1, 0);
            v = new Vector3d(-vc, 0, 0);
            ut = 0;
            double period = 2 * Math.PI * Math.Sqrt(r1 * r1 * r1 / mu);

            plan = new OrbitPlan();
            double t1 = 0.75 * period;
            KeplerPropagator.Propagate(r, v, mu, t1, out var rb1, out var vb1);
            double vPe = Math.Sqrt(mu * (2 / r1 - 2 / (r1 + r2)));
            Vector3d dv1 = (vPe - vb1.magnitude) * vb1.normalized;
            plan.Burns.Add(new PlannedBurn { UT = t1, DeltaV = dv1, Label = "Set Ap 250.0 km in 3 min" });

            double a = (r1 + r2) / 2;
            double t2 = t1 + Math.PI * Math.Sqrt(a * a * a / mu);
            KeplerPropagator.Propagate(rb1, vb1 + dv1, mu, t2 - t1, out var rb2, out var vb2);
            Vector3d vCirc = Math.Sqrt(mu / r2) * vb2.normalized;
            Vector3d vNew = OrbitMath.Rotate(vCirc, rb2.normalized, 45 * Math.PI / 180);
            plan.Burns.Add(new PlannedBurn { UT = t2, DeltaV = vNew - vb2, Label = "Set Pe 250.0 km at apoapsis" });
            plan.Final = OrbitMath.Elements(rb2, vNew, mu);
        }
    }

    /// <summary>The Orbit tab's texts (pure, tested).</summary>
    public static class OrbitText
    {
        static readonly NumberFormatInfo Spaced = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "." };

        /// <summary>412 m/s, 1 102 m/s (the mockups' thin grouping).</summary>
        public static string Dv(double metersPerSecond) =>
            Math.Round(metersPerSecond).ToString("#,0", Spaced) + " m/s";

        public static string Km(double km) => km.ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>"100 x 100 km, 0°" for a closed orbit, "open, Pe 35 km, 12°" for an open trajectory.</summary>
        public static string Shape(OrbitElements el, double bodyRadius)
        {
            string inc = (el.InclinationRad * 180 / Math.PI).ToString("0.#", CultureInfo.InvariantCulture) + "°";
            string pe = ((el.PeriapsisRadius - bodyRadius) / 1000).ToString("0", CultureInfo.InvariantCulture);
            if (!el.IsClosed)
                return $"open, Pe {pe} km, {inc}";
            string ap = ((el.ApoapsisRadius - bodyRadius) / 1000).ToString("0", CultureInfo.InvariantCulture);
            return $"{ap} x {pe} km, {inc}";
        }

        /// <summary>
        /// A burn's label without where it happens: "Set Ap 250.0 km at periapsis" → "Set Ap 250.0 km",
        /// "Capture at periapsis (120.0 km)" → "Capture", "Inclination 45.00° at ascending node" →
        /// "Inclination 45.00°" (the timeline's steps).
        /// </summary>
        public static string Short(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            int cut = label.Length;
            foreach (string sep in new[] { " at ", " in ", " as soon", " (" })
            {
                int i = label.IndexOf(sep, StringComparison.Ordinal);
                if (i > 0 && i < cut) cut = i;
            }
            return label.Substring(0, cut);
        }

        static string LowerFirst(string s) =>
            string.IsNullOrEmpty(s) ? "" : char.ToLowerInvariant(s[0]) + s.Substring(1);

        /// <summary>"1 · set Ap 250.0 km · 412 m/s" (the view's marker of burn i, from 0).</summary>
        public static string MarkerLabel(int index, PlannedBurn burn) =>
            $"{index + 1} · {LowerFirst(Short(burn.Label))} · {Dv(burn.DeltaV.magnitude)}";

        /// <summary>32:10, 1:05:12 (the period tile).</summary>
        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "—";
            var t = TimeSpan.FromSeconds(Math.Round(seconds));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }

        /// <summary>The classic tab's T+ time of a burn (00:12:30).</summary>
        public static string TPlus(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }

        /// <summary>The first sentence of a status line (the pill; the console keeps the whole text).</summary>
        public static string FirstSentence(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Trim();
            int i = text.IndexOf(". ", StringComparison.Ordinal);
            return i > 0 ? text.Substring(0, i + 1) : text;
        }
    }
}
