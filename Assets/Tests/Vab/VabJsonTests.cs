using K2D2.VabTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabJsonTests
{
    [Test]
    public void Parse_KeepsDateLikeStringsAndBigIntegers()
    {
        // Values from a real workspace file: SaveTime is a 64-bit tick count, Temperature a string.
        JToken t = VabJson.Parse("{\"when\":\"2026-10-16T05:07:09\",\"SaveTime\":639277225550056177," +
                                 "\"Temperature\":\"-Infinity\",\"Mass\":288.014038}");
        Assert.AreEqual(JTokenType.String, t["when"].Type);
        Assert.AreEqual("2026-10-16T05:07:09", (string)t["when"]);
        Assert.AreEqual(639277225550056177L, (long)t["SaveTime"]);
        Assert.AreEqual("-Infinity", VabJson.Str(t["Temperature"]));
        Assert.AreEqual(288.014038, VabJson.Num(t["Mass"]).Value, 1e-12);
        Assert.AreEqual(double.NegativeInfinity, VabJson.Num(t["Temperature"]).Value);
        Assert.IsNull(VabJson.Num(t["missing"]));
        Assert.IsNull(VabJson.Str(t["Mass"]));
    }

    [Test]
    public void Parse_RejectsBrokenOrTrailingJson()
    {
        Assert.Catch<JsonException>(() => VabJson.Parse("{\"parts\": ["));
        Assert.Catch<JsonException>(() => VabJson.Parse(""));
        Assert.Catch<JsonException>(() => VabJson.Parse(null));
        Assert.Catch<JsonException>(() => VabJson.Parse("{} {}"));
    }

    [Test]
    public void Write_IsIndentedAndRoundTrips()
    {
        var doc = new JObject { ["a"] = 1, ["b"] = new JArray("x", 2.5) };
        string text = VabJson.Write(doc);
        StringAssert.Contains("\n", text);
        Assert.IsTrue(JToken.DeepEquals(doc, VabJson.Parse(text)));
        Assert.IsFalse(VabJson.WriteCompact(doc).Contains("\n"));
    }

    [Test]
    public void GuidOf_NormalizesOrReturnsNull()
    {
        Assert.AreEqual("8dda8ca2-e636-41fa-bb90-0ddf0c1c25dd",
            VabJson.GuidOf(new JObject { ["Guid"] = "8DDA8CA2-E636-41FA-BB90-0DDF0C1C25DD" }));
        Assert.IsNull(VabJson.GuidOf(new JObject { ["Guid"] = "not-a-guid" }));
        Assert.IsNull(VabJson.GuidOf(new JValue("8dda8ca2-e636-41fa-bb90-0ddf0c1c25dd"))); // not an object
        Assert.IsNull(VabJson.GuidOf(null));
    }
}
