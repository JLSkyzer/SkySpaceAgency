using System.Linq;
using NUnit.Framework;
using SkySpaceAgency.AppUI;
using SkySpaceAgency.Node;
using UnityEngine;
using UnityEngine.UIElements;

// The NODES tab of the new window (classic Node and Attitude tabs), plan 2026-11-19-new-ui-tab-migration.
public class NodesTabTests
{
    static NodesTab Build()
    {
        var tab = new NodesTab(AppTabContext.Editor());
        tab.Build();
        return tab;
    }

    static AppSwitch FindSwitch(VisualElement root, string label) =>
        root.Query<AppSwitch>().ToList().First(s => s.Q<Label>(className: "ssa-switch__label").text == label);

    static AppSlider FindSlider(VisualElement root, string label) =>
        root.Query<AppSlider>().ToList().First(s => s.Q<Label>(className: "ssa-slider__label").text == label);

    // ---- the tab ----

    [Test]
    public void BuildsAndTicksWithoutTheGame()
    {
        var tab = Build();
        Assert.DoesNotThrow(() => { tab.Tick(); tab.Tick(); });
        Assert.AreEqual("nodes", tab.Code);
        Assert.AreEqual("Nodes", tab.Label);
        Assert.IsFalse(tab.IsRunning);
        Assert.IsNull(tab.Hud);
        Assert.IsFalse(tab.MainButton.enabledSelf, "no pilot in the editor: Start disabled");
        Assert.IsFalse(tab.AbortButton.enabledSelf);
        Assert.IsFalse(tab.CircularizeAp.enabledSelf);
        Assert.IsFalse(tab.HoldButton.enabledSelf);
        Assert.AreEqual("Start", tab.MainButton.Text);
        Assert.DoesNotThrow(() => { tab.MainButton.Click(); tab.HoldButton.Click(); tab.CircularizePe.Click(); });
        Assert.AreEqual("Attitude autopilot not enabled", tab.HoldCard.Note);
        Assert.IsTrue(tab.Nodes.EmptyShown);
        Assert.AreEqual("none", tab.NodeInfo.Get("Node"));
        Assert.AreEqual(NodesLogic.NodeSteps.Length, tab.Page.Timeline.Model.Steps.Count);
    }

    [Test]
    public void EverySettingOfTheTab_IsBound()
    {
        Build();
        foreach (string key in NodesLogic.SettingKeys)
            Assert.That(AppBind.Bound, Does.Contain(key), key);
    }

    [Test]
    public void SettingKeys_AreTheRealOnes()
    {
        var s = new NodeExSettings();
        foreach (string key in new[] { s.auto_warp.key, s.pause_on_end.key, s.start_mode.path, s.start_before.key, BurnWarpSettings.mode.path })
            Assert.That(NodesLogic.SettingKeys, Does.Contain(key));
    }

    [Test]
    public void Bind_FollowsTheSetting()
    {
        var tab = Build();
        var sw = FindSwitch(tab.Page, "Pause the game when done");
        bool before = tab.Settings.pause_on_end.V;
        try
        {
            tab.Settings.pause_on_end.V = !before;
            Assert.AreEqual(!before, sw.value);
            sw.value = before;
            Assert.AreEqual(before, tab.Settings.pause_on_end.V, "the switch writes the setting");
        }
        finally { tab.Settings.pause_on_end.V = before; }

        var mode = BurnWarpSettings.mode.V;
        try
        {
            BurnWarpSettings.mode.V = BurnWarpMode.Physics;
            Assert.AreEqual(1, tab.WarpDuringBurns.Selected);
            tab.WarpDuringBurns.Select(2);
            Assert.AreEqual(BurnWarpMode.Rails, BurnWarpSettings.mode.V);
        }
        finally { BurnWarpSettings.mode.V = mode; }
    }

    [Test]
    public void StartBefore_ShowsOnlyForBeforeT0()
    {
        var tab = Build();
        var slider = FindSlider(tab.Page, "Start before T0");
        var mode = tab.Settings.start_mode.V;
        try
        {
            tab.Settings.start_mode.V = NodeExSettings.StartMode.constant;
            Assert.AreEqual(DisplayStyle.Flex, slider.style.display.value);
            tab.Settings.start_mode.V = NodeExSettings.StartMode.half_duration;
            Assert.AreEqual(DisplayStyle.None, slider.style.display.value);
        }
        finally { tab.Settings.start_mode.V = mode; }
    }

