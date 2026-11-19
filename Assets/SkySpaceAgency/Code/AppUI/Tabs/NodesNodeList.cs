using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>One maneuver node of the NODES tab's list: ring, "Node 1 · prograde", Δv, time to the node.</summary>
    public class NodesNodeItem : VisualElement
    {
        public const string NextClass = "ssa-nodes-item--next";
        public const string PastClass = "ssa-nodes-item__time--past";
        readonly Label _title, _dv, _time;

        public NodesNodeItem()
        {
            AddToClassList("ssa-nodes-item");
            Add(Ui.Box("ssa-nodes-item__ring"));
            var text = Ui.Box("ssa-nodes-item__text");
            _title = Ui.Label("", "ssa-nodes-item__title");
            _dv = Ui.Label("", "ssa-nodes-item__dv");
            text.Add(_title);
            text.Add(_dv);
            Add(text);
            _time = Ui.Label("", "ssa-nodes-item__time");
            Add(_time);
        }

        public string Title => _title.text;
        public string DeltaV => _dv.text;
        public string Time => _time.text;

        public void Set(int number, string direction, double dv, double secondsToNode, bool next)
        {
            string title = $"Node {number} · {direction}";
            if (_title.text != title) _title.text = title;
            string d = NodesLogic.Speed(dv) + " m/s";
            if (_dv.text != d) _dv.text = d;
            string t = NodesLogic.When(secondsToNode);
            if (_time.text != t) _time.text = t;
            _time.EnableInClassList(PastClass, secondsToNode < 0);
            EnableInClassList(NextClass, next);
        }
    }

    /// <summary>The list of the vessel's maneuver nodes, items kept and updated in place.</summary>
    public class NodesNodeList : VisualElement
    {
        readonly List<NodesNodeItem> _items = new();
        readonly Label _empty;

        public NodesNodeList()
        {
            AddToClassList("ssa-nodes-list");
            _empty = Ui.Label("No maneuver node. Plot one on the map, or circularize below.", "ssa-paragraph", "ssa-nodes-empty");
            Add(_empty);
        }

        public int Count { get; private set; }
        public NodesNodeItem ItemAt(int i) => _items[i];
        public bool EmptyShown => _empty.style.display != DisplayStyle.None;

        /// <summary>Shows count items (the first ones of the list), creating them only when the count grows.</summary>
        public void SetCount(int count)
        {
            if (count < 0) count = 0;
            if (count == Count) return;   // every frame in flight: nothing to restyle
            while (_items.Count < count)
            {
                var item = new NodesNodeItem();
                _items.Add(item);
                Add(item);
            }
            for (int i = 0; i < _items.Count; i++)
                _items[i].style.display = i < count ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.style.display = count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            Count = count;
        }
    }
}
