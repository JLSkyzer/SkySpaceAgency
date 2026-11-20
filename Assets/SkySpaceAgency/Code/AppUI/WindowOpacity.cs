using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The window opacity of the new window: from 30 % (still readable) to 100 %, on a 5 % step,
    /// 100 % by default. Applied as the window element's opacity: its position, size and the resize
    /// handle are not affected, in Scale and Reflow alike.
    /// </summary>
    public static class WindowOpacity
    {
        public const float Min = 0.3f;
        public const float Max = 1f;
        public const float Step = 0.05f;
        public const float Default = 1f;

        /// <summary>Within [Min, Max] on a 5 % step. NaN, infinite or not positive: Default.</summary>
        public static float Clamp(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v <= 0) return Default;
            double snapped = Math.Round(Math.Round(v / Step) * Step, 2);
            return Mathf.Clamp((float)snapped, Min, Max);
        }

        public static string Percent(float v) =>
            Mathf.RoundToInt(Clamp(v) * 100).ToString(CultureInfo.InvariantCulture) + " %";
    }

    /// <summary>
    /// The small opacity slider at the bottom of the window, next to the resize handle: a label, a thin
    /// slider in the style of the other sliders (cyan fill, white knob) and its value.
    /// </summary>
    public class AppOpacityBar : VisualElement
    {
        readonly Label _value;
        readonly VisualElement _fill;

        public Slider Input { get; }
        public event Action<float> changed;

        public AppOpacityBar(float value)
        {
            AddToClassList("ssa-opacity");
            AddToClassList("ssa-slider");   // the slider rules of SsaApp.uss (track, fill, knob)
            name = "opacity-bar";
            tooltip = "Window opacity";
            Add(Ui.Label("OPACITY", "ssa-opacity__label"));
            Input = new Slider(WindowOpacity.Min * 100, WindowOpacity.Max * 100) { value = WindowOpacity.Clamp(value) * 100 };
            Input.AddToClassList("ssa-slider__input");
            Input.AddToClassList("ssa-opacity__slider");
            _fill = Ui.Box("ssa-slider__fill");
            _fill.pickingMode = PickingMode.Ignore;
            (Input.Q(className: "unity-base-slider__tracker") ?? Input).Add(_fill);
            Add(Input);
            _value = Ui.Label("", "ssa-opacity__value");
            Add(_value);
            Input.RegisterValueChangedCallback(e => OnSlider(e.newValue));
            Refresh();
        }

        public float Value => WindowOpacity.Clamp(Input.value / 100);

        void OnSlider(float percent)
        {
            float v = WindowOpacity.Clamp(percent / 100);
            Input.SetValueWithoutNotify(v * 100);
            Refresh();
            changed?.Invoke(v);
        }

        public void SetValueWithoutNotify(float v)
        {
            Input.SetValueWithoutNotify(WindowOpacity.Clamp(v) * 100);
            Refresh();
        }

        void Refresh()
        {
            _value.text = WindowOpacity.Percent(Value);
            _fill.style.width = Length.Percent(AppSlider.Fill(Input.value, Input.lowValue, Input.highValue) * 100);
        }
    }
}
