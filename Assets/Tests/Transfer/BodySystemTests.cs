using System;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class BodySystemTests
{
    static void AssertVector(Vector3d expected, Vector3d actual, double tolerance, string what)
    {
        Assert.That((actual - expected).magnitude, Is.LessThan(tolerance),
            $"{what}: expected ({expected.x}, {expected.y}, {expected.z}), got ({actual.x}, {actual.y}, {actual.z})");
    }

    [Test]
    public void Period_Kerbin_MatchesKepler()
    {
        var s = KerbolFixture.Build();
        var kerbin = s["Kerbin"];
        double period = s.OrbitalPeriod(kerbin.Index);
        double a = kerbin.EpochPosition.magnitude; // circular
        Assert.That(period, Is.EqualTo(2 * Math.PI * Math.Sqrt(a * a * a / s["Kerbol"].Mu)).Within(1e-6).Percent);
        // EXTERNAL DATA (KSP wiki): Kerbin's sidereal period is 9 203 545 s.
        Assert.That(period, Is.EqualTo(9203545).Within(10));

        s.StateRelativeToParent(kerbin.Index, kerbin.EpochUT + period, out var r, out var v);
        AssertVector(kerbin.EpochPosition, r, 1, "position after one period");
        AssertVector(kerbin.EpochVelocity, v, 1e-3, "velocity after one period");

        // The same through the propagator itself, so the period wrap in BodySystem hides nothing.
        KSP2FlightAssistant.MathLibrary.KeplerPropagator.Propagate(kerbin.EpochPosition, kerbin.EpochVelocity,
            s["Kerbol"].Mu, period, out var rk, out _);
        AssertVector(kerbin.EpochPosition, rk, 1, "propagator over one period");
    }

    [Test]
    public void ManyPeriods_Duna_MatchesOneStep()
    {
        var s = KerbolFixture.Build();
        var duna = s["Duna"]; // eccentric
        double period = s.OrbitalPeriod(duna.Index);
        s.StateRelativeToParent(duna.Index, duna.EpochUT + 0.3 * period, out var r1, out _);
        s.StateRelativeToParent(duna.Index, duna.EpochUT + 7.3 * period, out var r7, out _);
        s.StateRelativeToParent(duna.Index, duna.EpochUT - 2.7 * period, out var rNeg, out _);
        AssertVector(r1, r7, 10, "seven periods later");
        AssertVector(r1, rNeg, 10, "three periods earlier");
    }

    [Test]
    public void HalfPeriod_Kerbin_IsOpposite()
    {
        var s = KerbolFixture.Build();
        var kerbin = s["Kerbin"];
        s.StateRelativeToParent(kerbin.Index, kerbin.EpochUT + 0.5 * s.OrbitalPeriod(kerbin.Index), out var r, out _);
        AssertVector(-kerbin.EpochPosition, r, 1, "position after half a period");
    }

    [Test]
    public void StateRelativeToParent_Root_IsZero()
    {
        var s = KerbolFixture.Build();
        s.StateRelativeToParent(s["Kerbol"].Index, 1e6, out var r, out var v);
        Assert.That(r.magnitude, Is.EqualTo(0));
        Assert.That(v.magnitude, Is.EqualTo(0));
    }

    [Test]
    public void StateRelativeTo_MunAroundKerbol_IsSumOfChain()
    {
        var s = KerbolFixture.Build();
        int mun = s["Mun"].Index, kerbin = s["Kerbin"].Index, kerbol = s["Kerbol"].Index;
        double ut = 1234567;

        s.StateRelativeToParent(mun, ut, out var rMun, out var vMun);
        s.StateRelativeToParent(kerbin, ut, out var rKerbin, out var vKerbin);
        s.StateRelativeTo(mun, kerbol, ut, out var r, out var v);

        AssertVector(rMun + rKerbin, r, 1e-3, "position");
        AssertVector(vMun + vKerbin, v, 1e-9, "velocity");

        s.StateRelativeTo(mun, kerbin, ut, out var rToParent, out _);
        AssertVector(rMun, rToParent, 1e-9, "relative to the parent");
        s.StateRelativeTo(mun, mun, ut, out var rSelf, out _);
        Assert.That(rSelf.magnitude, Is.EqualTo(0));
    }

    [Test]
    public void StateRelativeTo_NotAnAncestor_Throws()
    {
        var s = KerbolFixture.Build();
        Assert.Throws<ArgumentException>(() => s.StateRelativeTo(s["Mun"].Index, s["Duna"].Index, 0, out _, out _));
    }

    [Test]
    public void CommonAncestor_MunDuna_IsKerbol()
    {
        var s = KerbolFixture.Build();
        Assert.That(s.CommonAncestor(s["Mun"].Index, s["Duna"].Index), Is.EqualTo(s["Kerbol"].Index));
        Assert.That(s.CommonAncestor(s["Ike"].Index, s["Laythe"].Index), Is.EqualTo(s["Kerbol"].Index));
    }

    [Test]
    public void CommonAncestor_MunMinmus_IsKerbin()
    {
        var s = KerbolFixture.Build();
        Assert.That(s.CommonAncestor(s["Mun"].Index, s["Minmus"].Index), Is.EqualTo(s["Kerbin"].Index));
        // A body and its own child: the body itself.
        Assert.That(s.CommonAncestor(s["Kerbin"].Index, s["Mun"].Index), Is.EqualTo(s["Kerbin"].Index));
    }

    [Test]
    public void PathToRoot_And_IsAncestor()
    {
        var s = KerbolFixture.Build();
        int mun = s["Mun"].Index, kerbin = s["Kerbin"].Index, kerbol = s["Kerbol"].Index;
        Assert.That(s.PathToRoot(mun), Is.EqualTo(new[] { mun, kerbin, kerbol }));
        Assert.That(s.PathToRoot(kerbol), Is.EqualTo(new[] { kerbol }));
        Assert.That(s.IsAncestor(kerbol, mun), Is.True);
        Assert.That(s.IsAncestor(kerbin, mun), Is.True);
        Assert.That(s.IsAncestor(mun, mun), Is.False);
        Assert.That(s.IsAncestor(mun, kerbin), Is.False);
        Assert.That(s.IsAncestor(s["Duna"].Index, mun), Is.False);
    }

    [Test]
    public void SynodicPeriod_KerbinDuna_About2Years()
    {
        var s = KerbolFixture.Build();
        int kerbin = s["Kerbin"].Index, duna = s["Duna"].Index;
        double years = s.SynodicPeriod(kerbin, duna) / s.OrbitalPeriod(kerbin);
        // EXTERNAL DATA (KSP wiki): Duna's synodic period seen from Kerbin is 19 645 697 s, i.e.
        // 2.13 Kerbin years. The plan's "1.92" does not match the wiki's periods (see the report).
        Assert.That(years, Is.EqualTo(2.13).Within(0.1));
        Assert.That(s.SynodicPeriod(duna, kerbin), Is.EqualTo(s.SynodicPeriod(kerbin, duna)));
    }

    [Test]
    public void SynodicPeriod_NotSiblings_Throws()
    {
        var s = KerbolFixture.Build();
        Assert.Throws<ArgumentException>(() => s.SynodicPeriod(s["Mun"].Index, s["Duna"].Index));
    }

    [Test]
    public void Indexers_And_LowOrbitRadius()
    {
        var s = KerbolFixture.Build();
        var duna = s["Duna"];
        Assert.That(s[duna.Index], Is.SameAs(duna));
        Assert.That(s["Kerbol"].ParentIndex, Is.EqualTo(-1));
        Assert.That(double.IsPositiveInfinity(s.OrbitalPeriod(s["Kerbol"].Index)), Is.True);
        // Duna: atmosphere 50 km above the 8.3 km terrain, so 320 km + 50 km + 10 km.
        Assert.That(duna.LowOrbitRadius, Is.EqualTo(380000));
        // Mun: airless, terrain 7 061 m.
        Assert.That(s["Mun"].LowOrbitRadius, Is.EqualTo(200000 + 7061 + 10000));
    }
}
