using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    public enum LandMarkerKind { Node, Gold, Muted }

    /// <summary>A labelled point of the descent profile (km to the touchdown, km of altitude).</summary>
    public struct LandMarker
    {
        public string Label;
        public Vector2 At;
        public LandMarkerKind Kind;
    }

    /// <summary>A point on the ground track: degrees, and km above the terrain.</summary>
    public struct LandGeoPoint
    {
        public double Lat, Lon, AltKm;
        public LandGeoPoint(double lat, double lon, double altKm) { Lat = lat; Lon = lon; AltKm = altKm; }
    }

    /// <summary>A predicted descent sample: a position (any frame, the same for every sample), UT, km above the terrain.</summary>
    public struct LandSample
    {
        public double X, Y, Z, UT, AltKm;
    }

    /// <summary>
    /// What the Land tab's descent profile draws (docs/design/ui/land.png): altitude over the ground
    /// distance still to fly to the predicted touchdown (x = 0, drawn on the right), the part flown
    /// (gold), the prediction (cyan, dashed), the atmosphere line, the peak heating band and the
    /// phase markers. Pure: the tab fills it from the pilot, the sample from the mockup. Tested in EditMode.
    /// </summary>
    public class LandProfileModel
    {
        public readonly List<Vector2> Flown = new();
        public readonly List<Vector2> Predicted = new();
        public readonly List<LandMarker> Markers = new();
        public float AtmosphereKm = float.NaN;
        public int HeatFrom = -1, HeatTo = -1;   // indices in Predicted
        public string HeatLabel;
        public Vector2? Vessel;
        public string TargetNote;
        /// <summary>The card's note: where the prediction comes from.</summary>
        public string Source = "";

        public bool HasData => Predicted.Count > 1 || Flown.Count > 1;

        // Where x = 0 (the touchdown) and x = XMax sit across the box, and the ground and YMax down it.
        public const float RightAt = 0.854f, LeftAt = 0.015f, GroundAt = 0.866f, TopAt = 0.04f;

        public float XMax
        {
            get
            {
                float m = 1e-3f;
                foreach (var p in Flown) m = Mathf.Max(m, p.x);
                foreach (var p in Predicted) m = Mathf.Max(m, p.x);
                if (Vessel.HasValue) m = Mathf.Max(m, Vessel.Value.x);
                return m;
            }
        }

        public float YMax
        {
            get
            {
                float m = 1e-3f;
                foreach (var p in Flown) m = Mathf.Max(m, p.y);
                foreach (var p in Predicted) m = Mathf.Max(m, p.y);
                if (Vessel.HasValue) m = Mathf.Max(m, Vessel.Value.y);
                if (!float.IsNaN(AtmosphereKm) && AtmosphereKm < m * 3) m = Mathf.Max(m, AtmosphereKm * 1.15f);
                return m * 1.04f;
            }
        }

        /// <summary>A data point (km to go, km of altitude) in the box, with the touchdown on the right.</summary>
        public static Vector2 Map(Rect box, float xMax, float yMax, Vector2 p)
        {
            float fx = xMax > 0 ? Mathf.Clamp01(p.x / xMax) : 0;
            float fy = yMax > 0 ? Mathf.Clamp(p.y / yMax, 0, 1.2f) : 0;
            return new Vector2(box.x + box.width * (RightAt - (RightAt - LeftAt) * fx),
                box.y + box.height * (GroundAt - (GroundAt - TopAt) * fy));
        }

        // ---- helpers ----

        public static double GreatCircleKm(double lat1, double lon1, double lat2, double lon2, double radiusM)
        {
            const double d = Math.PI / 180;
            double dLat = (lat2 - lat1) * d, dLon = (lon2 - lon1) * d;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * d) * Math.Cos(lat2 * d) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * radiusM * Math.Asin(Math.Min(1, Math.Sqrt(a))) / 1000;
        }

        static double Angle(LandSample a, LandSample b)
        {
            double la = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z), lb = Math.Sqrt(b.X * b.X + b.Y * b.Y + b.Z * b.Z);
            if (!(la > 0) || !(lb > 0)) return 0;
            double c = (a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb);
            return Math.Acos(Math.Max(-1, Math.Min(1, c)));
        }

        static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
            float u = 1 - t;
            return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        }

        /// <summary>The first point of a descending path at an altitude (linear between points); null when never crossed.</summary>
        public static Vector2? PointAtAltitude(IReadOnlyList<Vector2> path, float altKm)
        {
            for (int i = 1; i < path.Count; i++)
            {
                Vector2 a = path[i - 1], b = path[i];
                if ((a.y - altKm) * (b.y - altKm) <= 0 && !Mathf.Approximately(a.y, b.y))
                    return Vector2.Lerp(a, b, (a.y - altKm) / (a.y - b.y));
            }
            return null;
        }

        /// <summary>The point at a fraction of the path's points (0: the first, 1: the last), linear between them.</summary>
        public static Vector2 PointAtFraction(IReadOnlyList<Vector2> path, float t)
        {
            if (path.Count == 0) return Vector2.zero;
            if (path.Count == 1) return path[0];
            float f = Mathf.Clamp01(t) * (path.Count - 1);
            int i = Mathf.Min(path.Count - 2, Mathf.FloorToInt(f));
            return Vector2.Lerp(path[i], path[i + 1], f - i);
        }

        /// <summary>The points of the flown track, in km to go to the end point.</summary>
        public void SetFlown(IReadOnlyList<LandGeoPoint> track, LandGeoPoint end, double radiusM)
        {
            Flown.Clear();
            if (track == null) return;
            foreach (var p in track)
                Flown.Add(new Vector2((float)GreatCircleKm(p.Lat, p.Lon, end.Lat, end.Lon, radiusM), (float)Math.Max(0, p.AltKm)));
        }

        /// <summary>
        /// No sampled prediction: the descent from the vessel to the predicted touchdown, as a smooth
        /// curve (its two ends are the real data, the shape between is drawn, not predicted). Its 41
        /// points are evenly spaced in the curve's parameter (PointAtFraction places a marker by time).
        /// </summary>
        public void SetPredictedCurve(Vector2 now)
        {
            Predicted.Clear();
            var end = new Vector2(0, 0);
            var c1 = new Vector2(now.x * 0.72f, now.y * 0.96f);
            var c2 = new Vector2(now.x * 0.28f, now.y * 0.32f);
            for (int i = 0; i <= 40; i++)
                Predicted.Add(Bezier(now, c1, c2, end, i / 40f));
        }

        /// <summary>
        /// A sampled prediction (the planned descent's samples, the touchdown last): x is the arc to the
        /// last sample times the radius. Samples before fromUT are left out. Returns the UT of each kept point.
        /// </summary>
        public List<double> SetPredictedSamples(IReadOnlyList<LandSample> samples, double radiusM, double fromUT)
        {
            Predicted.Clear();
            var uts = new List<double>();
            if (samples == null || samples.Count < 2) return uts;
            LandSample last = samples[samples.Count - 1];
            foreach (var s in samples)
            {
                if (s.UT < fromUT && s.UT < last.UT) continue;
                Predicted.Add(new Vector2((float)(Angle(s, last) * radiusM / 1000), (float)Math.Max(0, s.AltKm)));
                uts.Add(s.UT);
            }
            return uts;
        }

        /// <summary>The point of the prediction at a UT, from the UTs SetPredictedSamples returned; null outside.</summary>
        public Vector2? PointAtUT(IReadOnlyList<double> uts, double ut)
        {
            if (uts == null || uts.Count != Predicted.Count || uts.Count < 2) return null;
            if (ut < uts[0] || ut > uts[uts.Count - 1]) return null;
            for (int i = 1; i < uts.Count; i++)
                if (uts[i] >= ut)
                {
                    double span = uts[i] - uts[i - 1];
                    float t = span > 0 ? (float)((ut - uts[i - 1]) / span) : 0;
                    return Vector2.Lerp(Predicted[i - 1], Predicted[i], t);
                }
            return Predicted[Predicted.Count - 1];
        }

        public void AddMarker(string label, Vector2 at, LandMarkerKind kind) =>
            Markers.Add(new LandMarker { Label = label, At = at, Kind = kind });

        public void Clear()
        {
            Flown.Clear(); Predicted.Clear(); Markers.Clear();
            AtmosphereKm = float.NaN; HeatFrom = HeatTo = -1; HeatLabel = null; Vessel = null; TargetNote = null; Source = "";
        }

        // ---- the mockup (docs/design/ui/land.svg), in data units: x = 1040 - svg x (km), y = (610 - svg y) / 5 (km) ----

        public static LandProfileModel Sample()
        {
            var m = new LandProfileModel { AtmosphereKm = 50, Source = "predicted with drag and heating" };
            // M 490 250 C 560 258, 640 318, 700 372 C 780 440, 900 522, 1040 600 (ends on the ground).
            var p0 = new Vector2(550, 72); var p1 = new Vector2(340, 47.6f); var p2 = new Vector2(0, 0);
            for (int i = 0; i <= 30; i++) m.Predicted.Add(Bezier(p0, new Vector2(480, 70.4f), new Vector2(400, 58.4f), p1, i / 30f));
            for (int i = 1; i <= 30; i++) m.Predicted.Add(Bezier(p1, new Vector2(260, 34), new Vector2(140, 17.6f), p2, i / 30f));
            // Flown: M 490 250 C 520 252, 545 258, 570 266.
            for (int i = 0; i <= 12; i++) m.Flown.Add(Bezier(p0, new Vector2(520, 71.6f), new Vector2(495, 70.4f), new Vector2(470, 68.8f), i / 12f));
            // Peak heating: 708,380 to 850,478 on the path.
            m.HeatFrom = NearestIndex(m.Predicted, new Vector2(332, 46));
            m.HeatTo = NearestIndex(m.Predicted, new Vector2(190, 26.4f));
            m.HeatLabel = "peak heating · 61 %";
            m.AddMarker("Deorbit · 68 m/s", new Vector2(470, 68.8f), LandMarkerKind.Node);
            m.AddMarker("Pre-brake · 140 m/s", new Vector2(368, 52.6f), LandMarkerKind.Gold);
            m.AddMarker("Drop stage 1 · 2.4 km", new Vector2(140, 18), LandMarkerKind.Muted);
            m.AddMarker("Brake", new Vector2(72, 10), LandMarkerKind.Gold);
            m.TargetNote = "target · predicted 0.4 km off";
            return m;
        }

        public static int NearestIndex(IReadOnlyList<Vector2> path, Vector2 p)
        {
            int best = -1;
            float d = float.MaxValue;
            for (int i = 0; i < path.Count; i++)
            {
                float e = (path[i] - p).sqrMagnitude;
                if (e < d) { d = e; best = i; }
            }
            return best;
        }
    }
}
