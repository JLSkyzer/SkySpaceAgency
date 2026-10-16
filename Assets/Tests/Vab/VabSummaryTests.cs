using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabSummaryTests
{
    // Pod (root) <- tank <- engine, two radial tanks in one symmetry set, an antenna in no stage.
    static VabVessel Sample()
    {
        var v = new VabVessel
        {
            Name = "Bon vol", MassWetT = 12.34567, MassDryT = 4.5,
            DeltaVTotalVac = 3456.78, DeltaVTotalActual = 3012.34, DeltaVReady = true,
        };
        v.Parts.Add(new VabPart
        {
            Guid = "a", Name = "pod_2v_lander_crew", Title = "Lander pod", DryMassT = 0.8, Cost = 1200,
            Resources = { new VabResource { Name = "Monopropellant", Amount = 10, Capacity = 10, MassT = 0.04 } },
            Modules = { "Data_Command" },
        });
        v.Parts.Add(new VabPart
        {
            Guid = "b", Name = "fueltank_2v_inline_methalox_1x2", Title = "Tank", ParentGuid = "a", AttachNode = "top",
            DryMassT = 0.25, Cost = 300,
            Resources =
            {
                new VabResource { Name = "Methane", Amount = 1.6, Capacity = 1.6, MassT = 0.5 },
                new VabResource { Name = "Oxidizer", Amount = 6.4, Capacity = 6.4, MassT = 2.0 },
            },
        });
        v.Parts.Add(new VabPart
        {
            Guid = "c", Name = "engine_2v_methalox_poodle", Title = "Engine", ParentGuid = "b", AttachNode = "top",
            DryMassT = 1.0, Cost = 1500, Modules = { "Data_Engine", "Data_Gimbal" },
        });
        for (int i = 0; i < 2; i++)
            v.Parts.Add(new VabPart
            {
                Guid = i == 0 ? "d" : "e", Name = "fueltank_0v_radial_methalox", Title = "Radial tank",
                ParentGuid = "b", AttachNode = "srfAttach", SymmetryGroup = 7, DryMassT = 0.05,
                Resources = { new VabResource { Name = "Methane", Amount = 0.5, Capacity = 1.0, MassT = 0.1 } },
            });
        v.Parts.Add(new VabPart { Guid = "f", Name = "antenna_0v_16", Title = "Antenna", ParentGuid = "a", AttachNode = "srfAttach", DryMassT = 0.01 });

        v.Stages.Add(new VabStage
        {
            Stage = 1, PartGuids = { "c", "b", "d", "e", "zz" }, EngineGuids = { "c" },
            StartMassT = 12.34567, EndMassT = 6.1, DryMassT = 1.35, FuelMassT = 6.2,
            IspVac = 350.04, IspAsl = 90.06, ThrustVacKN = 60.04, ThrustAslKN = 15.46,
            TwrVac = 1.23456, TwrAsl = 0.314, TwrActual = 1.2,
            DeltaVVac = 1234.567, DeltaVAsl = 300.04, DeltaVActual = 1200.06, BurnTimeS = 95.56,
            SituationBody = "Kerbin", SituationAtmosphere = false, SituationAltitudeM = 0,
        });
        v.Stages.Add(new VabStage
        {
            Stage = 0, PartGuids = { "a" }, DeltaVVac = double.NaN, IspVac = double.PositiveInfinity,
            SituationBody = "Mun", SituationAtmosphere = false, SituationAltitudeM = 1500.4,
        });
        return v;
    }

    static JObject PartByGuid(JObject summary, string guid)
    {
        foreach (JToken p in (JArray)summary["parts"])
            if ((string)p["guid"] == guid)
                return (JObject)p;
        return null;
    }

    [Test]
    public void Build_CopiesVesselTotals()
    {
        JObject s = VabSummary.Build(Sample());
        Assert.AreEqual("Bon vol", (string)s["vessel_name"]);
        Assert.AreEqual(6, (int)s["part_count"]);
        Assert.AreEqual(12.346, (double)s["mass_wet_t"], 1e-9);
        Assert.AreEqual(4.5, (double)s["mass_dry_t"], 1e-9);
        Assert.AreEqual(3456.8, (double)s["delta_v_total_vac"], 1e-9);
        Assert.AreEqual(3012.3, (double)s["delta_v_total_actual"], 1e-9);
        Assert.IsTrue((bool)s["delta_v_ready"]);
    }

    [Test]
    public void Build_AggregatesResourcesByName()
    {
        var totals = (JArray)VabSummary.Build(Sample())["resources_total"];
        Assert.AreEqual(3, totals.Count);
        Assert.AreEqual("Monopropellant", (string)totals[0]["name"]);
        Assert.AreEqual(10.0, (double)totals[0]["amount"], 1e-9);
        Assert.AreEqual("Methane", (string)totals[1]["name"]);
        Assert.AreEqual(2.6, (double)totals[1]["amount"], 1e-9);   // 1.6 + 0.5 + 0.5
        Assert.AreEqual(3.6, (double)totals[1]["capacity"], 1e-9); // 1.6 + 1 + 1
        Assert.AreEqual(0.7, (double)totals[1]["mass_t"], 1e-9);
        Assert.AreEqual("Oxidizer", (string)totals[2]["name"]);
        Assert.AreEqual(2.0, (double)totals[2]["mass_t"], 1e-9);
    }

    [Test]
    public void Build_StagesUseTitlesInGameOrder()
    {
        var stages = (JArray)VabSummary.Build(Sample())["stages"];
        Assert.AreEqual(2, stages.Count);
        JToken s1 = stages[0];
        Assert.AreEqual(1, (int)s1["stage"]);
        CollectionAssert.AreEqual(new[] { "Engine", "Tank", "Radial tank", "Radial tank", "zz" },
            ((JArray)s1["parts"]).ToObject<string[]>());                 // an unknown GUID stays as is
        CollectionAssert.AreEqual(new[] { "Engine" }, ((JArray)s1["engines_active"]).ToObject<string[]>());
        Assert.AreEqual(350.0, (double)s1["isp_vac"], 1e-9);
        Assert.AreEqual(90.1, (double)s1["isp_asl"], 1e-9);
        Assert.AreEqual(15.5, (double)s1["thrust_asl_kN"], 1e-9);
        Assert.AreEqual(1.23, (double)s1["twr_vac"], 1e-9);
        Assert.AreEqual(1234.6, (double)s1["delta_v_vac"], 1e-9);
        Assert.AreEqual(1200.1, (double)s1["delta_v_actual"], 1e-9);
        Assert.AreEqual(95.6, (double)s1["burn_time_s"], 1e-9);
        Assert.AreEqual(12.346, (double)s1["start_mass_t"], 1e-9);
        Assert.AreEqual("Kerbin", (string)s1["situation"]["body"]);
        Assert.IsFalse((bool)s1["situation"]["atmosphere"]);
        Assert.AreEqual(0, (int)stages[1]["stage"]);
        CollectionAssert.AreEqual(new[] { "Lander pod" }, ((JArray)stages[1]["parts"]).ToObject<string[]>());
        CollectionAssert.IsEmpty((JArray)stages[1]["engines_active"]);
        Assert.AreEqual(1500.0, (double)stages[1]["situation"]["altitude_m"], 1e-9);
    }

    [Test]
    public void Build_NotANumberBecomesNull()
    {
        JObject s = VabSummary.Build(Sample());
        Assert.AreEqual(JTokenType.Null, s["stages"][1]["delta_v_vac"].Type);
        Assert.AreEqual(JTokenType.Null, s["stages"][1]["isp_vac"].Type);
        string text = VabJson.Write(s);
        StringAssert.DoesNotContain("NaN", text);
        StringAssert.DoesNotContain("Infinity", text);
    }

    [Test]
    public void Build_PartsCarryLinksStagesAndMass()
    {
        JObject s = VabSummary.Build(Sample());
        JObject pod = PartByGuid(s, "a");
        Assert.AreEqual(JTokenType.Null, pod["parent_guid"].Type);
        Assert.AreEqual(JTokenType.Null, pod["attach_node"].Type);
        Assert.AreEqual(0, (int)pod["stage_index"]);
        Assert.AreEqual(JTokenType.Null, pod["symmetry_group"].Type);
        Assert.AreEqual(0.84, (double)pod["mass_t"], 1e-9);      // 0.8 dry + 0.04 monopropellant
        Assert.AreEqual(0.8, (double)pod["dry_mass_t"], 1e-9);
        Assert.AreEqual(1200.0, (double)pod["cost"], 1e-9);
        CollectionAssert.AreEqual(new[] { "Data_Command" }, ((JArray)pod["modules"]).ToObject<string[]>());
        Assert.AreEqual("Monopropellant", (string)pod["resources"][0]["name"]);
        Assert.AreEqual(10.0, (double)pod["resources"][0]["amount"], 1e-9);

        JObject tank = PartByGuid(s, "b");
        Assert.AreEqual("a", (string)tank["parent_guid"]);
        Assert.AreEqual("top", (string)tank["attach_node"]);
        Assert.AreEqual(1, (int)tank["stage_index"]);
        Assert.AreEqual(2.75, (double)tank["mass_t"], 1e-9);     // 0.25 + 0.5 + 2.0

        Assert.AreEqual(7, (int)PartByGuid(s, "d")["symmetry_group"]);
        Assert.AreEqual(JTokenType.Null, PartByGuid(s, "f")["stage_index"].Type);
    }
}
