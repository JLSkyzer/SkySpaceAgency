using System.Linq;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using UnityEngine.UIElements;

// The new UI's components (plan 2026-11-19-new-ui-foundations, task 2): state and classes, without a panel.
public class AppControlsTests
{
    [Test]
    public void Switch_TogglesOnClick()
    {
        var s = new AppSwitch("Auto staging", true);
        Assert.IsTrue(s.ClassListContains(AppSwitch.OnClass));
        bool? raised = null;
        s.changed += v => raised = v;
        s.Toggle();
        Assert.IsFalse(s.value);
        Assert.AreEqual(false, raised);
        Assert.IsFalse(s.ClassListContains(AppSwitch.OnClass));
        s.SetValueWithoutNotify(true);
        Assert.AreEqual(false, raised, "no event without notify");
        s.SetEnabled(false);
        s.Toggle();
        Assert.IsTrue(s.value, "a disabled switch ignores clicks");
    }

    [Test]
    public void Segmented_SelectsAndRaises()
    {
        var seg = new AppSegmented(new[] { "Orbit", "Transfer", "Rendezvous" }, 0);
        int raised = -1;
        seg.changed += i => raised = i;
        Assert.IsTrue(seg.ItemAt(0).ClassListContains(AppSegmented.ActiveClass));
        seg.Select(2);
        Assert.AreEqual(2, seg.Selected);
        Assert.AreEqual(2, raised);
        Assert.IsFalse(seg.ItemAt(0).ClassListContains(AppSegmented.ActiveClass));
        Assert.IsTrue(seg.ItemAt(2).ClassListContains(AppSegmented.ActiveClass));
        raised = -1;
        seg.Select(5);
        seg.Select(2);
        Assert.AreEqual(-1, raised, "out of range and same index raise nothing");
    }

    [Test]
    public void Slider_FillFollowsValue()
    {
        var s = new AppSlider("Turn shape", 0, 100, 40, v => v.ToString("0") + " %");
        Assert.AreEqual(0.4f, s.Fraction, 1e-5f);
        Assert.AreEqual("40 %", s.Q<Label>(className: "ssa-slider__value").text);
        s.Value = 150;
        Assert.AreEqual(1f, s.Fraction, 1e-5f);
        Assert.AreEqual(0f, AppSlider.Fill(5, 10, 10), "empty range");
        Assert.IsNotNull(s.Q(className: "ssa-slider__fill"));
    }

    [Test]
    public void Pill_KindClass()
    {
        var p = new AppPill(StatusKind.Ok, "Gravity turn on track");
        Assert.IsTrue(p.ClassListContains("ssa-pill--ok"));
        p.Set(StatusKind.Alert, "Heat 92 %");
        Assert.IsTrue(p.ClassListContains("ssa-pill--alert"));
        Assert.IsFalse(p.ClassListContains("ssa-pill--ok"));
        Assert.AreEqual("Heat 92 %", p.Text);
    }

    [Test]
    public void Timeline_DotClasses()
    {
        var t = new AppTimeline();
        t.SetSteps(new[] { "Launch window", "Vertical climb", "Gravity turn", "Coast to Ap" }, 2);
        Assert.AreEqual(4, t.Dots.Count);
        Assert.IsTrue(t.Dots[0].ClassListContains("ssa-timeline__dot--done"));
        Assert.IsTrue(t.Dots[2].ClassListContains("ssa-timeline__dot--current"));
        Assert.IsNotNull(t.Dots[2].Q(className: "ssa-timeline__core"));
        Assert.IsTrue(t.Dots[3].ClassListContains("ssa-timeline__dot--pending"));
        t.SetCurrent(3);
        Assert.AreEqual(4, t.Dots.Count, "rebuilt, not added to");
        Assert.IsTrue(t.Dots[3].ClassListContains("ssa-timeline__dot--current"));
        Assert.AreEqual(4, t.Query(className: "ssa-timeline__step").ToList().Count);
    }

    [Test]
    public void TabBar_SelectRaisesAndMarksActive()
    {
        var bar = new AppTabBar();
        bar.AddTab("lift", "Lift");
        bar.AddTab("orbit", "Orbit");
        bar.AddTab("settings", "Settings", right: true);
        string raised = null;
        bar.selected += c => raised = c;
        Assert.AreEqual("lift", bar.Current);
        Assert.IsTrue(bar.Select("settings"));
        Assert.AreEqual("settings", raised);
        Assert.IsTrue(bar.TabElement("settings").ClassListContains(AppTabBar.ActiveClass));
        Assert.IsFalse(bar.TabElement("lift").ClassListContains(AppTabBar.ActiveClass));
        Assert.IsFalse(bar.Select("nothing"));
        // Order: left tabs, spacer, badge, right tabs.
        var kids = bar.Children().ToList();
        Assert.AreEqual("tab-lift", kids[0].name);
        Assert.AreEqual("tab-orbit", kids[1].name);
        Assert.AreEqual("tab-settings", kids[kids.Count - 1].name);
        Assert.AreEqual("LIFT", bar.TabElement("lift").Q<Label>().text);
        bar.SetEnabled("orbit", false);
        Assert.IsTrue(bar.TabElement("orbit").ClassListContains(AppTabBar.DisabledClass));
        Assert.IsFalse(bar.Select("orbit"));
    }

    [Test]
    public void ActionBar_ButtonKinds()
    {
        var bar = new AppActionBar("Next: coast at 41 km");
        int clicks = 0;
        bar.AddButton("Pause", AppButton.Kind.Plain);
        bar.AddButton("Abort", AppButton.Kind.Abort);
        var main = bar.AddButton("Running…", AppButton.Kind.Main, () => clicks++);
        Assert.IsTrue(main.ClassListContains("ssa-button--main"));
        Assert.IsTrue(bar.Buttons[1].ClassListContains("ssa-button--abort"));
        Assert.AreSame(main, bar.Children().Last(), "the main action is the last one, on the right");
        main.Click();
        Assert.AreEqual(1, clicks);
        main.SetEnabled(false);
        main.Click();
        Assert.AreEqual(1, clicks, "a disabled button does nothing");
        Assert.AreEqual("Next: coast at 41 km", bar.Note);
    }

    [Test]
    public void Tile_AndStageBar()
    {
        var tile = new AppTile("Apoapsis", "41.2", "km", AppTile.Kind.Gold);
        Assert.IsTrue(tile.ClassListContains("ssa-tile--gold"));
        tile.SetKind(AppTile.Kind.Alert);
        Assert.IsFalse(tile.ClassListContains("ssa-tile--gold"));
        Assert.IsTrue(tile.ClassListContains("ssa-tile--alert"));
        tile.SetValue("42.0");
        Assert.AreEqual("42.0", tile.Value);
        var stage = new AppStageBar("S1 booster", "1 950 m/s", 1.4f, active: true);
        Assert.AreEqual(1f, stage.Fraction);
        stage.SetFraction(float.NaN);
        Assert.AreEqual(0f, stage.Fraction);
        Assert.IsTrue(stage.ClassListContains("ssa-stage--gold"));
    }
}
