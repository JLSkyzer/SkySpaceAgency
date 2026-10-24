using System.Linq;
using K2D2.Rendezvous;
using K2D2.Transfer;
using K2UI.Tabs;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

// The window as the game builds it: K2D2_Window.uxml is the only Addressables entry, so each tab is a
// template of it. TabbedPage makes one button per TabPage, and K2Page.Init finds its page by code.
public class WindowLayoutTests
{
    const string WindowPath = "Assets/UI/K2D2_UI/K2D2_Window.uxml";

    static VisualElement CloneWindow()
    {
        var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(WindowPath);
        Assert.IsNotNull(asset, WindowPath);
        return asset.CloneTree();
    }

    [Test]
    public void TransferTab_IsATemplateOfTheWindow_WithEveryElementItsPageUses()
    {
        var root = CloneWindow();
        var tabs = root.Q<TabbedPage>();
        Assert.IsNotNull(tabs);
        var pages = tabs.Query<TabPage>().ToList();
        var names = pages.Select(p => p.name).ToList();
        CollectionAssert.AllItemsAreUnique(names);
        CollectionAssert.IsSubsetOf(new[] { "node", "orbit", "transfer", "lift", "landing", "dock", "attitude", "about" }, names);

        var transfer = pages.Single(p => p.name == "transfer");
        Assert.IsNotNull(transfer.Q("page"));
        Assert.IsNotNull(transfer.Q("settings"));
        foreach (string element in TransferUI.Elements)
            Assert.IsNotNull(transfer.Q(element), $"the Transfer tab has no element named {element}");
        Assert.IsNotNull(transfer.Q<DropdownField>("target_body"));
        Assert.IsNotNull(transfer.Q<K2UI.InlineEnum>("arrival"));
        Assert.IsNotNull(transfer.Q<K2UI.K2Toggle>("gravity_assist"));
        Assert.IsNotNull(transfer.Q<TextField>("max_trip_days"));
        Assert.IsNotNull(transfer.Q<Button>("cancel_saved"));
    }

    [Test]
    public void LiftTab_HasTheTransferGoalElements()
    {
        var lift = CloneWindow().Q<TabbedPage>().Query<TabPage>().ToList().Single(p => p.name == "lift");
        Assert.IsNotNull(lift.Q<DropdownField>("goal"));
        Assert.IsNotNull(lift.Q<DropdownField>("transfer_target"));
        Assert.IsNotNull(lift.Q<Label>("transfer_hint"));
        foreach (string element in new[] { "transfer_goal_group", "transfer_infos", "heading_row", "heading", "lift_infos" })
            Assert.IsNotNull(lift.Q(element), $"the Lift tab has no element named {element}");
    }

    [Test]
    public void RendezvousTab_TakesTheDockSlot_WithEveryElementItsPageUses()
    {
        var pages = CloneWindow().Q<TabbedPage>().Query<TabPage>().ToList();
        // The page code stays "dock": the window's template slot and the tab bar's styles use it.
        var page = pages.Single(p => p.name == "dock");
        Assert.AreEqual("Rendezvous", page.label);
        Assert.IsNotNull(page.Q("page"));
        Assert.IsNotNull(page.Q("settings"));
        foreach (string element in RendezvousUI.Elements)
            Assert.IsNotNull(page.Q(element), $"the Rendezvous tab has no element named {element}");
        Assert.IsNotNull(page.Q<DropdownField>("target_vessel"));
        Assert.IsNotNull(page.Q<DropdownField>("our_port"));
        Assert.IsNotNull(page.Q<DropdownField>("target_port"));
        Assert.IsNotNull(page.Q<K2UI.K2Toggle>("prepare_only"));
        foreach (string button in new[] { "rendezvous_dock", "rendezvous_only", "dock_only", "stop", "resume", "cancel_saved" })
            Assert.IsNotNull(page.Q<Button>(button), $"the Rendezvous tab has no button named {button}");
        // The old Dock assistant's controls are gone.
        foreach (string old in new[] { "align_dock", "main_brake", "rcs_final_approach", "cheat", "final_mode", "rcs_power" })
            Assert.IsNull(page.Q(old), $"the old Dock tab's {old} is still there");
    }
}
