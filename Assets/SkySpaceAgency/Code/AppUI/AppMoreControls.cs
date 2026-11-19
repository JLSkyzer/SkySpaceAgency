using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    // Controls the migrated tabs need beyond the foundations (plan 2026-11-19-new-ui-tab-migration).

    /// <summary>A labelled drop-down list (DropdownField restyled to the mockups' field box).</summary>
    public class AppDropdown : VisualElement
    {
        public DropdownField Input { get; }
        public event Action<int> changed;

        public AppDropdown(string label, List<string> choices, int index = 0)
        {
            AddToClassList("ssa-dropdown");
            if (!string.IsNullOrEmpty(label)) Add(Ui.Label(label, "ssa-field__label"));
            Input = new DropdownField(choices ?? new List<string>(), Mathf.Clamp(index, -1, (choices?.Count ?? 0) - 1));
            Input.AddToClassList("ssa-dropdown__input");
            Input.RegisterValueChangedCallback(_ => changed?.Invoke(Input.index));
            Add(Input);
        }

        public int Index => Input.index;
        public string Value => Input.value;

        public void SetChoices(List<string> choices, int index)
        {
            Input.choices = choices ?? new List<string>();
            SetIndexWithoutNotify(index);
        }

        public void SetIndexWithoutNotify(int index)
        {
            var c = Input.choices;
            if (c == null || c.Count == 0) { Input.SetValueWithoutNotify(""); return; }
            Input.SetValueWithoutNotify(c[Mathf.Clamp(index, 0, c.Count - 1)]);
        }
    }

    /// <summary>A labelled text field (for settings kept as text, such as an optional height).</summary>
    public class AppTextField : VisualElement
    {
        public TextField Input { get; }

        public AppTextField(string label, string value, string placeholder = null)
        {
            AddToClassList("ssa-field");
            AddToClassList("ssa-textfield");
            if (!string.IsNullOrEmpty(label)) Add(Ui.Label(label, "ssa-field__label"));
            Input = new TextField { value = value ?? "" };
            Input.AddToClassList("ssa-field__input");
            if (!string.IsNullOrEmpty(placeholder)) Input.textEdition.placeholder = placeholder;
            Add(Input);
        }
    }

    /// <summary>
    /// A titled group inside a card that folds (the classic tabs' ADVANCED / INFO foldouts). Folded
    /// by default; the title row toggles it.
    /// </summary>
    public class AppSection : VisualElement
    {
        public const string OpenClass = "ssa-section--open";
        readonly Label _arrow;

        public VisualElement Body { get; }
        public override VisualElement contentContainer => Body ?? this;
        public bool Open { get; private set; }

        public AppSection(string title, bool open = false)
        {
            AddToClassList("ssa-section");
            var head = Ui.Box("ssa-section__head");
            _arrow = Ui.Label("", "ssa-section__arrow");
            head.Add(_arrow);
            head.Add(Ui.Label(title?.ToUpperInvariant() ?? "", "ssa-section__title"));
            head.AddManipulator(new Clickable(() => SetOpen(!Open)));
            hierarchy.Add(head);
            Body = Ui.Box("ssa-section__body");
            hierarchy.Add(Body);
            SetOpen(open);
        }

        public void SetOpen(bool open)
        {
            Open = open;
            EnableInClassList(OpenClass, open);
            _arrow.text = open ? "▾" : "▸";
            Body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }

    /// <summary>Label / value rows (node info, plan summaries). Rows are kept by key and updated in place.</summary>
    public class AppInfoTable : VisualElement
    {
        readonly Dictionary<string, Label> _values = new();
        readonly Dictionary<string, VisualElement> _rows = new();

        public AppInfoTable() { AddToClassList("ssa-info"); }

        public int Count => _rows.Count;

        public void Set(string key, string value, StatusKind kind = StatusKind.Neutral)
        {
            if (!_values.TryGetValue(key, out var v))
            {
                var row = Ui.Box("ssa-info__row");
                row.Add(Ui.Label(key, "ssa-info__key"));
                v = Ui.Label("", "ssa-info__value");
                row.Add(v);
                Add(row);
                _values[key] = v;
                _rows[key] = row;
            }
            v.text = value ?? "";
            foreach (string c in new[] { "ssa-info__value--ok", "ssa-info__value--alert", "ssa-info__value--info" }) v.RemoveFromClassList(c);
            if (kind == StatusKind.Ok) v.AddToClassList("ssa-info__value--ok");
            else if (kind == StatusKind.Alert) v.AddToClassList("ssa-info__value--alert");
            else if (kind == StatusKind.Info) v.AddToClassList("ssa-info__value--info");
        }

        public string Get(string key) => _values.TryGetValue(key, out var v) ? v.text : null;

        public void Remove(string key)
        {
            if (_rows.TryGetValue(key, out var row)) row.RemoveFromHierarchy();
            _rows.Remove(key);
            _values.Remove(key);
        }

        public void ClearRows()
        {
            foreach (var r in _rows.Values) r.RemoveFromHierarchy();
            _rows.Clear();
            _values.Clear();
        }
    }

    /// <summary>A muted wrapping paragraph (notes under a control, warnings in plain words).</summary>
    public class AppNote : Label
    {
        public AppNote(string text = "") : base(text)
        {
            AddToClassList("ssa-paragraph");
        }

        public void Set(string text)
        {
            this.text = text ?? "";
            style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }

    /// <summary>A row of small buttons (Circularize at AP / at PE, Pick on map, Coordinates...).</summary>
    public class AppButtonRow : VisualElement
    {
        public AppButtonRow() { AddToClassList("ssa-button-row"); }

        public AppButton Add(string text, Action onClick, AppButton.Kind kind = AppButton.Kind.Plain)
        {
            var b = new AppButton(text, kind, onClick);
            b.AddToClassList("ssa-button--small");
            Add(b);
            return b;
        }
    }
}
