using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using F = VabFixtures;

// Spec, choix 3: only the main rocket is replaced, detached assemblies stay.
public class VabSnapshotTests
{
    [Test]
    public void ReplaceMain_ReplacesOnlyTheMainAssembly()
    {
        JObject ws = F.Workspace((F.SinglePart(F.DetachedGuid, "antenna_0v_16"), false), (F.Rocket(), true));
        JObject imported = F.SinglePart(F.NewGuid, "pod_1v_conic");
        Assert.IsTrue(VabSnapshot.ReplaceMain(ws, imported));

        JToken main = ws["Assemblies"][1];
        Assert.IsTrue(JToken.DeepEquals(imported, main["Assembly"]));
        Assert.AreNotSame(imported, main["Assembly"]);
        Assert.IsTrue((bool)main["isMainAssembly"]);
        Assert.AreEqual(10.7356882, (double)main["position"]["x"], 1e-9);   // placement kept
        Assert.IsTrue(JToken.DeepEquals(F.SinglePart(F.DetachedGuid, "antenna_0v_16"), ws["Assemblies"][0]["Assembly"]));

        imported["parts"][0]["partName"] = "changed";
        Assert.AreEqual("pod_1v_conic", (string)ws["Assemblies"][1]["Assembly"]["parts"][0]["partName"]);
    }

    [Test]
    public void ReplaceMain_WithoutMainChangesNothing()
    {
        JObject ws = F.Workspace((F.Rocket(), false));
        JToken before = ws.DeepClone();
        Assert.IsFalse(VabSnapshot.ReplaceMain(ws, F.SinglePart(F.NewGuid, "pod_1v_conic")));
        Assert.IsTrue(JToken.DeepEquals(before, ws));
        Assert.IsNull(VabSnapshot.MainEntry(ws));
        Assert.IsNull(VabSnapshot.MainEntry(null));
    }

    [Test]
    public void CountsParts()
    {
        JObject ws = F.Workspace((F.SinglePart(F.DetachedGuid, "antenna_0v_16"), false), (F.Rocket(), true));
        Assert.AreEqual(3, VabSnapshot.PartCount(F.Rocket()));
        Assert.AreEqual(0, VabSnapshot.PartCount(new JObject()));
        Assert.AreEqual(3, VabSnapshot.MainPartCount(ws));
        Assert.AreEqual(4, VabSnapshot.TotalPartCount(ws));
        Assert.AreEqual(0, VabSnapshot.MainPartCount(F.Workspace((F.Rocket(), false))));
        Assert.AreEqual(0, VabSnapshot.TotalPartCount(new JObject()));
    }
}
