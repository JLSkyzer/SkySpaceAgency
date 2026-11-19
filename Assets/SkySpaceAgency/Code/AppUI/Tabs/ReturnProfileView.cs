using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The pure geometry of the Return profile drawing (docs/design/ui/return.svg), in a unit frame:
    /// x from 0 (left of the view) to 1, y from 0 (top) to 1. The points are the mockup's, measured on
    /// its 655 x 435 drawing area. Tested: the dashes stay on the path and cover its first half at most.
    /// </summary>
    public static class ReturnProfileGeometry
    {
        public static readonly Vector2 Pad = new Vector2(0.061f, 0.885f);
        public static readonly Vector2 Separation = new Vector2(0.427f, 0.154f);
        public static readonly Vector2 Flip = new Vector2(0.553f, 0.172f);
        public static readonly Vector2 EntryBurn = new Vector2(0.336f, 0.494f);
        public static readonly Vector2 LandingBurn = new Vector2(0.122f, 0.793f);
        public static readonly Vector2 UpperStage = new Vector2(0.992f, 0.03f);
        public const float Ground = 0.915f;

        /// <summary>The keep-out cone behind the upper stage at separation.</summary>
        public static readonly Vector2[] Cone = { Separation, new Vector2(0.511f, 0.039f), new Vector2(0.562f, 0.191f) };

        /// <summary>A cubic Bézier sampled into n + 1 points.</summary>
        public static List<Vector2> Cubic(Vector2 a, Vector2 b, Vector2 c, Vector2 d, int n, List<Vector2> into = null)
        {
            into ??= new List<Vector2>();
            for (int i = (into.Count > 0 ? 1 : 0); i <= n; i++)
            {
                float t = i / (float)n, u = 1 - t;
                into.Add(u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d);
            }
            return into;
        }

        /// <summary>The booster's ascent then the upper stage's path: pad to separation to beyond the view's top right.</summary>
        public static List<Vector2> Ascent()
        {
            var p = Cubic(Pad, new Vector2(0.12f, 0.55f), new Vector2(0.27f, 0.26f), Separation, 24);
            Cubic(Separation, new Vector2(0.58f, 0.04f), new Vector2(0.76f, -0.01f), UpperStage, 24, p);
            return p;
        }

        /// <summary>The booster's return: flip and boostback, then down through the entry and landing burns to the pad.</summary>
        public static List<Vector2> Return()
        {
            var p = Cubic(Separation, new Vector2(0.50f, 0.10f), new Vector2(0.58f, 0.10f), Flip, 12);
            Cubic(Flip, new Vector2(0.50f, 0.30f), new Vector2(0.42f, 0.40f), EntryBurn, 16, p);
            Cubic(EntryBurn, new Vector2(0.25f, 0.60f), new Vector2(0.17f, 0.72f), LandingBurn, 16, p);
            Cubic(LandingBurn, new Vector2(0.09f, 0.83f), new Vector2(0.075f, 0.86f), Pad, 8, p);
            return p;
        }

        /// <summary>
        /// The dashes of a polyline (dash and gap in the same units as the points): pairs of points,
        /// start then end of each dash, every one of them on the polyline.
        /// </summary>
        public static List<(Vector2 a, Vector2 b)> Dashes(IReadOnlyList<Vector2> line, float dash, float gap)
        {
            var dashes = new List<(Vector2, Vector2)>();
            if (line == null || line.Count < 2 || dash <= 0) return dashes;
            gap = Mathf.Max(0, gap);
            bool on = true;
            float left = dash;
            Vector2 start = line[0];
            for (int i = 1; i < line.Count; i++)
            {
                Vector2 a = line[i - 1], b = line[i];
                float len = Vector2.Distance(a, b);
                float pos = 0;
                while (len - pos > left)
                {
                    pos += left;
                    Vector2 q = Vector2.Lerp(a, b, pos / len);
                    if (on) dashes.Add((start, q));
                    else start = q;
                    on = !on;
                    left = on ? dash : gap;
                    if (left <= 0) { start = q; on = true; left = dash; }
                }
                left -= len - pos;
            }
            if (on && Vector2.Distance(start, line[line.Count - 1]) > 1e-6f) dashes.Add((start, line[line.Count - 1]));
            return dashes;
        }
    }

    /// <summary>
    /// The Return page's centre view: a side view of a booster recovery as the mockup draws it (ascent,
    /// separation and the keep-out cone, flip and boostback, the dashed way back with its entry and
    /// landing burns, the pad). There is no Return pilot yet, so it is an illustration of what the
    /// pilot will fly, not live data.
    /// </summary>
    public class ReturnProfileView : VisualElement
    {
        readonly List<Vector2> _ascent = ReturnProfileGeometry.Ascent();
        readonly List<Vector2> _return = ReturnProfileGeometry.Return();
        readonly List<(Label label, Vector2 at)> _labels = new();

        public ReturnProfileView()
        {
            AddToClassList("ssa-return-view");
            pickingMode = PickingMode.Ignore;
            Note("separation", ReturnProfileGeometry.Separation + new Vector2(-0.045f, -0.06f), "ssa-return-view__label--gold");
            Note("keep-out cone", new Vector2(0.58f, 0.0f), "ssa-return-view__label--alert");
            Note("flip + boostback", ReturnProfileGeometry.Flip + new Vector2(0.02f, -0.01f), "ssa-return-view__label--node");
            Note("entry burn", ReturnProfileGeometry.EntryBurn + new Vector2(0.022f, -0.01f), "ssa-return-view__label--gold");
            Note("landing burn", ReturnProfileGeometry.LandingBurn + new Vector2(0.022f, -0.02f), "ssa-return-view__label--gold");
            Note("upper stage", new Vector2(0.885f, 0.05f), "ssa-return-view__label--data");
            Note("pad", new Vector2(ReturnProfileGeometry.Pad.x + 0.012f, ReturnProfileGeometry.Ground + 0.05f), "ssa-return-view__label--node");
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => PlaceLabels());
        }

        public int LabelCount => _labels.Count;

        void Note(string text, Vector2 at, string kind)
        {
            var l = Ui.Label(text, "ssa-return-view__label", kind);
            l.pickingMode = PickingMode.Ignore;
            Add(l);
            _labels.Add((l, at));
        }

        void PlaceLabels()
        {
            Rect r = contentRect;
            foreach (var (label, at) in _labels)
            {
                label.style.left = r.x + at.x * r.width;
                label.style.top = r.y + at.y * r.height;
            }
        }

        Vector2 Map(Rect r, Vector2 u) => new Vector2(r.x + u.x * r.width, r.y + u.y * r.height);

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width <= 0 || r.height <= 0) return;
            var p = ctx.painter2D;
            float k = Mathf.Min(r.width / 655f, r.height / 435f);

            // Ground.
            p.strokeColor = SsaPalette.Ground;
            p.lineWidth = 3 * k;
            p.BeginPath();
            p.MoveTo(Map(r, new Vector2(0, ReturnProfileGeometry.Ground)));
            p.LineTo(Map(r, new Vector2(1, ReturnProfileGeometry.Ground)));
            p.Stroke();

            // Keep-out cone.
            p.fillColor = SsaPalette.Hex(0xff7a5a, 0.22f);
            p.strokeColor = SsaPalette.Hex(0xff7a5a, 0.55f);
            p.lineWidth = 1 * k;
            p.BeginPath();
            p.MoveTo(Map(r, ReturnProfileGeometry.Cone[0]));
            p.LineTo(Map(r, ReturnProfileGeometry.Cone[1]));
            p.LineTo(Map(r, ReturnProfileGeometry.Cone[2]));
            p.ClosePath();
            p.Fill();
            p.Stroke();

            // Ascent and the upper stage's way on.
            p.strokeColor = SsaPalette.Hex(0x5fb0d0);
            p.lineWidth = 2.5f * k;
            p.lineCap = LineCap.Round;
            p.BeginPath();
            p.MoveTo(Map(r, _ascent[0]));
            for (int i = 1; i < _ascent.Count; i++) p.LineTo(Map(r, _ascent[i]));
            p.Stroke();

            // The booster's way back, dashed, in view pixels.
            var px = new List<Vector2>(_return.Count);
            foreach (var u in _return) px.Add(Map(r, u));
            p.strokeColor = SsaPalette.GoldLight;
            p.lineWidth = 3 * k;
            p.BeginPath();
            foreach (var (a, b) in ReturnProfileGeometry.Dashes(px, 12 * k, 9 * k))
            {
                p.MoveTo(a);
                p.LineTo(b);
            }
            p.Stroke();

            // Pad: the landing ring and the booster.
            Vector2 pad = Map(r, new Vector2(ReturnProfileGeometry.Pad.x + 0.03f, ReturnProfileGeometry.Ground));
            p.strokeColor = SsaPalette.Cyan;
            p.lineWidth = 2.5f * k;
            Ellipse(p, pad, 34 * k, 8 * k);
            p.fillColor = SsaPalette.Text;
            Vector2 rocket = Map(r, ReturnProfileGeometry.Pad);
            p.BeginPath();
            p.MoveTo(rocket + new Vector2(-5, 10) * k);
            p.LineTo(rocket + new Vector2(-5, -6) * k);
            p.LineTo(rocket + new Vector2(0, -14) * k);
            p.LineTo(rocket + new Vector2(5, -6) * k);
            p.LineTo(rocket + new Vector2(5, 10) * k);
            p.ClosePath();
            p.Fill();

            // Events.
            Dot(p, Map(r, ReturnProfileGeometry.Separation), 8 * k, SsaPalette.Gold);
            Dot(p, Map(r, ReturnProfileGeometry.Flip), 7 * k, SsaPalette.Node);
            Dot(p, Map(r, ReturnProfileGeometry.EntryBurn), 7 * k, SsaPalette.Gold);
            Dot(p, Map(r, ReturnProfileGeometry.LandingBurn), 7 * k, SsaPalette.Gold);
        }

        static void Dot(Painter2D p, Vector2 c, float radius, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.Arc(c, radius, 0, 360);
            p.Fill();
        }

        static void Ellipse(Painter2D p, Vector2 c, float rx, float ry)
        {
            const int n = 32;
            p.BeginPath();
            for (int i = 0; i <= n; i++)
            {
                float a = i * 2 * Mathf.PI / n;
                Vector2 q = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
                if (i == 0) p.MoveTo(q); else p.LineTo(q);
            }
            p.Stroke();
        }
    }
}
