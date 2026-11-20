using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using UnityEngine;
using UnityEngine.UIElements;

// 2026-11-20: the page keeps its height and each side column scrolls by itself; the opacity bar.
public class ColumnScrollAndOpacityTests
{
    static readonly Vector2 FullHd = new Vector2(1920, 1080);
    static readonly string[] Codes = AppTabs.Create(AppTabContext.Editor()).Select(t => t.Code).ToArray();

    // ---- opacity logic ----

    [Test]
    public void Opacity_ClampsAndSnaps()
    {
        Assert.AreEqual(0.3f, WindowOpacity.Clamp(0.1f), 1e-5f);
        Assert.AreEqual(1f, WindowOpacity.Clamp(3f), 1e-5f);
        Assert.AreEqual(0.65f, WindowOpacity.Clamp(0.66f), 1e-5f);
        Assert.AreEqual(1f, WindowOpacity.Clamp(float.NaN));
        Assert.AreEqual(1f, WindowOpacity.Clamp(0));
        Assert.AreEqual(1f, WindowOpacity.Clamp(float.PositiveInfinity));
        Assert.AreEqual("70 %", WindowOpacity.Percent(0.7f));
        Assert.AreEqual("30 %", WindowOpacity.Percent(0.05f));
    }

    [Test]
    public void Opacity_SettingDefaultsToOpaque()
    {
        Assert.AreEqual(1f, AppSettings.Opacity.V);
        Assert.AreEqual("app_window_opacity", AppSettings.Opacity.key);
    }

    // ---- opacity on the window ----

    static AppUiHarness.Opened Open(string tab, Vector2 size, SmallWindowMode mode) =>
        AppUiHarness.Open(tab, new Rect(new Vector2(20, 20), size), FullHd, 1, mode);

    [TestCase(SmallWindowMode.Reflow)]
    [TestCase(SmallWindowMode.Scale)]
    public void Opacity_AppliesToTheWindowAndKeepsItsGeometry(SmallWindowMode mode)
    {
        using var o = Open("lift", new Vector2(1000, 600), mode);
        var v = o.View;
        Rect before = v.layout;
        float scaleBefore = v.ContentScale;
        float? saved = null;
        v.opacityChanged += x => saved = x;

        v.OpacityBar.Input.value = 50;   // the player drags the bar
        o.Layout();
        Assert.AreEqual(0.5f, v.resolvedStyle.opacity, 1e-3f);
        Assert.AreEqual(0.5f, saved);
        Assert.AreEqual(before, v.layout, "position and size untouched");
        Assert.AreEqual(scaleBefore, v.ContentScale, 1e-5f);

        v.SetWindowOpacity(0.01f);
        Assert.AreEqual(WindowOpacity.Min, v.resolvedStyle.opacity, 1e-3f, "never below the readable floor");
        Assert.AreEqual(30f, v.OpacityBar.Input.value, 1e-3f);
        v.SetWindowOpacity(1f);
        Assert.AreEqual(1f, v.resolvedStyle.opacity, 1e-3f);
    }

    [TestCase(SmallWindowMode.Reflow)]
    [TestCase(SmallWindowMode.Scale)]
    public void OpacityBar_SitsInTheBottomStrip_AwayFromTheResizeHandle(SmallWindowMode mode)
    {
        using var o = Open("land", new Vector2(1000, 600), mode);
        var v = o.View;
        Rect w = v.worldBound, bar = v.OpacityBar.worldBound;
        Assert.Greater(bar.width, 100);
        Assert.LessOrEqual(bar.yMax, w.yMax + 0.5f, "inside the window");
        Assert.GreaterOrEqual(bar.yMin, w.yMax - 30, "in the bottom strip");
        // The resize handle is 24 x 24 px in the corner: the bar stays clear of it.
        Assert.LessOrEqual(bar.xMax, w.xMax - 24, "left of the handle");
        // Not under the action bar's buttons: they end above the strip.
        var page = (AppPage)v.PageFor("land");
        Assert.LessOrEqual(page.Actions.worldBound.yMax, bar.yMin + 6, "below the action bar");
        Assert.IsNull(v.OpacityBar.parent.Q("app-body")?.Q("opacity-bar"), "not inside the scaled body");
    }

    // ---- column scrolling ----

    static void Grow(AppPage page)
    {
        // Tall content in both side columns, whatever the tab holds.
        foreach (var col in new[] { page.Plan, page.Side })
            col.Add(new VisualElement { name = "test-filler", style = { height = 1600, flexShrink = 0 } });
        foreach (var s in page.Query<AppSection>().ToList()) s.SetOpen(true);
    }

