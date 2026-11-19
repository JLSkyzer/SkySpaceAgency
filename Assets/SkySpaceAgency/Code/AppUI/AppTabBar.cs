using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The horizontal tab bar: tabs on the left, SETTINGS on the right, an optional badge (PREVIEW,
    /// PLANNED) before it. The active tab is lit with a gold underline. Selection goes through TabModel.
    /// </summary>
    public class AppTabBar : VisualElement
    {
        public const string UssClass = "ssa-tabbar";
        public const string TabClass = "ssa-tab";
        public const string ActiveClass = "ssa-tab--active";
        public const string DisabledClass = "ssa-tab--disabled";

        readonly TabModel _model = new();
        readonly Dictionary<string, VisualElement> _tabs = new();
        readonly VisualElement _spacer, _badge;
        readonly Label _badgeLabel;

        public event Action<string> selected;

        public AppTabBar()
        {
            AddToClassList(UssClass);
            name = "tabs";
            _spacer = Ui.Box("ssa-tabbar__spacer");
            Add(_spacer);
            _badge = Ui.Box("ssa-badge");
            _badgeLabel = Ui.Label("", "ssa-badge__label");
            _badge.Add(_badgeLabel);
            _badge.style.display = DisplayStyle.None;
            Add(_badge);
        }

        public TabModel Model => _model;
        public string Current => _model.Current;
        public VisualElement TabElement(string code) => _tabs.TryGetValue(code, out var t) ? t : null;

        /// <summary>Adds a tab. Right-aligned tabs (SETTINGS) go after the spacer and the badge.</summary>
        public VisualElement AddTab(string code, string label, bool right = false)
        {
            var tab = Ui.Box(TabClass);
            tab.name = "tab-" + code;
            if (right) tab.AddToClassList(TabClass + "--right");
            tab.Add(Ui.Label(label.ToUpperInvariant(), "ssa-tab__label"));
            var underline = Ui.Box("ssa-tab__underline");
            SsaPalette.SetBackground(underline, SsaPalette.GoldGradient);
            tab.Add(underline);
            tab.AddManipulator(new Clickable(() => Select(code)));
            if (right) Add(tab);
            else Insert(IndexOf(_spacer), tab);
            _tabs[code] = tab;
            _model.Add(code);
            Refresh();
            return tab;
        }

        /// <summary>Shows the tab; false for an unknown or disabled one. Raises `selected` when the tab changed.</summary>
        public bool Select(string code)
        {
            string before = _model.Current;
            if (!_model.Select(code)) return false;
            Refresh();
            if (before != code) selected?.Invoke(code);
            return true;
        }

        public string Restore(string saved)
        {
            string code = _model.Restore(saved);
            Refresh();
            return code;
        }

        public void SetEnabled(string code, bool enabled)
        {
            _model.SetEnabled(code, enabled);
            Refresh();
        }

        public void SetBadge(string text)
        {
            _badgeLabel.text = text?.ToUpperInvariant() ?? "";
            _badge.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void Refresh()
        {
            foreach (var kv in _tabs)
            {
                kv.Value.EnableInClassList(ActiveClass, kv.Key == _model.Current);
                kv.Value.EnableInClassList(DisabledClass, !_model.IsEnabled(kv.Key));
            }
        }
    }
}
