using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The Lift tab's ascent profile (docs/design/ui/lift.png): altitude over downrange, in km. The
    /// planned Guided curve dashed cyan over a cyan area, the flown path in gold, the atmosphere of the
    /// real body dashed coral, the target apoapsis as a gold dot, the vessel as a ringed disc. Drawn
    /// with Painter2D from the data last given to <see cref="SetData"/> (repainted only then and on a
    /// resize).
    /// </summary>
    public class LiftProfileView : VisualElement
    {
        // Inner plot box, in px from the element's edges (room for the ticks and the site label).
        const float PadLeft = 34, PadRight = 18, PadTop = 26, PadBottom = 30;

        readonly Label[] _ticks = new Label[4];
        readonly Label _atmo, _ap, _site, _message;

        List<Vector2> _planned = new();
        readonly List<Vector2> _flown = new();
        Vector2? _vessel;
        float _targetKm = 100, _atmoKm = -1;
        Vector2 _range = new Vector2(150, 112);

        public LiftProfileView()
        {
            AddToClassList("ssa-plot");
            AddToClassList("ssa-lift-profile");
            name = "lift-profile";
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            for (int i = 0; i < _ticks.Length; i++) _ticks[i] = Note("", "ssa-plot__tick", null);
            _atmo = Note("", "ssa-plot__note", SsaPalette.AlertLight);
            _ap = Note("", "ssa-plot__note", SsaPalette.GoldLight);
            _site = Note("", "ssa-plot__note", SsaPalette.Dim);
            _message = Note("", "ssa-plot__note", SsaPalette.Muted);
            _message.AddToClassList("ssa-lift-profile__message");
            RegisterCallback<GeometryChangedEvent>(_ => OnGeometry());
        }

        public IReadOnlyList<Vector2> Planned => _planned;
        public IReadOnlyList<Vector2> Flown => _flown;
        public Vector2? Vessel => _vessel;
        public Vector2 Range => _range;
        public string Message => _message.text;
        public string AtmosphereText => _atmo.text;

        Label Note(string text, string cls, Color? color)
        {
            var l = Ui.Label(text, cls);
            if (color.HasValue) l.style.color = color.Value;
            l.pickingMode = PickingMode.Ignore;
            Add(l);
            return l;
        }

        float Aspect
        {
            get
            {
                Rect r = contentRect;
                float w = r.width - PadLeft - PadRight, h = r.height - PadTop - PadBottom;
                return w > 10 && h > 10 ? w / h : 1.5f;
            }
        }

        /// <summary>
        /// The data to draw. guided: draw the planned curve (Gravity Turn has none: a note says so);
        /// atmosphereKm &lt;= 0: no atmosphere line; vessel: where the vessel is (null: not shown).
        /// </summary>
        public void SetData(bool guided, float startKm, float midKm, float endKm, float targetKm, float atmosphereKm,
            IReadOnlyList<Vector2> flown, Vector2? vessel, string site)
        {
            _targetKm = Mathf.Max(1, targetKm);
            _atmoKm = atmosphereKm;
            _flown.Clear();
            if (flown != null) _flown.AddRange(flown);
            _vessel = vessel;
            _guided = guided;
            _startKm = startKm;
            _midKm = midKm;
            _endKm = endKm;
            Recompute();
            _message.text = guided ? "" : "Gravity Turn has no fixed curve: the path draws as it flies";
            _site.text = site ?? "";
            _ap.text = "Ap " + Ui.Num(_targetKm, "0") + " km";
            _atmo.text = atmosphereKm > 0 && atmosphereKm < _range.y ? "atmosphere " + Ui.Num(atmosphereKm, "0") + " km" : "";
            int[] ticks = LiftProfileModel.Ticks(_targetKm, _atmoKm);
            for (int i = 0; i < _ticks.Length; i++) _ticks[i].text = ticks[i].ToString();
            Place();
            MarkDirtyRepaint();
        }

        bool _guided = true;
        float _startKm, _midKm, _endKm, _aspect = -1;

        // The ranges (and so the planned curve, cut at the right edge) follow the view's aspect.
        void Recompute()
        {
            _aspect = Aspect;
            _range = LiftProfileModel.Range(_targetKm, _flown, _vessel, _aspect);
            _planned = _guided
                ? LiftProfileModel.PlannedPath(_startKm, _midKm, _endKm, _targetKm, _range.x)
                : new List<Vector2>();
        }

        void OnGeometry()
        {
            if (Mathf.Abs(Aspect - _aspect) > 0.01f)
            {
                Recompute();
                _atmo.text = _atmoKm > 0 && _atmoKm < _range.y ? "atmosphere " + Ui.Num(_atmoKm, "0") + " km" : "";
                MarkDirtyRepaint();
            }
            Place();
        }

        Rect Box()
        {
            Rect r = contentRect;
            return new Rect(r.x + PadLeft, r.y + PadTop, Mathf.Max(1, r.width - PadLeft - PadRight), Mathf.Max(1, r.height - PadTop - PadBottom));
        }

        Vector2 Map(Rect b, Vector2 km) =>
            new Vector2(b.x + km.x / _range.x * b.width, b.yMax - km.y / _range.y * b.height);

        void Place()
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10) return;
            Rect b = Box();
            int[] ticks = LiftProfileModel.Ticks(_targetKm, _atmoKm);
            for (int i = 0; i < _ticks.Length; i++)
            {
                _ticks[i].style.left = b.x - 34;
                _ticks[i].style.top = Map(b, new Vector2(0, ticks[i])).y - 8;
            }
            Vector2 ap = ApPoint(b);
            _ap.style.right = Mathf.Max(0, r.width - ap.x + 4);
            _ap.style.top = ap.y - 24;
            float atmoY = Map(b, new Vector2(0, _atmoKm)).y;
            _atmo.style.right = PadRight;
            _atmo.style.top = atmoY + 4;
            _site.style.left = b.x + 6;
            _site.style.top = b.yMax + 8;
            _message.style.left = b.x + 12;
            _message.style.top = b.y + 8;
        }

        Vector2 ApPoint(Rect b)
        {
            // The curve's end when it reaches the target; else the target at the right edge (the
            // Guided curve, at 1 deg past its 5 deg altitude, often leaves the plot first).
            if (_planned.Count > 1 && _planned[_planned.Count - 1].y >= _targetKm * 0.99f)
                return Map(b, _planned[_planned.Count - 1]);
            return Map(b, new Vector2(_range.x * 0.98f, _targetKm));
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10) return;
            Rect b = Box();
            var p = ctx.painter2D;

            // Grid: the four altitude ticks, three downrange lines.
            p.strokeColor = new Color(SsaPalette.Cyan.r, SsaPalette.Cyan.g, SsaPalette.Cyan.b, 0.08f);
            p.lineWidth = 1;
            foreach (int t in LiftProfileModel.Ticks(_targetKm, _atmoKm))
            {
                float y = Map(b, new Vector2(0, t)).y;
                Line(p, new Vector2(b.x, y), new Vector2(b.xMax, y));
            }
            for (int i = 1; i <= 3; i++)
            {
                float x = b.x + b.width * i / 4;
                Line(p, new Vector2(x, b.y), new Vector2(x, b.yMax));
            }

            // Atmosphere of the real body.
            if (_atmoKm > 0 && _atmoKm < _range.y)
            {
                float y = Map(b, new Vector2(0, _atmoKm)).y;
                p.strokeColor = new Color(SsaPalette.Alert.r, SsaPalette.Alert.g, SsaPalette.Alert.b, 0.5f);
                Dashed(p, new List<Vector2> { new Vector2(b.x, y), new Vector2(b.xMax, y) }, 8, 8);
            }

            // Planned curve: the area under it, then the dashed line.
            if (_planned.Count > 1)
            {
                var mapped = new List<Vector2>(_planned.Count);
                foreach (var v in _planned) mapped.Add(Map(b, v));
                Vector2 end = mapped[mapped.Count - 1];
                p.fillColor = new Color(SsaPalette.Cyan.r, SsaPalette.Cyan.g, SsaPalette.Cyan.b, 0.16f);
                p.BeginPath();
                p.MoveTo(new Vector2(mapped[0].x, b.yMax));
                foreach (var v in mapped) p.LineTo(v);
                p.LineTo(new Vector2(end.x, b.yMax));
                p.ClosePath();
                p.Fill();
                p.strokeColor = SsaPalette.Cyan;
                p.lineWidth = 2.5f;
                Dashed(p, mapped, 9, 7);
            }
            p.fillColor = SsaPalette.Gold;
            p.BeginPath(); p.Arc(ApPoint(b), 6, 0, 360); p.Fill();

            // Flown path.
            if (_flown.Count > 1)
            {
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 4;
                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                p.MoveTo(Map(b, _flown[0]));
                for (int i = 1; i < _flown.Count; i++) p.LineTo(Map(b, _flown[i]));
                p.Stroke();
            }

            // Ground.
            p.fillColor = SsaPalette.Ground;
            p.BeginPath();
            p.MoveTo(new Vector2(b.x - 4, b.yMax)); p.LineTo(new Vector2(b.xMax + 4, b.yMax));
            p.LineTo(new Vector2(b.xMax + 4, b.yMax + 3)); p.LineTo(new Vector2(b.x - 4, b.yMax + 3));
            p.ClosePath();
            p.Fill();

            // The vessel: a night disc in a gold ring.
            if (_vessel.HasValue)
            {
                Vector2 c = Map(b, _vessel.Value);
                p.fillColor = SsaPalette.Night;
                p.BeginPath(); p.Arc(c, 12, 0, 360); p.Fill();
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 2.5f;
                p.BeginPath(); p.Arc(c, 12, 0, 360); p.Stroke();
                p.fillColor = SsaPalette.Text;
                p.BeginPath(); p.Arc(c, 4, 0, 360); p.Fill();
            }
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
                Vector2 a = pts[i - 1], c = pts[i];
                float len = Vector2.Distance(a, c);
                if (len < 1e-4f) continue;
                float s = 0;
                while (s < len)
                {
                    float phase = (along + s) % period;
                    bool on = phase < dash;
                    float step = Mathf.Min(len - s, on ? dash - phase : period - phase);
                    if (on)
                    {
                        p.MoveTo(Vector2.Lerp(a, c, s / len));
                        p.LineTo(Vector2.Lerp(a, c, (s + step) / len));
                    }
                    s += Mathf.Max(step, 0.01f);
                }
                along += len;
            }
            p.Stroke();
        }
    }
}
