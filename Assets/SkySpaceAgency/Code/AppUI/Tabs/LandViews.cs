using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>The Land tab's colours beyond SsaPalette (docs/design/ui/land.svg).</summary>
    public static class LandPalette
    {
        public static readonly Color Dusk = SsaPalette.Hex(0x3a2430);
        public static readonly Color Soil = SsaPalette.Hex(0xb5543a);
        public static readonly Color HeatRed = SsaPalette.Hex(0xff5a3a);
        public static readonly Color NodeLight = SsaPalette.Hex(0x8fd3ff);
        public static readonly Color MutedLight = SsaPalette.Hex(0xc4d0ef);

        static Texture2D _sky, _heat;

        /// <summary>The descent profile's card: night at the top to a dusky red at the bottom.</summary>
        public static Texture2D DuskGradient => _sky ??= Make(1, 64, (x, y) => Color.Lerp(Dusk, SsaPalette.Night, y));

        /// <summary>The hottest part's bar: cyan, gold at 60 %, red at the end.</summary>
        public static Texture2D HeatGradient => _heat ??= Make(64, 1, (x, y) =>
            x < 0.6f ? Color.Lerp(SsaPalette.Cyan, SsaPalette.Gold, x / 0.6f) : Color.Lerp(SsaPalette.Gold, HeatRed, (x - 0.6f) / 0.4f));

        // f(x, y) with x and y from 0 to 1 (y = 0 is the bottom row).
        static Texture2D Make(int w, int h, System.Func<float, float, Color> f)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "ssa-land-gradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    t.SetPixel(x, y, f(w > 1 ? x / (float)(w - 1) : 0, h > 1 ? y / (float)(h - 1) : 0));
            t.Apply(false, false);
            return t;
        }
    }

    /// <summary>
    /// The descent profile (Painter2D): altitude over the distance to the predicted touchdown, the
    /// touchdown on the right. Draws a LandProfileModel; SetModel repaints, nothing else does.
    /// </summary>
    public class LandProfileView : VisualElement
    {
        readonly List<Label> _pool = new();
        readonly List<(Label label, Vector2 at, int align)> _placed = new();   // align: 0 left, 1 right, 2 centre
        readonly Label _empty;

        public LandProfileModel Model { get; private set; } = new LandProfileModel();

        public LandProfileView()
        {
            AddToClassList("ssa-plot");
            AddToClassList("ssa-land-profile");
            name = "descent-profile";
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            _empty = Ui.Label("", "ssa-land-profile__empty");
            _empty.pickingMode = PickingMode.Ignore;
            Add(_empty);
            RegisterCallback<GeometryChangedEvent>(_ => PlaceLabels());
            SetModel(Model);
        }

        public int LabelCount => _placed.Count;

        public void SetModel(LandProfileModel model, string emptyText = null)
        {
            Model = model ?? new LandProfileModel();
            _empty.text = Model.HasData ? "" : emptyText ?? "No descent prediction yet.";
            _empty.style.display = Model.HasData ? DisplayStyle.None : DisplayStyle.Flex;
            BuildLabels();
            PlaceLabels();
            MarkDirtyRepaint();
        }

        Label Take(int i)
        {
            while (_pool.Count <= i)
            {
                var l = Ui.Label("", "ssa-plot__note");
                l.pickingMode = PickingMode.Ignore;
                Add(l);
                _pool.Add(l);
            }
            return _pool[i];
        }

        void BuildLabels()
        {
            _placed.Clear();
            var m = Model;
            int n = 0;
            void Put(string text, Vector2 at, int align, Color color)
            {
                var l = Take(n++);
                l.text = text;
                l.style.color = color;
                l.style.display = DisplayStyle.Flex;
                _placed.Add((l, at, align));
            }
            if (m.HasData)
            {
                if (!float.IsNaN(m.AtmosphereKm))
                    Put("atmosphere " + Ui.Num(m.AtmosphereKm, "0") + " km", new Vector2(-1, m.AtmosphereKm), 1, SsaPalette.AlertLight);
                if (m.HeatFrom >= 0 && m.HeatTo > m.HeatFrom && m.HeatTo < m.Predicted.Count && !string.IsNullOrEmpty(m.HeatLabel))
                    Put(m.HeatLabel, m.Predicted[(m.HeatFrom + m.HeatTo) / 2], 0, SsaPalette.AlertLight);
                foreach (var mk in m.Markers)
                    Put(mk.Label, mk.At, 0, mk.Kind == LandMarkerKind.Node ? LandPalette.NodeLight
                        : mk.Kind == LandMarkerKind.Gold ? SsaPalette.GoldLight : LandPalette.MutedLight);
                if (!string.IsNullOrEmpty(m.TargetNote))
                    Put(m.TargetNote, Vector2.zero, 2, SsaPalette.GoldLight);
            }
            for (int i = n; i < _pool.Count; i++) _pool[i].style.display = DisplayStyle.None;
        }

        float Scale(Rect r) => Mathf.Max(0.3f, Mathf.Min(r.width / 656f, r.height / 436f));

        void PlaceLabels()
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10) return;
            float k = Scale(r);
            float xMax = Model.XMax, yMax = Model.YMax;
            foreach (var (label, at, align) in _placed)
            {
                Vector2 p = LandProfileModel.Map(r, xMax, yMax, new Vector2(Mathf.Max(0, at.x), at.y));
                if (align == 1)
                {
                    label.style.left = StyleKeyword.Auto;
                    label.style.right = 6 * k;
                    label.style.top = p.y - 18 * k;
                    label.style.width = StyleKeyword.Auto;
                }
                else if (align == 2)
                {
                    label.style.right = StyleKeyword.Auto;
                    label.style.width = 220;
                    label.style.left = Mathf.Min(p.x - 110, r.width - 220);
                    label.style.top = p.y + 26 * k;
                    label.style.unityTextAlign = TextAnchor.UpperCenter;
                }
                else
                {
                    label.style.right = StyleKeyword.Auto;
                    label.style.width = StyleKeyword.Auto;
                    label.style.left = Mathf.Min(p.x + 12 * k, r.width - 150);
                    label.style.top = p.y - 22 * k;
                }
            }
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            var m = Model;
            if (r.width < 10 || r.height < 10 || !m.HasData) return;
            float k = Scale(r);
            float xMax = m.XMax, yMax = m.YMax;
            var p = ctx.painter2D;
            Vector2 M(Vector2 v) => LandProfileModel.Map(r, xMax, yMax, v);
            float ground = M(Vector2.zero).y;
            float left = r.x, right = r.xMax;

            // Atmosphere: a faint coral band under a dashed line.
            if (!float.IsNaN(m.AtmosphereKm))
            {
                float y = M(new Vector2(0, m.AtmosphereKm)).y;
                p.fillColor = new Color(SsaPalette.Alert.r, SsaPalette.Alert.g, SsaPalette.Alert.b, 0.015f);   // linear colour space: 0.05 of the mockup reads far stronger
                p.BeginPath();
                p.MoveTo(new Vector2(left, y)); p.LineTo(new Vector2(right, y));
                p.LineTo(new Vector2(right, ground)); p.LineTo(new Vector2(left, ground));
                p.ClosePath();
                p.Fill();
                p.strokeColor = new Color(SsaPalette.Alert.r, SsaPalette.Alert.g, SsaPalette.Alert.b, 0.5f);
                p.lineWidth = 1;
                Dashed(p, new List<Vector2> { new Vector2(left, y), new Vector2(right, y) }, 8 * k, 8 * k);
            }

            // Peak heating: a wide translucent coral stroke along the prediction.
            if (m.HeatFrom >= 0 && m.HeatTo > m.HeatFrom && m.HeatTo < m.Predicted.Count)
            {
                p.strokeColor = new Color(SsaPalette.Alert.r, SsaPalette.Alert.g, SsaPalette.Alert.b, 0.18f);
                p.lineWidth = 16 * k;
                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                p.MoveTo(M(m.Predicted[m.HeatFrom]));
                for (int i = m.HeatFrom + 1; i <= m.HeatTo; i++) p.LineTo(M(m.Predicted[i]));
                p.Stroke();
            }

            // Prediction, dashed cyan.
            if (m.Predicted.Count > 1)
            {
                p.strokeColor = SsaPalette.Cyan;
                p.lineWidth = 3 * k;
                p.lineCap = LineCap.Butt;
                var pts = new List<Vector2>(m.Predicted.Count);
                foreach (var v in m.Predicted) pts.Add(M(v));
                Dashed(p, pts, 10 * k, 8 * k);
            }

            // Flown, gold.
            if (m.Flown.Count > 1)
            {
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 5 * k;
                p.lineCap = LineCap.Round;
                p.lineJoin = LineJoin.Round;
                p.BeginPath();
                p.MoveTo(M(m.Flown[0]));
                for (int i = 1; i < m.Flown.Count; i++) p.LineTo(M(m.Flown[i]));
                p.Stroke();
            }

            // Ground.
            p.fillColor = LandPalette.Soil;
            p.BeginPath();
            p.MoveTo(new Vector2(left, ground)); p.LineTo(new Vector2(right, ground));
            p.LineTo(new Vector2(right, ground + 4 * k)); p.LineTo(new Vector2(left, ground + 4 * k));
            p.ClosePath();
            p.Fill();

            // Markers.
            foreach (var mk in m.Markers)
            {
                p.fillColor = mk.Kind == LandMarkerKind.Node ? SsaPalette.Node : mk.Kind == LandMarkerKind.Gold ? SsaPalette.Gold : SsaPalette.Muted;
                p.BeginPath();
                p.Arc(M(mk.At), (mk.Kind == LandMarkerKind.Node ? 9 : 8) * k, 0, 360);
                p.Fill();
            }

            // The vessel: a night disc in a gold ring.
            if (m.Vessel.HasValue)
            {
                Vector2 c = M(m.Vessel.Value);
                p.fillColor = SsaPalette.Night;
                p.BeginPath(); p.Arc(c, 10 * k, 0, 360); p.Fill();
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 3 * k;
                p.BeginPath(); p.Arc(c, 10 * k, 0, 360); p.Stroke();
            }

            // Target: a gold crosshair on the touchdown.
            Vector2 t = M(Vector2.zero) + new Vector2(0, -6 * k);
            p.strokeColor = SsaPalette.GoldLight;
            p.lineWidth = 3 * k;
            p.lineCap = LineCap.Butt;
            p.BeginPath(); p.Arc(t, 12 * k, 0, 360); p.Stroke();
            p.BeginPath();
            p.MoveTo(t + new Vector2(-20 * k, 0)); p.LineTo(t + new Vector2(20 * k, 0));
            p.MoveTo(t + new Vector2(0, -20 * k)); p.LineTo(t + new Vector2(0, 8 * k));
            p.Stroke();
        }

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

    /// <summary>The hottest part's bar (heat gradient), with the guard's 75 % and the 90 % ceiling marked.</summary>
    public class LandHeatGauge : VisualElement
    {
        readonly VisualElement _fill;

        public float Fraction { get; private set; }

        public LandHeatGauge(float guard, float ceiling)
        {
            AddToClassList("ssa-land-heat");
            var track = Ui.Box("ssa-land-heat__track");
            _fill = Ui.Box("ssa-land-heat__fill");
            SsaPalette.SetBackground(_fill, LandPalette.HeatGradient);
            track.Add(_fill);
            Add(track);
            Add(Mark(guard, Ui.Num(guard * 100, "0") + " % guard", "ssa-land-heat__mark--guard"));
            Add(Mark(ceiling, Ui.Num(ceiling * 100, "0") + " %", "ssa-land-heat__mark--ceiling"));
            SetFraction(0);
        }

        static VisualElement Mark(float at, string text, string cls)
        {
            var m = Ui.Box("ssa-land-heat__mark", cls);
            m.style.left = Length.Percent(at * 100);
            m.Add(Ui.Box("ssa-land-heat__tick"));
            m.Add(Ui.Label(text, "ssa-land-heat__label"));
            m.pickingMode = PickingMode.Ignore;
            return m;
        }

        public void SetFraction(float f)
        {
            Fraction = float.IsNaN(f) ? 0 : Mathf.Clamp01(f);
            _fill.style.width = Length.Percent(Fraction * 100);
        }
    }

    /// <summary>The target box's crosshair (land.svg: a gold circle and cross).</summary>
    public class LandTargetIcon : VisualElement
    {
        public LandTargetIcon()
        {
            AddToClassList("ssa-land-target__icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += ctx =>
            {
                Rect r = contentRect;
                if (r.width < 4) return;
                float k = Mathf.Min(r.width, r.height) / 36f;
                Vector2 c = r.center;
                var p = ctx.painter2D;
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 3 * k;
                p.BeginPath(); p.Arc(c, 11 * k, 0, 360); p.Stroke();
                p.BeginPath();
                p.MoveTo(c + new Vector2(-18 * k, 0)); p.LineTo(c + new Vector2(18 * k, 0));
                p.MoveTo(c + new Vector2(0, -18 * k)); p.LineTo(c + new Vector2(0, 18 * k));
                p.Stroke();
            };
        }
    }
}
