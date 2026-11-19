using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    // The new UI's small controls. Each one only builds its children and sets USS classes; the look is in
    // SsaApp.uss. Tabs migrating from the classic window (step 2) build their pages from these.

    static class Ui
    {
        public static Label Label(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (string c in classes) l.AddToClassList(c);
            return l;
        }

        public static VisualElement Box(params string[] classes)
        {
            var e = new VisualElement();
            foreach (string c in classes) e.AddToClassList(c);
            return e;
        }

        public static string Num(float v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>A card: a spaced uppercase title, an optional note on the right, and a body.</summary>
    public class AppCard : VisualElement
    {
        public const string UssClass = "ssa-card";
        readonly Label _title, _note;

        public VisualElement Body { get; }
        public override VisualElement contentContainer => Body ?? this;

        public AppCard(string title, string note = null, bool sky = false)
        {
            AddToClassList(UssClass);
            var head = Ui.Box("ssa-card__head");
            _title = Ui.Label(title?.ToUpperInvariant() ?? "", "ssa-card__title");
            _note = Ui.Label(note ?? "", "ssa-card__note");
            head.Add(_title);
            head.Add(_note);
            hierarchy.Add(head);
            Body = Ui.Box("ssa-card__body");
            hierarchy.Add(Body);
            if (sky)
            {
                AddToClassList(UssClass + "--sky");
                SsaPalette.SetBackground(this, SsaPalette.SkyGradient);
            }
        }

        public string Title { get => _title.text; set => _title.text = value?.ToUpperInvariant() ?? ""; }
        public string Note { get => _note.text; set => _note.text = value ?? ""; }
    }

    /// <summary>A telemetry tile: label, big value, unit. Kind colours the value (gold, data, ok, alert).</summary>
    public class AppTile : VisualElement
    {
        public enum Kind { Plain, Gold, Data, Ok, Alert }
        readonly Label _value, _unit;
        Kind _kind;

        public AppTile(string label, string value, string unit = null, Kind kind = Kind.Plain)
        {
            AddToClassList("ssa-tile");
            Add(Ui.Label(label?.ToUpperInvariant() ?? "", "ssa-tile__label"));
            var line = Ui.Box("ssa-tile__line");
            _value = Ui.Label(value ?? "", "ssa-tile__value");
            _unit = Ui.Label(unit ?? "", "ssa-tile__unit");
            line.Add(_value);
            line.Add(_unit);
            Add(line);
            SetKind(kind);
        }

        public string Value => _value.text;

        public void SetValue(string value, string unit = null)
        {
            _value.text = value ?? "";
            if (unit != null) _unit.text = unit;
        }

        public void SetKind(Kind kind)
        {
            RemoveFromClassList("ssa-tile--" + _kind.ToString().ToLowerInvariant());
            _kind = kind;
            if (kind != Kind.Plain) AddToClassList("ssa-tile--" + kind.ToString().ToLowerInvariant());
        }
    }

    /// <summary>An on/off switch with its label on the left (prograde when on).</summary>
    public class AppSwitch : VisualElement
    {
        public const string OnClass = "ssa-switch--on";
        bool _value;

        public event Action<bool> changed;

        public AppSwitch(string label, bool value = false)
        {
            AddToClassList("ssa-switch");
            Add(Ui.Label(label, "ssa-switch__label"));
            var track = Ui.Box("ssa-switch__track");
            track.Add(Ui.Box("ssa-switch__knob"));
            Add(track);
            SetValueWithoutNotify(value);
            this.AddManipulator(new Clickable(Toggle));
        }

        public bool value
        {
            get => _value;
            set
            {
                if (value == _value) return;
                SetValueWithoutNotify(value);
                changed?.Invoke(value);
            }
        }

        public void SetValueWithoutNotify(bool v)
        {
            _value = v;
            EnableInClassList(OnClass, v);
        }

        /// <summary>What a click does (ignored while disabled).</summary>
        public void Toggle()
        {
            if (enabledInHierarchy) value = !_value;
        }
    }

    /// <summary>A labelled number field with its unit inside the box, on the right.</summary>
    public class AppField : VisualElement
    {
        public FloatField Input { get; }

        public AppField(string label, float value, string unit = null, string format = "0.0")
        {
            AddToClassList("ssa-field");
            Add(Ui.Label(label, "ssa-field__label"));
            var box = Ui.Box("ssa-field__box");
            Input = new FloatField { value = value, formatString = format };
            Input.AddToClassList("ssa-field__input");
            box.Add(Input);
            if (!string.IsNullOrEmpty(unit))
            {
                var u = Ui.Label(unit, "ssa-field__unit");
                u.pickingMode = PickingMode.Ignore;
                box.Add(u);
            }
            Add(box);
        }
    }

    /// <summary>A slider row: label and value text above a thin track with a cyan fill and a white knob.</summary>
    public class AppSlider : VisualElement
    {
        readonly Label _valueText;
        readonly VisualElement _fill;
        readonly Func<float, string> _format;

        public Slider Input { get; }
        public event Action<float> changed;

        public AppSlider(string label, float min, float max, float value, Func<float, string> format = null)
        {
            AddToClassList("ssa-slider");
            _format = format ?? (v => Ui.Num(v, "0.##"));
            var head = Ui.Box("ssa-slider__head");
            head.Add(Ui.Label(label, "ssa-slider__label"));
            _valueText = Ui.Label("", "ssa-slider__value");
            head.Add(_valueText);
            Add(head);

            Input = new Slider(min, max) { value = value };
            Input.AddToClassList("ssa-slider__input");
            _fill = Ui.Box("ssa-slider__fill");
            _fill.pickingMode = PickingMode.Ignore;
            var tracker = Input.Q(className: "unity-base-slider__tracker");
            (tracker ?? Input).Add(_fill);
            Add(Input);
            Input.RegisterValueChangedCallback(e => { Refresh(); changed?.Invoke(e.newValue); });
            Refresh();
        }

        public float Value { get => Input.value; set => Input.value = value; }

        /// <summary>Moves the knob, the fill and the value text without raising `changed`.</summary>
        public void SetValueWithoutNotify(float v)
        {
            Input.SetValueWithoutNotify(v);
            Refresh();
        }

        /// <summary>The filled part of the track, from 0 to 1.</summary>
        public float Fraction => Fill(Input.value, Input.lowValue, Input.highValue);

        public static float Fill(float value, float min, float max) =>
            max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0;

        void Refresh()
        {
            _valueText.text = _format(Input.value);
            _fill.style.width = Length.Percent(Fraction * 100);
        }
    }

    /// <summary>A segmented choice (Orbit / Transfer / Rendezvous): one option active.</summary>
    public class AppSegmented : VisualElement
    {
        public const string ActiveClass = "ssa-segmented__item--active";
        readonly List<VisualElement> _items = new();

        public int Selected { get; private set; } = -1;
        public event Action<int> changed;

        public AppSegmented(IReadOnlyList<string> options, int selected = 0)
        {
            AddToClassList("ssa-segmented");
            for (int i = 0; i < options.Count; i++)
            {
                int index = i;
                var item = Ui.Box("ssa-segmented__item");
                item.Add(Ui.Label(options[i], "ssa-segmented__label"));
                item.AddManipulator(new Clickable(() => Select(index)));
                _items.Add(item);
                Add(item);
            }
            SetWithoutNotify(selected);
        }

        public int Count => _items.Count;
        public VisualElement ItemAt(int i) => _items[i];

        public void Select(int index)
        {
            if (index < 0 || index >= _items.Count || index == Selected) return;
            SetWithoutNotify(index);
            changed?.Invoke(index);
        }

        public void SetWithoutNotify(int index)
        {
            Selected = Mathf.Clamp(index, 0, Mathf.Max(0, _items.Count - 1));
            for (int i = 0; i < _items.Count; i++)
                _items[i].EnableInClassList(ActiveClass, i == Selected);
        }
    }

    /// <summary>A stage Δv bar: name, value, and a bar filled from 0 to 1 (gold for the active stage).</summary>
    public class AppStageBar : VisualElement
    {
        readonly Label _value;
        readonly VisualElement _fill;

        public AppStageBar(string label, string value, float fraction, bool active = false)
        {
            AddToClassList("ssa-stage");
            EnableInClassList("ssa-stage--gold", active);
            var head = Ui.Box("ssa-stage__head");
            head.Add(Ui.Label(label, "ssa-stage__label"));
            _value = Ui.Label(value, "ssa-stage__value");
            head.Add(_value);
            Add(head);
            var bar = Ui.Box("ssa-stage__bar");
            _fill = Ui.Box("ssa-stage__fill");
            bar.Add(_fill);
            Add(bar);
            SetFraction(fraction);
        }

        public float Fraction { get; private set; }

        public void SetFraction(float fraction)
        {
            Fraction = float.IsNaN(fraction) ? 0 : Mathf.Clamp01(fraction);
            _fill.style.width = Length.Percent(Fraction * 100);
        }

        public void SetValue(string value) => _value.text = value;
    }

    /// <summary>A status pill: a dot and a line of text, coloured by its kind (OK, alert, info, neutral).</summary>
    public class AppPill : VisualElement
    {
        readonly Label _text;
        public StatusKind Kind { get; private set; }

        public AppPill(StatusKind kind, string text)
        {
            AddToClassList(StatusKinds.PillClass);
            Add(Ui.Box("ssa-dot"));
            _text = Ui.Label("", "ssa-pill__text");
            Add(_text);
            Set(kind, text);
        }

        public string Text => _text.text;

        public void Set(StatusKind kind, string text)
        {
            foreach (string c in StatusKinds.AllClasses) RemoveFromClassList(c);
            Kind = kind;
            AddToClassList(StatusKinds.ClassFor(kind));
            _text.text = text ?? "";
        }
    }

    /// <summary>A header chip: "label value" in a rounded capsule, or a status dot and a name.</summary>
    public class AppChip : VisualElement
    {
        readonly Label _label, _value;
        readonly VisualElement _dot;

        public AppChip(string label, string value, StatusKind? dot = null)
        {
            AddToClassList("ssa-chip");
            _dot = Ui.Box("ssa-dot");
            Add(_dot);
            _label = Ui.Label(label ?? "", "ssa-chip__label");
            _value = Ui.Label(value ?? "", "ssa-chip__value");
            Add(_label);
            Add(_value);
            SetDot(dot);
            _label.style.display = string.IsNullOrEmpty(label) ? DisplayStyle.None : DisplayStyle.Flex;
            if (string.IsNullOrEmpty(label)) _value.style.marginLeft = 0;
        }

        public void SetValue(string value) => _value.text = value ?? "";

        public void SetDot(StatusKind? kind)
        {
            _dot.style.display = kind.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (string c in new[] { "ssa-dot--alert", "ssa-dot--info", "ssa-dot--neutral" }) _dot.RemoveFromClassList(c);
            if (kind == StatusKind.Alert) _dot.AddToClassList("ssa-dot--alert");
            else if (kind == StatusKind.Info) _dot.AddToClassList("ssa-dot--info");
            else if (kind == StatusKind.Neutral) _dot.AddToClassList("ssa-dot--neutral");
        }
    }

    /// <summary>An action button: main (gold, the one that flies), abort (red), or plain.</summary>
    public class AppButton : VisualElement
    {
        public enum Kind { Plain, Main, Abort }
        readonly Label _label;

        public event Action clicked;
        public Kind ButtonKind { get; }

        public AppButton(string text, Kind kind = Kind.Plain, Action onClick = null)
        {
            ButtonKind = kind;
            AddToClassList("ssa-button");
            AddToClassList("ssa-button--" + kind.ToString().ToLowerInvariant());
            if (kind == Kind.Main)
                SsaPalette.SetBackground(this, SsaPalette.GoldGradient);
            _label = Ui.Label(text, "ssa-button__label");
            _label.pickingMode = PickingMode.Ignore;
            Add(_label);
            if (onClick != null) clicked += onClick;
            this.AddManipulator(new Clickable(Click));
        }

        public string Text { get => _label.text; set => _label.text = value; }

        public void Click()
        {
            if (enabledInHierarchy) clicked?.Invoke();
        }
    }
}
