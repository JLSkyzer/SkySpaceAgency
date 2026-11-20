using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// A side column of a page (plan, telemetry, budget): a vertical scroll view of its own, so the
    /// three-column page keeps its height and each side column scrolls by itself. When the page is
    /// stacked (narrow Reflow) the column grows with its content instead and the page scrolls.
    /// </summary>
    public class AppColumn : ScrollView
    {
        public const string StackedClass = "ssa-col--stacked";

        public AppColumn(params string[] classes) : base(ScrollViewMode.Vertical)
        {
            AddToClassList("ssa-col");
            AddToClassList("ssa-colscroll");
            foreach (string c in classes) AddToClassList(c);
            horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            verticalScrollerVisibility = ScrollerVisibility.Auto;
            // The content is at least the column's height, so the card fills it down to the bottom.
            contentViewport.RegisterCallback<GeometryChangedEvent>(e =>
                contentContainer.style.minHeight = Stacked ? StyleKeyword.Null : (StyleLength)e.newRect.height);
            contentContainer.RegisterCallback<GeometryChangedEvent>(FollowContent);
        }

        public bool Stacked { get; private set; }

        // A ScrollView does not size itself to its content: stacked, its height follows the content's.
        void FollowContent(GeometryChangedEvent e)
        {
            if (Stacked && e.newRect.height > 0) style.height = e.newRect.height;
        }

        public void SetStacked(bool stacked)
        {
            if (stacked == Stacked) return;
            Stacked = stacked;
            EnableInClassList(StackedClass, stacked);
            if (stacked)
            {
                contentContainer.style.minHeight = StyleKeyword.Null;
                if (contentContainer.layout.height > 0) style.height = contentContainer.layout.height;
            }
            else
                style.height = StyleKeyword.Null;
        }

        /// <summary>True when the content is taller than the column (a scroll bar is needed).</summary>
        public bool Overflows => contentContainer.layout.height > contentViewport.layout.height + 0.5f;
    }

    /// <summary>
    /// A page of the new window, as the mockups lay it out: three columns (plan, view, telemetry and
    /// safety), then the phase timeline and the action bar, both fixed at the bottom. The page keeps
    /// its height: the plan and the side column scroll by themselves, the view does not. A narrow
    /// Reflow window stacks the columns (the view under the other two) and the whole page scrolls.
    /// </summary>
    public class AppPage : VisualElement
    {
        public const string UssClass = "ssa-page";
        readonly VisualElement _columns;
        readonly AppColumn _plan, _side;

        public ScrollView Scroll { get; }
        public VisualElement Plan => _plan;
        public VisualElement View { get; }
        public VisualElement Side => _side;
        public AppColumn PlanColumn => _plan;
        public AppColumn SideColumn => _side;
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
            _plan = new AppColumn("ssa-col--plan");
            View = Ui.Box("ssa-col", "ssa-col--view");
            _side = new AppColumn("ssa-col--side");
            _columns.Add(_plan);
            _columns.Add(View);
            _columns.Add(_side);
            Scroll.Add(_columns);
            Add(Scroll);
            // The columns are as high as the page's viewport (and so follow the window's size).
            Scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(e => FitColumns(e.newRect.height));
            Timeline = new AppTimeline();
            Timeline.style.display = DisplayStyle.None;
            Add(Timeline);
            Actions = new AppActionBar();
            Add(Actions);
        }

        float _viewportHeight;

        // Wide: the columns row is exactly the page's height; the plan and side columns stretch to it
        // and scroll, the view fills it. Stacked: everything grows with its content, the page scrolls.
        void FitColumns(float height)
        {
            if (height <= 0) return;
            _viewportHeight = height;
            Scroll.contentContainer.style.minHeight = height;
            _columns.style.height = Narrow ? StyleKeyword.Null : (StyleLength)height;
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
            _plan.SetStacked(narrow);
            _side.SetStacked(narrow);
            FitColumns(_viewportHeight);
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
        readonly VisualElement _body;
        readonly Dictionary<string, VisualElement> _pageByCode = new();
        StyleLength _heightBeforeMinimise, _minHeightBeforeMinimise;

        public AppHeader Header { get; }
        public AppHeaderData HeaderData { get; }
        public AppOpacityBar OpacityBar { get; }

        /// <summary>Raised when the player moves the opacity bar (the value to save).</summary>
        public event System.Action<float> opacityChanged;

        public float WindowOpacityValue { get; private set; } = WindowOpacity.Default;

        /// <summary>Sets the whole window's opacity (30 to 100 %). The bar follows without raising an event.</summary>
        public void SetWindowOpacity(float v)
        {
            v = WindowOpacity.Clamp(v);
            bool changed = !Mathf.Approximately(v, WindowOpacityValue);
            WindowOpacityValue = v;
            style.opacity = v;
            OpacityBar.SetValueWithoutNotify(v);
            if (changed) opacityChanged?.Invoke(v);
        }
        public AppTabBar Tabs { get; }
        public bool Narrow { get; private set; }
        public bool Minimised { get; private set; }

        public AppWindowView(string title, string slogan)
        {
            AddToClassList(UssClass);
            name = "app-window";
            SsaPalette.SetBackground(this, SsaPalette.WindowGradient);
            // Header, tabs and pages live in a body. In Reflow mode it simply fills the window; in Scale
            // mode it is laid out at the logical size and drawn shrunk (ApplyLayoutMode).
            _body = Ui.Box("ssa-body");
            _body.name = "app-body";
            hierarchy.Add(_body);
            Header = new AppHeader(title, slogan);
            _body.Add(Header);
            Tabs = new AppTabBar();
            _body.Add(Tabs);
            _pages = Ui.Box("ssa-pages");
            _pages.name = "pages";
            _body.Add(_pages);
            Tabs.selected += ShowPage;
            Tabs.selected += code => Tabs.SetBadge(TabFor(code)?.Badge);
            HeaderData = new AppHeaderData(Header);
            // The opacity bar sits in the bottom strip of the window, left of the resize handle, outside the
            // body (so it is neither scaled nor re-laid out in Scale mode).
            OpacityBar = new AppOpacityBar(WindowOpacity.Default);
            hierarchy.Add(OpacityBar);
            OpacityBar.changed += SetWindowOpacity;
            Header.MinimiseButton.clicked += () => SetMinimised(!Minimised);
            RegisterCallback<GeometryChangedEvent>(e => ApplyLayoutMode());
        }

        // ---- small-window mode ----

        SmallWindowMode _smallMode = SmallWindowMode.Reflow;

        /// <summary>The mode the window is in (Reflow until a window sets it).</summary>
        public SmallWindowMode SmallMode => _smallMode;

        /// <summary>The factor the content is drawn at: 1 in Reflow, SmallWindowScale.Factor in Scale.</summary>
        public float ContentScale { get; private set; } = 1;

        /// <summary>The size the content is laid out at (the window's inner size in Reflow).</summary>
        public Vector2 LogicalSize { get; private set; }

        public void SetSmallWindowMode(SmallWindowMode mode)
        {
            if (mode == _smallMode) return;
            _smallMode = mode;
            ApplyLayoutMode();
        }

        /// <summary>
        /// Reflow: the body fills the window, narrow and compact follow its real size (as before).
        /// Scale: the body is positioned over the window's inner box, laid out at inner / factor and drawn
        /// with that factor (transform from its top-left corner), so it fills the window exactly while
        /// the window keeps its real size. The layout is then never narrow nor compact, because the
        /// logical size is at least the reference.
        /// </summary>
        public void ApplyLayoutMode()
        {
            Rect inner = contentRect;
            if (inner.width <= 0 || inner.height <= 0) return;   // hidden
            bool scale = _smallMode == SmallWindowMode.Scale && !Minimised;
            if (!scale)
            {
                ContentScale = 1;
                LogicalSize = inner.size;
                _body.style.position = StyleKeyword.Null;
                _body.style.left = StyleKeyword.Null;
                _body.style.top = StyleKeyword.Null;
                _body.style.width = StyleKeyword.Null;
                _body.style.height = StyleKeyword.Null;
                _body.style.scale = StyleKeyword.Null;
                _body.style.transformOrigin = StyleKeyword.Null;
                UpdateNarrow(layout.width);
                return;
            }
            float s = SmallWindowScale.Factor(inner.size);
            Vector2 logical = SmallWindowScale.LogicalSize(inner.size, s);
            ContentScale = s;
            LogicalSize = logical;
            _body.style.position = Position.Absolute;
            _body.style.left = resolvedStyle.paddingLeft;
            _body.style.top = resolvedStyle.paddingTop;
            _body.style.width = logical.x;
            _body.style.height = logical.y;
            _body.style.transformOrigin = new TransformOrigin(0, 0, 0);
            _body.style.scale = new Scale(new Vector3(s, s, 1));
            UpdateNarrow(logical.x);
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
            var current = CurrentTab;
            current?.ShownPilot?.MarkShownInNewUi();
            current?.Tick();
            foreach (var t in _tabs)
                Tabs.TabElement(t.Code)?.EnableInClassList(RunningClass, SafeRunning(t));
        }

        /// <summary>Tells every tab the window closed (a pick waiting for a map click is cancelled).</summary>
        public void WindowClosed()
        {
            foreach (var t in _tabs)
            {
                try { t.OnWindowClosed(); }
                catch (System.Exception e) { UnityEngine.Debug.LogWarning($"[AppWindow] {t.Code}.OnWindowClosed: {e.Message}"); }
            }
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

        public void Restore(string savedTab)
        {
            string code = Tabs.Restore(savedTab);
            ShowPage(code);
            Tabs.SetBadge(TabFor(code)?.Badge);
        }

        public bool AnyRunning => _tabs.Exists(t => SafeRunning(t));

        static bool SafeRunning(AppTab t)
        {
            try { return t.IsRunning; } catch (System.Exception) { return false; }
        }

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
            ApplyLayoutMode();
            minimisedChanged?.Invoke(minimised);
        }
    }
}
