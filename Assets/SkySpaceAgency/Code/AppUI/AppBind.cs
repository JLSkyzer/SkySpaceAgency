using System;
using System.Collections.Generic;
using System.Globalization;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// Builds new-UI controls bound both ways to the mod's settings (KTools Setting / ClampSetting /
    /// EnumSetting): a change of the control writes the setting, a change of the setting (from the
    /// classic window, a pilot, Reset) updates the control without raising it again. Every bound key
    /// is recorded in Bound: the test AppSettingsCoverageTests checks that each persisted key of the
    /// classic UI has a control in the new one.
    /// </summary>
    public static class AppBind
    {
        public static readonly HashSet<string> Bound = new();

        public static void Record(string key)
        {
            if (!string.IsNullOrEmpty(key)) Bound.Add(key);
        }

        static string Inv(float v, string f) => v.ToString(f, CultureInfo.InvariantCulture);

        // ---- bool ----

        public static AppSwitch Switch(string label, Setting<bool> s)
        {
            var sw = new AppSwitch(label, s.V);
            Record(s.key);
            sw.changed += v => s.V = v;
            s.listeners += v => sw.SetValueWithoutNotify(v);
            return sw;
        }

        // ---- sliders ----

        /// <summary>A slider on a clamped float setting; step snaps the value (0: none).</summary>
        public static AppSlider Slider(string label, ClampSetting<float> s, Func<float, string> format = null, float step = 0)
        {
            var sl = new AppSlider(label, s.min, s.max, s.V, format ?? (v => Inv(v, "0.##")));
            Record(s.key);
            sl.changed += v =>
            {
                float w = step > 0 ? Mathf.Round(v / step) * step : v;
                if (!Mathf.Approximately(w, v)) sl.Input.SetValueWithoutNotify(w);
                s.V = w;
            };
            s.listeners += v => sl.SetValueWithoutNotify(v);
            return sl;
        }

        public static AppSlider Slider(string label, ClampSetting<int> s, Func<float, string> format = null)
        {
            var sl = new AppSlider(label, s.min, s.max, s.V, format ?? (v => Inv(v, "0")));
            Record(s.key);
            sl.changed += v =>
            {
                int w = Mathf.RoundToInt(v);
                sl.Input.SetValueWithoutNotify(w);
                s.V = w;
            };
            s.listeners += v => sl.SetValueWithoutNotify(v);
            return sl;
        }

        /// <summary>A slider on a plain setting with an explicit range (the classic UI's slider limits).</summary>
        public static AppSlider Slider(string label, Setting<float> s, float min, float max, Func<float, string> format = null, float step = 0)
        {
            var sl = new AppSlider(label, min, max, s.V, format ?? (v => Inv(v, "0.##")));
            Record(s.key);
            sl.changed += v =>
            {
                float w = step > 0 ? Mathf.Round(v / step) * step : v;
                if (!Mathf.Approximately(w, v)) sl.Input.SetValueWithoutNotify(w);
                s.V = w;
            };
            s.listeners += v => sl.SetValueWithoutNotify(v);
            return sl;
        }

        public static AppSlider Slider(string label, Setting<int> s, int min, int max, Func<float, string> format = null)
        {
            var sl = new AppSlider(label, min, max, s.V, format ?? (v => Inv(v, "0")));
            Record(s.key);
            sl.changed += v =>
            {
                int w = Mathf.RoundToInt(v);
                sl.Input.SetValueWithoutNotify(w);
                s.V = w;
            };
            s.listeners += v => sl.SetValueWithoutNotify(v);
            return sl;
        }

        // ---- number fields ----

        public static AppField Field(string label, Setting<float> s, string unit = null, string format = "0.##")
        {
            var f = new AppField(label, s.V, unit, format);
            Record(s.key);
            f.Input.RegisterValueChangedCallback(e => { s.V = e.newValue; if (!Mathf.Approximately(s.V, e.newValue)) f.Input.SetValueWithoutNotify(s.V); });
            s.listeners += v => f.Input.SetValueWithoutNotify(v);
            return f;
        }

        public static AppField Field(string label, Setting<double> s, string unit = null, string format = "0.##")
        {
            var f = new AppField(label, (float)s.V, unit, format);
            Record(s.key);
            f.Input.RegisterValueChangedCallback(e => s.V = e.newValue);
            s.listeners += v => f.Input.SetValueWithoutNotify((float)v);
            return f;
        }

        public static AppField Field(string label, Setting<int> s, string unit = null)
        {
            var f = new AppField(label, s.V, unit, "0");
            Record(s.key);
            f.Input.RegisterValueChangedCallback(e =>
            {
                int w = Mathf.RoundToInt(e.newValue);
                s.V = w;
                if (!Mathf.Approximately(w, e.newValue) || s.V != w) f.Input.SetValueWithoutNotify(s.V);
            });
            s.listeners += v => f.Input.SetValueWithoutNotify(v);
            return f;
        }

        public static AppTextField Text(string label, Setting<string> s, string placeholder = null)
        {
            var t = new AppTextField(label, s.V, placeholder);
            Record(s.key);
            t.Input.RegisterValueChangedCallback(e => s.V = e.newValue ?? "");
            s.listeners += v => t.Input.SetValueWithoutNotify(v ?? "");
            return t;
        }

        // ---- enums ----

        /// <summary>A segmented choice over some values of an enum setting (labels in the same order).</summary>
        public static AppSegmented Segmented<T>(EnumSetting<T> s, IReadOnlyList<T> values, IReadOnlyList<string> labels) where T : struct
        {
            int Index(T v) { for (int i = 0; i < values.Count; i++) if (values[i].Equals(v)) return i; return 0; }
            var seg = new AppSegmented(labels, Index(s.V));
            Record(s.path);
            seg.changed += i => s.V = values[i];
            s.listeners += v => seg.SetWithoutNotify(Index(v));
            return seg;
        }

        public static AppDropdown Dropdown<T>(string label, EnumSetting<T> s, IReadOnlyList<T> values, IReadOnlyList<string> labels) where T : struct
        {
            int Index(T v) { for (int i = 0; i < values.Count; i++) if (values[i].Equals(v)) return i; return 0; }
            var d = new AppDropdown(label, new List<string>(labels), Index(s.V));
            Record(s.path);
            d.changed += i => { if (i >= 0 && i < values.Count) s.V = values[i]; };
            s.listeners += v => d.SetIndexWithoutNotify(Index(v));
            return d;
        }

        /// <summary>All the values of an enum, labelled by their names with underscores as spaces.</summary>
        public static (List<T> values, List<string> labels) EnumChoices<T>() where T : struct, Enum
        {
            var values = new List<T>((T[])Enum.GetValues(typeof(T)));
            var labels = values.ConvertAll(v => v.ToString().Replace('_', ' '));
            return (values, labels);
        }
    }
}
