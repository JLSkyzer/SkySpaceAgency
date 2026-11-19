using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The Orbit tab's centre view (docs/design/ui/orbit.png), drawn with Painter2D from an
    /// OrbitViewModel: the body, the current orbit dashed cyan, the orbit the plan ends on in gold, a
    /// blue marker per planned burn with its label, the vessel, and the two shape lines at the bottom.
    /// Repainted only when a new model is set.
    /// </summary>
    public class OrbitView : VisualElement
    {
        // Size of the mockup's drawing area, in USS pixels (mockup pixels x 0.85): line widths and
        // marker sizes scale with the element from it.
        const float MockupWidth = 558, MockupHeight = 366;
        const float BottomBand = 26;

        static readonly Color NodeText = SsaPalette.Hex(0x8fc4ff);

        OrbitViewModel _model = new OrbitViewModel();
        readonly List<Label> _markerLabels = new();
        readonly Label _current, _target, _empty;

        public OrbitViewModel Model => _model;
        public IReadOnlyList<Label> MarkerLabels => _markerLabels;

        public OrbitView()
        {
            AddToClassList("ssa-plot");
            AddToClassList("ssa-orbit__view");
            name = "orbit-view";
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            _current = Ui.Label("", "ssa-orbit__shape", "ssa-orbit__shape--current");
            _target = Ui.Label("", "ssa-orbit__shape", "ssa-orbit__shape--target");
            _empty = Ui.Label("", "ssa-orbit__empty");
            foreach (var l in new[] { _current, _target, _empty }) { l.pickingMode = PickingMode.Ignore; Add(l); }
            RegisterCallback<GeometryChangedEvent>(_ => PlaceLabels());
            SetModel(null, "No orbit to show: be in flight.");
        }

        /// <summary>Shows a model (null: nothing but the message).</summary>
        public void SetModel(OrbitViewModel model, string emptyMessage = null)
        {
            _model = model ?? new OrbitViewModel();
            _current.text = _model.CurrentText ?? "";
            _target.text = _model.TargetText ?? "";
            _empty.text = _model.IsEmpty ? emptyMessage ?? "" : "";
            _empty.style.display = string.IsNullOrEmpty(_empty.text) ? DisplayStyle.None : DisplayStyle.Flex;
            while (_markerLabels.Count < _model.Markers.Count)
            {
                var l = Ui.Label("", "ssa-orbit__marker-label");
                l.style.color = NodeText;
                l.pickingMode = PickingMode.Ignore;
                Add(l);
                _markerLabels.Add(l);
            }
            for (int i = 0; i < _markerLabels.Count; i++)
            {
                bool shown = i < _model.Markers.Count;
                _markerLabels[i].style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
                if (shown) _markerLabels[i].text = _model.Markers[i].Label;
            }
            PlaceLabels();
            MarkDirtyRepaint();
        }

        // ---- Mapping ----

        Rect Plot(Rect r) => new Rect(r.x, r.y, r.width, Mathf.Max(10, r.height - BottomBand));

        float PxPerKm(Rect plot) =>
            _model.ExtentKm <= 0 ? 0 : Mathf.Min(plot.width, plot.height) / 2f / _model.ExtentKm;

        Vector2 Map(Rect plot, float scale, Vector2 km) =>
            new Vector2(plot.center.x + km.x * scale, plot.center.y - km.y * scale);

        static float Size(Rect r) => Mathf.Clamp(Mathf.Min(r.width / MockupWidth, r.height / MockupHeight), 0.5f, 1.6f) * 0.85f;

        void PlaceLabels()
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10) return;
            Rect plot = Plot(r);
            float scale = PxPerKm(plot);
            for (int i = 0; i < _model.Markers.Count && i < _markerLabels.Count; i++)
            {
                Vector2 p = Map(plot, scale, _model.Markers[i].At);
                var l = _markerLabels[i];
                // Right half: the label on the marker's right; left half: above it, centred.
                if (p.x >= plot.center.x)
                {
                    l.style.left = p.x + 16 * Size(r) / 0.85f;
                    l.style.top = p.y - 8;
                    l.style.translate = new Translate(0, 0);
                }
                else
                {
                    l.style.left = p.x;
                    l.style.top = p.y - 32 * Size(r) / 0.85f;
                    l.style.translate = new Translate(Length.Percent(-50), 0);
                }
            }
        }

        // ---- Drawing ----

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10 || _model.ExtentKm <= 0) return;
            Rect plot = Plot(r);
            float scale = PxPerKm(plot);
            float k = Size(r);
            var p = ctx.painter2D;
            Vector2 M(Vector2 km) => Map(plot, scale, km);

            // The body: a blue disc, lit from the upper left.
            Vector2 c = plot.center;
            float rad = Mathf.Max(3, _model.BodyRadiusKm * scale);
            Disc(p, c, rad, SsaPalette.Hex(0x1b5f96));
            Disc(p, c + new Vector2(-0.12f, -0.12f) * rad, rad * 0.78f, SsaPalette.Hex(0x2a86bf, 0.85f));
            Disc(p, c + new Vector2(-0.22f, -0.22f) * rad, rad * 0.5f, SsaPalette.Hex(0x47b4dc, 0.7f));
            Disc(p, c + new Vector2(-0.3f, -0.3f) * rad, rad * 0.22f, SsaPalette.Hex(0x6fd2ec, 0.6f));

            // Current orbit, dashed cyan.
            if (_model.Current.Count > 1)
            {
                var pts = new List<Vector2>(_model.Current.Count);
                foreach (var v in _model.Current) pts.Add(M(v));
                p.strokeColor = SsaPalette.Cyan;
                p.lineWidth = 3 * k;
                p.lineCap = LineCap.Butt;
                Dashed(p, pts, 10 * k, 8 * k);
            }

            // Target orbit, gold.
            if (_model.Target.Count > 1)
            {
                p.strokeColor = SsaPalette.Gold;
                p.lineWidth = 5 * k;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                p.MoveTo(M(_model.Target[0]));
                for (int i = 1; i < _model.Target.Count; i++) p.LineTo(M(_model.Target[i]));
                p.Stroke();
            }

            // Planned burns: blue rings with a dot.
            foreach (var m in _model.Markers)
            {
                Vector2 at = M(m.At);
                Disc(p, at, 11 * k, SsaPalette.Night);
                p.strokeColor = SsaPalette.Node;
                p.lineWidth = 4 * k;
                p.BeginPath(); p.Arc(at, 11 * k, 0, 360); p.Stroke();
                Disc(p, at, 4 * k, SsaPalette.Node);
            }

            // The vessel: a night disc in a gold ring with a small rocket.
            if (_model.Vessel.HasValue)
            {
                Vector2 at = M(_model.Vessel.Value);
                Disc(p, at, 12 * k, SsaPalette.Night);
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 3 * k;
                p.BeginPath(); p.Arc(at, 12 * k, 0, 360); p.Stroke();
                p.fillColor = Color.white;
                p.BeginPath();
                p.MoveTo(at + new Vector2(-3, 5) * k);
                p.LineTo(at + new Vector2(-3, -5) * k);
                p.QuadraticCurveTo(at + new Vector2(0, -10) * k, at + new Vector2(3, -5) * k);
                p.LineTo(at + new Vector2(3, 5) * k);
                p.ClosePath();
                p.Fill();
            }
        }

        static void Disc(Painter2D p, Vector2 c, float r, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.Arc(c, r, 0, 360);
            p.Fill();
        }

        // A polyline in dashes of `dash` px separated by `gap` px (as AscentProfileView).
        static void Dashed(Painter2D p, List<Vector2> pts, float dash, float gap)
        {
            float period = dash + gap, along = 0;
            p.BeginPath();
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i];
                float len = Vector2.Distance(a, b);
                if (len < 1e-4f) continue;
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
