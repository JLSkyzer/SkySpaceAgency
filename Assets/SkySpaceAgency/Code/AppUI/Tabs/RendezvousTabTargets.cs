using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The TARGET card's list (docs/design/ui/rendezvous.png): one row per vessel, name on the left,
    /// distance on the right, the chosen one with a gold border; each body's name above its vessels
    /// (the classic drop-down's headers). The rows are rebuilt only when the list changes; distances
    /// and the selection are updated in place.
    /// </summary>
    public class RendezvousTargetList : VisualElement
    {
        public const string RowClass = "ssa-rendezvous-target";
        public const string SelectedClass = "ssa-rendezvous-target--selected";
        public const string HeaderClass = "ssa-rendezvous-body";

        public struct Item
        {
            public string Id;        // null: a body's header
            public string Label;
            public string Detail;    // "182 km", or the body's name for a far vessel
        }

        readonly ScrollView _scroll;
        readonly List<(VisualElement row, Label detail, string id)> _rows = new();
        readonly List<string> _keys = new();
        string _selected;

        public event Action<string> picked;

        public RendezvousTargetList()
        {
            AddToClassList("ssa-rendezvous-targets");
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("ssa-rendezvous-targets__scroll");
            _scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _scroll.verticalScrollerVisibility = ScrollerVisibility.Auto;   // only when the vessels overflow
            Add(_scroll);
        }

        public int RowCount => _rows.FindAll(r => r.id != null).Count;
        public string Selected => _selected;

        public void SetItems(IReadOnlyList<Item> items, string selected)
        {
            bool same = items.Count == _keys.Count;
            for (int i = 0; same && i < items.Count; i++)
                same = _keys[i] == Key(items[i]);
            if (!same)
            {
                _scroll.Clear();
                _rows.Clear();
                _keys.Clear();
                foreach (var it in items)
                {
                    _keys.Add(Key(it));
                    if (it.Id == null)
                    {
                        var head = Ui.Label(it.Label?.Trim() ?? "", HeaderClass);
                        _scroll.Add(head);
                        _rows.Add((head, null, null));
                        continue;
                    }
                    var row = Ui.Box(RowClass);
                    row.Add(Ui.Label(it.Label?.Trim() ?? "", "ssa-rendezvous-target__name"));
                    var detail = Ui.Label("", "ssa-rendezvous-target__detail");
                    row.Add(detail);
                    string id = it.Id;
                    row.AddManipulator(new Clickable(() => { if (row.enabledInHierarchy) picked?.Invoke(id); }));
                    _scroll.Add(row);
                    _rows.Add((row, detail, id));
                }
            }
            for (int i = 0; i < items.Count && i < _rows.Count; i++)
                if (_rows[i].detail != null && _rows[i].detail.text != (items[i].Detail ?? ""))
                    _rows[i].detail.text = items[i].Detail ?? "";
            Select(selected);
        }

        public void Select(string id)
        {
            _selected = id;
            foreach (var (row, _, rowId) in _rows)
                if (rowId != null) row.EnableInClassList(SelectedClass, rowId == id);
        }

        static string Key(Item it) => (it.Id ?? "#") + "|" + it.Label;
    }

    /// <summary>The CHECKS list: a coloured dot and a line per check, updated in place.</summary>
    public class RendezvousCheckList : VisualElement
    {
        readonly List<(VisualElement row, VisualElement dot, Label text)> _rows = new();

        public RendezvousCheckList() { AddToClassList("ssa-rendezvous-checks"); }

        public int Count { get; private set; }
        public string TextAt(int i) => i < Count ? _rows[i].text.text : null;

        public void Set(IReadOnlyList<RendezvousCheck> checks)
        {
            while (_rows.Count < checks.Count)
            {
                var row = Ui.Box("ssa-rendezvous-check");
                var dot = Ui.Box("ssa-rendezvous-check__dot");
                var text = Ui.Label("", "ssa-rendezvous-check__text");
                row.Add(dot);
                row.Add(text);
                Add(row);
                _rows.Add((row, dot, text));
            }
            Count = checks.Count;
            for (int i = 0; i < _rows.Count; i++)
            {
                var (row, dot, text) = _rows[i];
                bool shown = i < checks.Count;
                row.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
                if (!shown) continue;
                if (text.text != checks[i].Text) text.text = checks[i].Text ?? "";
                foreach (var k in new[] { StatusKind.Ok, StatusKind.Alert, StatusKind.Info, StatusKind.Neutral })
                    dot.EnableInClassList("ssa-rendezvous-check__dot--" + k.ToString().ToLowerInvariant(), k == checks[i].Kind);
            }
        }
    }
}