    [Test]
    public void ShowSample_FillsThePage()
    {
        var tab = Build();
        tab.ShowSample();
        Assert.AreEqual(2, tab.Nodes.Count);
        Assert.IsFalse(tab.Nodes.EmptyShown);
        Assert.AreEqual("Node 1 · prograde", tab.Nodes.ItemAt(0).Title);
        Assert.AreEqual("412 m/s", tab.Nodes.ItemAt(0).DeltaV);
        Assert.AreEqual("in 4:12", tab.Nodes.ItemAt(0).Time);
        Assert.AreEqual("in 21:40", tab.Nodes.ItemAt(1).Time);
        Assert.IsTrue(tab.Nodes.ItemAt(0).ClassListContains(NodesNodeItem.NextClass));
        Assert.AreEqual("286", tab.DvLeft.Value);
        Assert.AreEqual("0:41", tab.BurnTime.Value);
        Assert.AreEqual("100", tab.Throttle.Value);
        Assert.AreEqual("x4", tab.Warp.Value);
        Assert.AreEqual(1, tab.WarpDuringBurns.Selected);
        Assert.IsTrue(tab.Burn.HasBurn);
        Assert.AreEqual("aligned to 0.8°", tab.Burn.NoteText);
        Assert.AreEqual("Burning, target orbit converging", tab.Status.Pill.Text);
        Assert.AreEqual(StatusKind.Ok, tab.Status.Pill.Kind);
        Assert.AreEqual("126 of 412 m/s", tab.AppliedNote.text);
        Assert.AreEqual(3, tab.Page.Timeline.Model.Current);
        Assert.AreEqual("Burn", tab.Page.Timeline.Model.Steps[3]);
        Assert.AreEqual("Running…", tab.MainButton.Text);
        Assert.DoesNotThrow(tab.Tick, "the sample stays when ticked");
        Assert.AreEqual("286", tab.DvLeft.Value);
    }

    [Test]
    public void NodeList_KeepsItsItems()
    {
        var list = new NodesNodeList();
        list.SetCount(3);
        var first = list.ItemAt(0);
        list.SetCount(1);
        list.SetCount(2);
        Assert.AreSame(first, list.ItemAt(0));
        Assert.AreEqual(2, list.Count);
        list.ItemAt(1).Set(2, "retrograde", 3.42, -65, false);
        Assert.AreEqual("3.4 m/s", list.ItemAt(1).DeltaV);
        Assert.AreEqual("1:05 ago", list.ItemAt(1).Time);
        Assert.IsTrue(list.ItemAt(1).Q<Label>(className: "ssa-nodes-item__time").ClassListContains(NodesNodeItem.PastClass));
        list.SetCount(0);
        Assert.IsTrue(list.EmptyShown);
    }

    [Test]
    public void BurnView_RepaintsOnlyWhenItMoves()
    {
        var view = new NodesBurnView();
        int n = view.Repaints;
        Assert.IsTrue(view.Set(true, 12f, Vector2.up, 3, 0.5f));
        Assert.IsFalse(view.Set(true, 12.01f, Vector2.up, 3.05f, 0.5f), "below the thresholds");
        Assert.IsTrue(view.Set(true, 12.2f, Vector2.up, 3, 0.5f));
        Assert.IsTrue(view.Set(false, float.NaN, Vector2.zero, 3, 0.5f));
        Assert.AreEqual(n + 3, view.Repaints);
        Assert.AreEqual("no burn to point at", view.NoteText);
    }

    // ---- pure logic ----

    [Test]
    public void Clock_Formats()
    {
        Assert.AreEqual("0:41", NodesLogic.Clock(41.7));
        Assert.AreEqual("4:12", NodesLogic.Clock(252));
        Assert.AreEqual("1:02:03", NodesLogic.Clock(3723));
        Assert.AreEqual("-0:05", NodesLogic.Clock(-5));
        Assert.AreEqual("—", NodesLogic.Clock(double.NaN));
        Assert.AreEqual("in 21:40", NodesLogic.When(1300));
        Assert.AreEqual("0:10 ago", NodesLogic.When(-10));
    }

    [Test]
    public void Direction_IsTheLargestComponent()
    {
        Assert.AreEqual("prograde", NodesLogic.Direction(1, 2, 412));
        Assert.AreEqual("retrograde", NodesLogic.Direction(0, 0, -5));
        Assert.AreEqual("normal", NodesLogic.Direction(1, 38, 3));
        Assert.AreEqual("anti-normal", NodesLogic.Direction(0, -38, 3));
        Assert.AreEqual("radial out", NodesLogic.Direction(20, 3, 3));
        Assert.AreEqual("radial in", NodesLogic.Direction(-20, 3, 3));
        Assert.AreEqual("empty", NodesLogic.Direction(0, 0, 0));
    }

