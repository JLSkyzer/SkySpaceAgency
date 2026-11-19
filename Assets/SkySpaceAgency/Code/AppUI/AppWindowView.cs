using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// A page of the new window, as the mockups lay it out: three columns (plan, view, telemetry and
    /// safety), then the phase timeline and the action bar. The columns scroll when the window is too
    /// small for them; a narrow window puts the view on its own row under the other two.
    /// </summary>
    public class AppPage : VisualElement
    {
        public const string UssClass = "ssa-page";
        readonly VisualElement _columns;

        public ScrollView Scroll { get; }
        public VisualElement Plan { get; }
        public VisualElement View { get; }
        public VisualElement Side { get; }
        public AppTimeline Timeline { get; }
        public AppActionBar Actions { get; }
        public bool Narrow { get; private set; }

        public AppPage(string code)
        {
            AddToClassList(UssClass);
            name = "page-" + code;
            Scroll = new ScrollView(ScrollViewMode.Vertical);
            Scroll.AddToClassList("ssa-page__scroll");
            Scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _columns = Ui.Box("ssa-columns");
            Plan = Ui.Box("ssa-col", "ssa-col--plan");
            View = Ui.Box("ssa-col", "ssa-col--view");
            Side = Ui.Box("ssa-col", "ssa-col--side");
            _columns.Add(Plan);
            _columns.Add(View);
            _columns.Add(Side);
            Scroll.Add(_columns);
            Add(Scroll);
            // The columns fill the page's height (and so follow the window's size); they only scroll
            // when their content is taller than the page.
            Scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(e =>
                Scroll.contentContainer.style.minHeight = e.newRect.height);
            Timeline = new AppTimeline();
            Timeline.style.display = DisplayStyle.None;
            Add(Timeline);
            Actions = new AppActionBar();
            Add(Actions);
        }

        public void SetTimeline(IReadOnlyList<string> steps, int current)
        {
            Timeline.SetSteps(steps, current);
            Timeline.style.display = steps != null && steps.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Narrow: plan and side on the first row, the view under them (the USS wraps the row).</summary>
        public void SetNarrow(bool narrow)
        {
            if (narrow == Narrow) return;
            Narrow = narrow;
            View.RemoveFromHierarchy();
            if (narrow) _columns.Add(View);
            else _columns.Insert(1, View);
        }
    }

    /// <summary>
    /// The frame of the new window: header, tab bar, pages. Built in code (the Addressables UXML only
    /// carries the style sheet), so the EditMode harness lays it out without the game.
    /// </summary>
    public class AppWindowView : VisualElement
    {
        public const string UssClass = "ssa-app";
        public const string NarrowClass = "ssa-app--narrow";
        public const string MinimisedClass = "ssa-app--minimised";

        readonly VisualElement _pages;
        readonly Dictionary<string, VisualElement> _pageByCode = new();
        StyleLength _heightBeforeMinimise, _minHeightBeforeMinimise;

        public AppHeader Header { get; }
        public AppTabBar Tabs { get; }
        public bool Narrow { get; private set; }
        public bool Minimised { get; private set; }

        public AppWindowView(string title, string slogan)
        {
            AddToClassList(UssClass);
            name = "app-window";
            SsaPalette.SetBackground(this, SsaPalette.WindowGradient);
            Header = new AppHeader(title, slogan);
            Add(Header);
            Tabs = new AppTabBar();
            Add(Tabs);
            _pages = Ui.Box("ssa-pages");
            _pages.name = "pages";
            Add(_pages);
            Tabs.selected += ShowPage;
            Header.MinimiseButton.clicked += () => SetMinimised(!Minimised);
            RegisterCallback<GeometryChangedEvent>(e => UpdateNarrow(e.newRect.width));
        }

        readonly List<AppTab> _tabs = new();

        public IReadOnlyList<AppTab> TabList => _tabs;
        public AppTab CurrentTab => _tabs.Find(t => t.Code == Tabs.Current);
        public AppTab TabFor(string code) => _tabs.Find(t => t.Code == code);

        /// <summary>Builds the tab's page and adds it with its tab button.</summary>
        public void AddTab(AppTab tab)
        {
            var page = tab.Build();
            _tabs.Add(tab);
            AddPage(tab.Code, tab.Label, page, tab.RightAligned);
        }

        public const string RunningClass = "ssa-tab--running";

        /// <summary>Every frame while open: the shown tab updates, every tab's button lights while its pilot flies.</summary>
        public void Tick()
        {
            CurrentTab?.Tick();
            foreach (var t in _tabs)
                Tabs.TabElement(t.Code)?.EnableInClassList(RunningClass, t.IsRunning);
        }

        public IEnumerable<string> PageCodes => _pageByCode.Keys;
        public VisualElement PageFor(string code) => _pageByCode.TryGetValue(code, out var p) ? p : null;

        public void AddPage(string code, string label, VisualElement page, bool right = false)
        {
            _pageByCode[code] = page;
            page.style.display = DisplayStyle.None;
            _pages.Add(page);
            Tabs.AddTab(code, label, right);
            ShowPage(Tabs.Current);
        }

        public bool Select(string code) => Tabs.Select(code);

        void ShowPage(string code)
        {
            foreach (var kv in _pageByCode)
                kv.Value.style.display = kv.Key == code ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public void Restore(string savedTab) => ShowPage(Tabs.Restore(savedTab));

        public void UpdateNarrow(float width)
        {
            if (width <= 0) return;   // hidden
            bool narrow = AppWindowGeometry.IsNarrow(width);
            Narrow = narrow;
            EnableInClassList(NarrowClass, narrow);
            foreach (var p in _pageByCode.Values)
                if (p is AppPage page) page.SetNarrow(narrow);
        }

        /// <summary>Raised after SetMinimised changed the state (true: minimised).</summary>
        public event System.Action<bool> minimisedChanged;

        /// <summary>
        /// Minimised, only the header shows; the inline height and minimum height (set by the resize
        /// handle, the size saver, the defaults) come back as they were when it is restored.
        /// </summary>
        public void SetMinimised(bool minimised)
        {
            if (minimised == Minimised) return;
            Minimised = minimised;
            if (minimised)
            {
                _heightBeforeMinimise = style.height;
                _minHeightBeforeMinimise = style.minHeight;
                style.height = StyleKeyword.Auto;
                style.minHeight = StyleKeyword.Auto;
            }
            else
            {
                style.height = _heightBeforeMinimise;
                style.minHeight = _minHeightBeforeMinimise;
            }
            EnableInClassList(MinimisedClass, minimised);
            minimisedChanged?.Invoke(minimised);
        }
    }
}