    static System.Collections.Generic.IEnumerable<TestCaseData> WideCases()
    {
        foreach (string code in Codes)
            foreach (var mode in new[] { SmallWindowMode.Reflow, SmallWindowMode.Scale })
                yield return new TestCaseData(code, mode).SetName($"Columns_{code}_{mode}_ScrollByThemselves");
    }

    [TestCaseSource(nameof(WideCases))]
    public void Columns_ScrollByThemselves_WhenTheyHoldMore(string tab, SmallWindowMode mode)
    {
        using var o = Open(tab, AppWindowGeometry.DefaultSize, mode);
        o.View.Select(tab);
        o.Layout();
        var page = (AppPage)o.View.PageFor(tab);
        float timelineY = page.Timeline.worldBound.yMin, actionsY = page.Actions.worldBound.yMin;
        float viewHeight = page.View.layout.height, pageContent = page.Scroll.contentContainer.layout.height;

        Grow(page);
        o.Layout();

        Assert.IsTrue(page.PlanColumn.Overflows, "the plan column has its own scroll");
        Assert.IsTrue(page.SideColumn.Overflows, "and so has the side column");
        Assert.LessOrEqual(page.Scroll.contentContainer.layout.height, page.Scroll.contentViewport.layout.height + 1,
            "the page itself does not scroll");
        Assert.AreEqual(pageContent, page.Scroll.contentContainer.layout.height, 1f, "the page keeps its height");
        Assert.AreEqual(viewHeight, page.View.layout.height, 1f, "the centre view keeps its size");
        Assert.AreEqual(timelineY, page.Timeline.worldBound.yMin, 1f, "the timeline stays");
        Assert.AreEqual(actionsY, page.Actions.worldBound.yMin, 1f, "and the action bar");
        Assert.AreEqual(page.PlanColumn.contentViewport.layout.height, page.View.layout.height, 1f);

        // Scrolling a column moves its content only.
        float planTop = page.Plan.Q("test-filler").worldBound.yMin;
        float sideTop = page.Side.Q("test-filler").worldBound.yMin;
        page.PlanColumn.scrollOffset = new Vector2(0, 300);
        o.Layout();
        Assert.AreEqual(planTop - 300, page.Plan.Q("test-filler").worldBound.yMin, 2f, "the plan scrolled");
        Assert.AreEqual(sideTop, page.Side.Q("test-filler").worldBound.yMin, 1f, "the side column did not");
        Assert.AreEqual(viewHeight, page.View.layout.height, 1f);
    }

    [Test]
    public void Columns_TheCardFillsTheColumn_WhenTheContentIsShort()
    {
        int checkedColumns = 0;
        foreach (string tab in Codes)
        {
            using var o = Open(tab, new Vector2(1600, 1000), SmallWindowMode.Reflow);
            o.View.Select(tab);
            o.Layout();
            var page = (AppPage)o.View.PageFor(tab);
            foreach (var col in new[] { page.PlanColumn, page.SideColumn })
            {
                var card = col.Q<AppCard>();
                if (card == null || col.Overflows) continue;
                checkedColumns++;
                Assert.AreEqual(col.contentViewport.layout.height, card.layout.height, 1.5f,
                    tab + ": the card goes down to the bottom of the page, as in the mockups");
            }
        }
        Assert.Greater(checkedColumns, 8, "a tall window leaves most columns short");
    }

    [TestCase("lift")]
    [TestCase("land")]
    [TestCase("transfer")]
    public void Reflow_Narrow_StacksTheColumnsAndThePageScrolls(string tab)
    {
        using var o = Open(tab, AppWindowGeometry.MinSize, SmallWindowMode.Reflow);
        o.View.Select(tab);
        o.Layout();
        var page = (AppPage)o.View.PageFor(tab);
        Assert.IsTrue(page.Narrow);
        Assert.IsTrue(page.PlanColumn.Stacked);
        Assert.IsTrue(page.SideColumn.Stacked);
        Grow(page);
        o.Layout();
        Assert.IsFalse(page.PlanColumn.Overflows, "a stacked column grows with its content");
        Assert.Greater(page.Scroll.contentContainer.layout.height, page.Scroll.contentViewport.layout.height + 100,
            "so the whole page scrolls");
        Assert.AreEqual(DisplayStyle.Flex, page.Timeline.resolvedStyle.display);
    }

    [Test]
    public void Scale_KeepsTheColumnScrollingToo()
    {
        using var o = Open("land", new Vector2(760, 480), SmallWindowMode.Scale);
        o.View.Select("land");
        o.Layout();
        var page = (AppPage)o.View.PageFor("land");
        Assert.IsFalse(page.PlanColumn.Stacked);
        Grow(page);
        o.Layout();
        Assert.IsTrue(page.PlanColumn.Overflows);
        Assert.LessOrEqual(page.Scroll.contentContainer.layout.height, page.Scroll.contentViewport.layout.height + 1);
    }
}