    [Test]
    public void Phase_FollowsThePilotMode()
    {
        string[] steps;
        Assert.AreEqual(0, NodesLogic.Phase(NodeExPilot.Mode.Turn, false, TargetOrbitBurn.Mode.Warp, out steps));
        Assert.AreSame(NodesLogic.NodeSteps, steps);
        Assert.AreEqual(1, NodesLogic.Phase(NodeExPilot.Mode.Warp, false, TargetOrbitBurn.Mode.Warp, out _));
        Assert.AreEqual(2, NodesLogic.Phase(NodeExPilot.Mode.Burn, true, TargetOrbitBurn.Mode.Warp, out _));
        Assert.AreEqual(3, NodesLogic.Phase(NodeExPilot.Mode.Burn, false, TargetOrbitBurn.Mode.Warp, out _));
        Assert.AreEqual(1, NodesLogic.Phase(NodeExPilot.Mode.Circularize, false, TargetOrbitBurn.Mode.Align, out steps));
        Assert.AreSame(NodesLogic.ClosedLoopSteps, steps);
        Assert.AreEqual(2, NodesLogic.Phase(NodeExPilot.Mode.TargetOrbit, false, TargetOrbitBurn.Mode.Burn, out _));
        Assert.AreEqual(3, NodesLogic.Phase(NodeExPilot.Mode.NextStep, false, TargetOrbitBurn.Mode.Warp, out _));
        Assert.AreEqual(0, NodesLogic.Phase(NodeExPilot.Mode.Off, false, TargetOrbitBurn.Mode.Warp, out steps));
        Assert.AreSame(NodesLogic.NodeSteps, steps);
    }

    [Test]
    public void IgnitionUT_FollowsTheStartMode()
    {
        Assert.AreEqual(1000, NodesLogic.IgnitionUT(NodeExSettings.StartMode.precise, 1000, 60, 5));
        Assert.AreEqual(970, NodesLogic.IgnitionUT(NodeExSettings.StartMode.half_duration, 1000, 60, 5));
        Assert.AreEqual(995, NodesLogic.IgnitionUT(NodeExSettings.StartMode.constant, 1000, 60, 5));
    }

    [Test]
    public void AppliedAndBurnTimeLeft()
    {
        Assert.AreEqual(126f / 412f, NodesLogic.Applied(412, 286), 1e-5);
        Assert.AreEqual(0, NodesLogic.Applied(0, 5));
        Assert.AreEqual(0, NodesLogic.Applied(412, double.NaN));
        Assert.AreEqual(1, NodesLogic.Applied(412, -3));
        Assert.AreEqual(30, NodesLogic.BurnTimeLeft(60, 400, 200), 1e-9);
        Assert.AreEqual(60, NodesLogic.BurnTimeLeft(60, 400, double.NaN));
        Assert.AreEqual(60, NodesLogic.BurnTimeLeft(60, 400, 900), "clamped to the whole burn");
        Assert.AreEqual("412", NodesLogic.Speed(412.4));
        Assert.AreEqual("3.4", NodesLogic.Speed(3.42));
    }

    [Test]
    public void BurnGeometry()
    {
        Assert.AreEqual(Vector2.zero, NodesLogic.MarkerOffset(0, Vector2.up, 100));
        Assert.AreEqual(50, NodesLogic.MarkerOffset(30, Vector2.right, 100).x, 1e-3);
        Assert.AreEqual(100, NodesLogic.MarkerOffset(90, new Vector2(0, -3), 100).magnitude, 1e-3, "direction normalised");
        Assert.AreEqual(100, NodesLogic.MarkerOffset(150, Vector2.right, 100).x, 1e-3, "behind: on the rim");
        Assert.IsTrue(NodesLogic.MarkerBehind(150));
        Assert.IsFalse(NodesLogic.MarkerBehind(60));
        Assert.AreEqual(Vector2.zero, NodesLogic.MarkerOffset(float.NaN, Vector2.up, 100));

        Assert.AreEqual(0, NodesLogic.HorizonOffset(0, 100), 1e-4);
        Assert.AreEqual(50, NodesLogic.HorizonOffset(30, 100), 1e-3, "nose up: horizon below the centre");
        Assert.AreEqual(100, NodesLogic.HorizonOffset(120, 100), 1e-3, "clamped at 90°");

        Assert.AreEqual(new Vector2(0, 180), NodesLogic.GroundArc(0, 100).Value);
        var a = NodesLogic.GroundArc(50, 100).Value;
        Assert.AreEqual(30, a.x, 1e-3);
        Assert.AreEqual(150, a.y, 1e-3);
        Assert.IsNull(NodesLogic.GroundArc(100, 100), "horizon at the bottom: no ground");
        Assert.AreEqual(new Vector2(0, 360), NodesLogic.GroundArc(-100, 100).Value);
    }

    [Test]
    public void AlignNote_SaysHowFar()
    {
        Assert.AreEqual("aligned to 0.8°", NodesLogic.AlignNote(true, 0.8f, 2, out var k));
        Assert.AreEqual(StatusKind.Ok, k);
        Assert.AreEqual("12° from the burn", NodesLogic.AlignNote(true, 12.3f, 2, out k));
        Assert.AreEqual(StatusKind.Info, k);
        Assert.AreEqual("no burn to point at", NodesLogic.AlignNote(false, 1, 2, out k));
        Assert.AreEqual(StatusKind.Neutral, k);
    }
}
