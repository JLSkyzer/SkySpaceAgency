using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The compact flight bar (docs/design/ui/hud.png), shown by AppWindow while a pilot flies and the
    /// window is closed. Expanded variant: badge, pilot and phase, phase dots, the pilot's readouts, an
    /// optional gauge, Stop and open-the-window buttons. Alert variant (HudState.Alert set): the bar
    /// turns coral with the guard's message and detail; Stop and open stay. Show is called every
    /// frame: texts are updated in place, the dots and readouts are rebuilt only when their count
    /// changes. It sits in the window's host, outside .ssa-app, so its style sheet rules are scoped
    /// under .ssa-hud (App/Tabs/Settings.uss).
    /// </summary>
    public class AppHudView : VisualElement
    {
        public const string UssClass = "ssa-hud";
        public const string AlertClass = "ssa-hud--alert";

        public event Action openWindow;

        readonly VisualElement _main, _dots, _readouts, _gauge, _gaugeFill, _alertRow;
        readonly Label _pilot, _phase, _gaugeLabel, _gaugeValue, _alertText, _alertDetail;
        readonly List<VisualElement> _dotList = new();
        readonly List<VisualElement> _links = new();
        readonly List<(VisualElement box, Label label, Label value)> _readoutList = new();
        Action _stop;
        int _gaugePercent = -1;

        public HudIconButton StopButton { get; }
        public HudIconButton OpenButton { get; }
        public HudState State { get; private set; }
        public bool Alerting { get; private set; }
        public int DotCount => _dotList.Count;
        public int ReadoutCount { get; private set; }
        public string PilotText => _pilot.text;
        public string PhaseText => _phase.text;
        public string AlertText => _alertText.text;
        public string GaugeText => _gaugeValue.text;
        public bool GaugeShown => _gauge.style.display != DisplayStyle.None;

        public AppHudView()
        {
            AddToClassList(UssClass);
            name = "app-hud";
            SsaPalette.SetBackground(this, HudGradient);

            var badge = new AppLogo();
            badge.AddToClassList("ssa-hud__badge");
            Add(badge);

            _main = Ui.Box("ssa-hud__main");
            var title = Ui.Box("ssa-hud__title");
            _pilot = Ui.Label("", "ssa-hud__pilot");
            _phase = Ui.Label("", "ssa-hud__phase");
            title.Add(_pilot);
            title.Add(_phase);
            _main.Add(title);
            _dots = Ui.Box("ssa-hud__dots");
            _main.Add(_dots);
            _main.Add(Ui.Box("ssa-hud__sep"));
            _readouts = Ui.Box("ssa-hud__readouts");
            _main.Add(_readouts);
            _gauge = Ui.Box("ssa-hud__gauge");
            _gaugeLabel = Ui.Label("", "ssa-hud__label");
            _gauge.Add(_gaugeLabel);
            var gaugeLine = Ui.Box("ssa-hud__gauge-line");
            var track = Ui.Box("ssa-hud__gauge-track");
            _gaugeFill = Ui.Box("ssa-hud__gauge-fill");
            track.Add(_gaugeFill);
            gaugeLine.Add(track);
            _gaugeValue = Ui.Label("", "ssa-hud__gauge-value");
            gaugeLine.Add(_gaugeValue);
            _gauge.Add(gaugeLine);
            _main.Add(_gauge);
            Add(_main);

            _alertRow = Ui.Box("ssa-hud__alert");
            _alertRow.Add(Ui.Box("ssa-hud__alert-dot"));
            _alertText = Ui.Label("", "ssa-hud__alert-text");
            _alertDetail = Ui.Label("", "ssa-hud__alert-detail");
            _alertRow.Add(_alertText);
            _alertRow.Add(_alertDetail);
            Add(_alertRow);
            _alertRow.style.display = DisplayStyle.None;

            StopButton = new HudIconButton(HudIconButton.Glyph.Stop, () => _stop?.Invoke());
            StopButton.tooltip = "Stop the pilot";
            OpenButton = new HudIconButton(HudIconButton.Glyph.Window, RaiseOpen);
            OpenButton.tooltip = "Open the full window";
            Add(StopButton);
            Add(OpenButton);

            style.display = DisplayStyle.None;
        }

        protected void RaiseOpen() => openWindow?.Invoke();

        /// <summary>Shows the running pilot's state; null hides the bar.</summary>
        public void Show(HudState state)
        {
            State = state;
            if (state == null)
            {
                _stop = null;
                if (style.display != DisplayStyle.None) style.display = DisplayStyle.None;
                return;
            }
            if (style.display != DisplayStyle.Flex) style.display = DisplayStyle.Flex;

            _stop = state.Stop;
            // Always shown, as in the mockup; disabled when the pilot gives no way to stop it.
            if (StopButton.enabledSelf != (state.Stop != null)) StopButton.SetEnabled(state.Stop != null);

            bool alert = !string.IsNullOrEmpty(state.Alert);
            if (alert != Alerting)
            {
                Alerting = alert;
                EnableInClassList(AlertClass, alert);
                Display(_main, !alert);
                Display(_alertRow, alert);
                SsaPalette.SetBackground(this, alert ? AlertGradient : HudGradient);
            }
            if (alert)
            {
                SetText(_alertText, string.IsNullOrEmpty(state.Pilot) ? state.Alert : state.Pilot + " · " + state.Alert);
                SetText(_alertDetail, state.AlertDetail);
                return;
            }

            SetText(_pilot, state.Pilot?.ToUpperInvariant());
            SetText(_phase, state.Phase);
            SetPhases(state.Phases, state.CurrentPhase);
            SetReadouts(state.Readouts);

            bool gauge = state.Gauge.HasValue;
            Display(_gauge, gauge);
            if (gauge)
            {
                float g = Fraction(state.Gauge.Value);
                SetText(_gaugeLabel, state.GaugeLabel?.ToUpperInvariant());
                int pc = Mathf.RoundToInt(g * 100);
                if (pc != _gaugePercent)
                {
                    _gaugePercent = pc;
                    _gaugeValue.text = pc + " %";
                    _gaugeFill.style.width = Length.Percent(pc);
                }
            }
        }

        /// <summary>A gauge value within 0..1 (NaN: 0).</summary>
        public static float Fraction(float v) => float.IsNaN(v) ? 0 : Mathf.Clamp01(v);

        void SetPhases(IReadOnlyList<string> phases, int current)
        {
            int n = phases?.Count ?? 0;
            Display(_dots, n > 0);
            if (n != _dotList.Count)
            {
                _dots.Clear();
                _dotList.Clear();
                _links.Clear();
                for (int i = 0; i < n; i++)
                {
                    if (i > 0)
                    {
                        var link = Ui.Box("ssa-hud__link");
                        _links.Add(link);
                        _dots.Add(link);
                    }
                    var dot = Ui.Box("ssa-hud__dot");
                    _dotList.Add(dot);
                    _dots.Add(dot);
                }
            }
            if (n == 0) return;
            int cur = Mathf.Clamp(current, 0, n - 1);   // as PhaseTimelineModel
            for (int i = 0; i < n; i++)
            {
                _dotList[i].EnableInClassList("ssa-hud__dot--done", i < cur);
                _dotList[i].EnableInClassList("ssa-hud__dot--current", i == cur);
                if (i > 0) _links[i - 1].EnableInClassList("ssa-hud__link--done", i <= cur);
            }
        }

        void SetReadouts(List<(string label, string value)> readouts)
        {
            int n = readouts?.Count ?? 0;
            while (_readoutList.Count < n)
            {
                var box = Ui.Box("ssa-hud__readout");
                var label = Ui.Label("", "ssa-hud__label");
                var value = Ui.Label("", "ssa-hud__value");
                box.Add(label);
                box.Add(value);
                _readouts.Add(box);
                _readoutList.Add((box, label, value));
            }
            for (int i = 0; i < _readoutList.Count; i++)
            {
                var r = _readoutList[i];
                Display(r.box, i < n);
                if (i >= n) continue;
                SetText(r.label, readouts[i].label?.ToUpperInvariant());
                SetText(r.value, readouts[i].value);
            }
            ReadoutCount = n;
        }

        static void SetText(Label l, string text)
        {
            text ??= "";
            if (l.text != text) l.text = text;
        }

        static void Display(VisualElement e, bool shown)
        {
            var d = shown ? DisplayStyle.Flex : DisplayStyle.None;
            if (e.style.display != d) e.style.display = d;
        }

        static Texture2D _hud, _alert;

        /// <summary>The bar: #111c44 to #0a1230, as the window's top (hud.svg).</summary>
        static Texture2D HudGradient => _hud ??= Gradient(SsaPalette.Hex(0x121d48), SsaPalette.Hex(0x0a1230));

        /// <summary>The alert bar: dark red (hud.svg #3a1414 to #2a0e10).</summary>
        static Texture2D AlertGradient => _alert ??= Gradient(SsaPalette.Hex(0x3a1616), SsaPalette.Hex(0x2a0e10));

        static Texture2D Gradient(Color top, Color bottom)
        {
            const int h = 32;
            var t = new Texture2D(1, h, TextureFormat.RGBA32, false)
            {
                name = "ssa-hud-gradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < h; y++)
                t.SetPixel(0, y, Color.Lerp(bottom, top, y / (float)(h - 1)));
            t.Apply(false, false);
            return t;
        }
    }

    /// <summary>The bar's square buttons: Stop (a coral square on dark red), open the window (a window outline).</summary>
    public class HudIconButton : VisualElement
    {
        public enum Glyph { Stop, Window }
        readonly Glyph _glyph;
        public event Action clicked;

        public HudIconButton(Glyph glyph, Action onClick = null)
        {
            _glyph = glyph;
            AddToClassList("ssa-hud__button");
            AddToClassList(glyph == Glyph.Stop ? "ssa-hud__button--stop" : "ssa-hud__button--open");
            name = glyph == Glyph.Stop ? "hud-stop" : "hud-open";
            if (onClick != null) clicked += onClick;
            this.AddManipulator(new Clickable(Click));
            generateVisualContent += Draw;
        }

        public void Click()
        {
            if (enabledInHierarchy) clicked?.Invoke();
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width <= 0) return;
            float k = Mathf.Min(r.width, r.height) / 46f;   // hud.svg: a 46 px button
            Vector2 c = r.center;
            var p = ctx.painter2D;
            if (_glyph == Glyph.Stop)
            {
                p.fillColor = SsaPalette.AlertLight;
                float h = 8 * k;
                p.BeginPath();
                p.MoveTo(c + new Vector2(-h, -h));
                p.LineTo(c + new Vector2(h, -h));
                p.LineTo(c + new Vector2(h, h));
                p.LineTo(c + new Vector2(-h, h));
                p.ClosePath();
                p.Fill();
            }
            else
            {
                p.strokeColor = SsaPalette.Muted;
                p.lineWidth = 2 * k;
                float w = 11 * k, h = 10 * k;
                p.BeginPath();
                p.MoveTo(c + new Vector2(-w, -h));
                p.LineTo(c + new Vector2(w, -h));
                p.LineTo(c + new Vector2(w, h));
                p.LineTo(c + new Vector2(-w, h));
                p.ClosePath();
                p.MoveTo(c + new Vector2(-w, -h + 6 * k));
                p.LineTo(c + new Vector2(w, -h + 6 * k));
                p.Stroke();
            }
        }
    }
}
