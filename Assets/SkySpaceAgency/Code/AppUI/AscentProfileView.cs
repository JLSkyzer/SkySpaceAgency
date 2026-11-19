using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The Lift tab's ascent profile (altitude over downrange), drawn with Painter2D. The demo draws the
    /// lift mockup's curves; the coordinates are the mockup's (docs/design/ui/lift.svg, inner box
    /// x 480..1136, y 224..656 of the 1600 x 900 drawing), stretched to the element.
    /// </summary>
    public class AscentProfileView : VisualElement
    {
        static readonly Rect Box = new Rect(480, 224, 656, 432);

        readonly List<(Label label, Vector2 at, bool right)> _notes = new();

        public AscentProfileView()
        {
            AddToClassList("ssa-plot");
            name = "ascent-profile";
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            foreach (var (text, y) in new[] { ("100", 284f), ("70", 384f), ("35", 484f), ("0", 584f) })
                Note(text, new Vector2(492, y - 8), true, "ssa-plot__tick", null);
            Note("atmosphere 70 km", new Vector2(1126, 316), true, "ssa-plot__note", SsaPalette.AlertLight);
            Note("Ap 100 km", new Vector2(1110, 249), true, "ssa-plot__note", SsaPalette.GoldLight);
            Note("KSC Pad 1", new Vector2(510, 634), false, "ssa-plot__note", SsaPalette.Dim);
            RegisterCallback<GeometryChangedEvent>(_ => PlaceNotes());
        }

        void Note(string text, Vector2 at, bool right, string cls, Color? color)
        {
            var l = Ui.Label(text, cls);
            if (color.HasValue) l.style.color = color.Value;
            l.pickingMode = PickingMode.Ignore;
            Add(l);
            _notes.Add((l, at, right));
        }

        Vector2 Map(Rect r, float x, float y) =>
            new Vector2(r.x + (x - Box.x) / Box.width * r.width, r.y + (y - Box.y) / Box.height * r.height);

        void PlaceNotes()
        {
            Rect r = contentRect;
            foreach (var (label, at, right) in _notes)
            {
                Vector2 p = Map(r, at.x, at.y);
                label.style.top = p.y;
                if (right)
                {
                    label.style.left = StyleKeyword.Auto;
                    label.style.right = r.width - p.x;
                }
                else
                {
                    label.style.left = p.x;
                }
            }
        }

        static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
        {
            float u = 1 - t;
            return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        }

        // The mockup's predicted path: M 510 620 C 560 520, 640 380, 820 312 C 960 286, 1060 280, 1120 280.
        static List<Vector2> Predicted()
        {
            var pts = new List<Vector2>();
            for (int i = 0; i <= 40; i++)
                pts.Add(Bezier(new Vector2(510, 620), new Vector2(560, 520), new Vector2(640, 380), new Vector2(820, 312), i / 40f));
            for (int i = 1; i <= 40; i++)
                pts.Add(Bezier(new Vector2(820, 312), new Vector2(960, 286), new Vector2(1060, 280), new Vector2(1120, 280), i / 40f));
            return pts;
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10) return;
            float k = Mathf.Min(r.width / Box.width, r.height / Box.height);
            var p = ctx.painter2D;
            Vector2 M(float x, float y) => Map(r, x, y);
            Vector2 MV(Vector2 v) => Map(r, v.x, v.y);

            // Grid.
            p.strokeColor = new Color(SsaPalette.Cyan.r, SsaPalette.Cyan.g, SsaPalette.Cyan.b, 0.08f);
            p.lineWidth = 1;
            foreach (float y in new[] { 280f, 380f, 480f, 580f }) Line(p, M(500, y), M(1130, y));
            foreach (float x in new[] { 650f, 800f, 950f }) Line(p, M(x, 240), M(x, 620));

            // Atmosphere boundary.
            p.strokeColor = new Color(SsaPalette.Alert.r, SsaPalette.Alert.g, SsaPalette.Alert.b, 0.5f);
            Dashed(p, new List<Vector2> { M(500, 335), M(1130, 335) }, 8 * k, 8 * k);

            // Area under the predicted path, cyan fading out downwards (mockup: 35 % at the top, 0 at
            // the ground). Nested layers of the same faint cyan, each from the top down to a lower
            // edge: the higher a point, the more layers cover it. The path only climbs, so a layer is
            // the area right of the path, up to x = 1120.
            List<Vector2> path = Predicted();
            const int layers = 24;
            const float top = 280, ground = 620;
            p.fillColor = new Color(SsaPalette.Cyan.r, SsaPalette.Cyan.g, SsaPalette.Cyan.b, 1 - Mathf.Pow(0.65f, 1f / layers));
            for (int i = 1; i <= layers; i++)
            {
                float b = top + (ground - top) * i / layers;
                p.BeginPath();
                p.MoveTo(M(PathX(path, b), b));
                foreach (var v in path)
                    if (v.y < b) p.LineTo(MV(v));
                p.LineTo(M(1120, top));
                p.LineTo(M(1120, b));
                p.ClosePath();
                p.Fill();
            }

            // Predicted path, dashed cyan.
            p.strokeColor = SsaPalette.Cyan;
            p.lineWidth = 3 * k;
            var mapped = new List<Vector2>();
            foreach (var v in path) mapped.Add(MV(v));
            Dashed(p, mapped, 10 * k, 8 * k);

            // Flown so far, gold: M 510 620 C 540 560, 580 490, 640 440.
            p.strokeColor = SsaPalette.GoldLight;
            p.lineWidth = 5 * k;
            p.lineCap = LineCap.Round;
            p.BeginPath();
            p.MoveTo(M(510, 620));
            p.BezierCurveTo(M(540, 560), M(580, 490), M(640, 440));
            p.Stroke();

            // Ground and apoapsis.
            p.fillColor = SsaPalette.Ground;
            p.BeginPath();
            Vector2 g0 = M(500, 620), g1 = M(1130, 623);
            p.MoveTo(g0); p.LineTo(new Vector2(g1.x, g0.y)); p.LineTo(g1); p.LineTo(new Vector2(g0.x, g1.y));
            p.ClosePath();
            p.Fill();
            p.fillColor = SsaPalette.Gold;
            p.BeginPath();
            p.Arc(M(1120, 280), 7 * k, 0, 360);
            p.Fill();

            // The vessel: a night disc in a gold ring, the rocket turned 52 degrees along the path.
            Vector2 c = M(640, 440);
            p.fillColor = SsaPalette.Night;
            p.BeginPath(); p.Arc(c, 16 * k, 0, 360); p.Fill();
            p.strokeColor = SsaPalette.GoldLight;
            p.lineWidth = 3 * k;
            p.BeginPath(); p.Arc(c, 16 * k, 0, 360); p.Stroke();
            float turn = 52 * Mathf.Deg2Rad;
            Vector2 R(float x, float y) => c + new Vector2(x * Mathf.Cos(turn) - y * Mathf.Sin(turn), x * Mathf.Sin(turn) + y * Mathf.Cos(turn)) * k;
            p.fillColor = SsaPalette.Text;
            p.BeginPath();
            p.MoveTo(R(-4, 6)); p.LineTo(R(-4, -6)); p.QuadraticCurveTo(R(0, -13), R(4, -6)); p.LineTo(R(4, 6));
            p.ClosePath();
            p.Fill();
        }

        // Where the path (its y falls as x grows) is at height y; clamped to its ends.
        static float PathX(List<Vector2> path, float y)
        {
            if (y >= path[0].y) return path[0].x;
            for (int i = 1; i < path.Count; i++)
                if (path[i].y <= y)
                {
                    Vector2 u = path[i - 1], v = path[i];
                    float t = Mathf.Approximately(u.y, v.y) ? 0 : (u.y - y) / (u.y - v.y);
                    return Mathf.Lerp(u.x, v.x, t);
                }
            return path[path.Count - 1].x;
        }

        static void Line(Painter2D p, Vector2 a, Vector2 b)
        {
            p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke();
        }

        // A polyline in dashes of `dash` px separated by `gap` px.
        static void Dashed(Painter2D p, List<Vector2> pts, float dash, float gap)
        {
            float period = dash + gap, along = 0;
            p.BeginPath();
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i];
                float len = Vector2.Distance(a, b);
                float s = 0;
                while (s < len)
                {
                    float phase = (along + s) % period;
                    bool on = phase < dash;
                    float step = Mathf.Min(len - s, on ? dash - phase : period - phase);
                    if (on)
                    {
                        p.MoveTo(Vector2.Lerp(a, b, s / len));
                        p.LineTo(Vector2.Lerp(a, b, (s + step) / len));
                    }
                    s += Mathf.Max(step, 0.01f);
                }
                along += len;
            }
            p.Stroke();
        }
    }
}
