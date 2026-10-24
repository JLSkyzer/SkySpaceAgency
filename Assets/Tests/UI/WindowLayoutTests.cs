using System.Linq;
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
}
