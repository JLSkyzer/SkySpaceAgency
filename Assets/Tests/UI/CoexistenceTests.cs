using NUnit.Framework;
using SkySpaceAgency;
using UnityEngine;

// SkySpaceAgency and K2-D2 can be installed together (user decision 2026-11-19). Each value of
// ours that the game or another mod sees must differ from K2-D2's. K2-D2's values are written
// here literally, from IanMealworm/K2D2Redux upstream/main (8273fb8, K2-D2 1.4.1) and from the
// pull-request branch pr/k2d2 (K2-D2 1.5.0, our code with the K2-D2 identity), both read in the
// clone D:/KSPReduxModding/.k2d2-pr on 2026-11-19.
public class CoexistenceTests
{
    [Test]
    public void WindowShortcut_IsNotK2D2s()
    {
        // K2-D2: Input.GetKey(KeyCode.LeftAlt) && Input.GetKeyDown(KeyCode.O) (K2D2_Plugin.cs).
        var ours = ModIdentity.DefaultWindowShortcut;
        Assert.AreEqual(KeyCode.Y, ours.Main);
        CollectionAssert.AreEqual(new[] { KeyCode.LeftAlt }, ours.Modifiers);
        Assert.AreNotEqual(KeyCode.O, ours.Main);
        Assert.AreEqual("LeftAlt+Y", ours.ToString(), "the text Redux's settings show and save");
    }
}
