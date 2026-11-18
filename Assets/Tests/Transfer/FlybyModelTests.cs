using System;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

// The patched-conic flyby (4.6): Eve's values from KerbolFixture (μ = 8.1717302e12, floor 800 km).
public class FlybyModelTests
{
    const double Mu = 8.1717302e12;
    const double Floor = 800_000, Ceiling = 42_500_000;

    static Vector3d Rotated(double speed, double deg, Vector3d axis) =>
        QuaternionD.AngleAxis(deg, axis) * new Vector3d(speed, 0, 0);

    [Test]
    public void Unpowered_ClassicTurn()
    {
        Assert.AreEqual(64.2283, FlybyModel.TurnDeg(8e5, 3000, 3000, Mu), 1e-3);
    }

    [Test]
    public void Powered_RecoversThePeriapsisAndTheBurn()
    {
        var vin = new Vector3d(3000, 0, 0);
        var vout = Rotated(3500, 52.00432, new Vector3d(0, 0, 1));
        var f = FlybyModel.Solve(vin, vout, Mu, Floor, Ceiling);
        Assert.IsTrue(f.Ok, f.Why);
        Assert.AreEqual(1_000_000, f.PeriapsisRadius, 1);
        Assert.AreEqual(313.056, f.DeltaV, 0.01);
        Assert.AreEqual(52.00432, f.TurnDeg, 1e-6);
        Assert.AreEqual(0, (f.Normal - new Vector3d(0, 0, 1)).magnitude, 1e-9);
    }

    [Test]
    public void Unpowered_SameSpeed_ZeroBurn()
    {
        var f = FlybyModel.Solve(new Vector3d(3000, 0, 0), Rotated(3000, 40, new Vector3d(0, 0, 1)), Mu, Floor, Ceiling);
        Assert.IsTrue(f.Ok, f.Why);
        Assert.Less(f.DeltaV, 1e-6);
    }

    [Test]
    public void TooLargeTurn_Refused()
    {
        var f = FlybyModel.Solve(new Vector3d(3000, 0, 0), Rotated(3500, 60, new Vector3d(0, 0, 1)), Mu, Floor, Ceiling);
        Assert.IsFalse(f.Ok);
        Assert.IsTrue(double.IsPositiveInfinity(f.DeltaV));
        StringAssert.StartsWith("turn 60.0° above the 59.2°", f.Why);
    }

    [Test]
    public void TooSmallTurn_Refused()
    {
        var f = FlybyModel.Solve(new Vector3d(3000, 0, 0), Rotated(3000, 0.01, new Vector3d(0, 0, 1)), Mu, Floor, Ceiling);
        Assert.IsFalse(f.Ok);
        StringAssert.Contains("too small", f.Why);
    }

    [Test]
    public void Aligned_Refused()
    {
        var vin = new Vector3d(3000, 200, -50);
        var f = FlybyModel.Solve(vin, 1.1 * vin, Mu, Floor, Ceiling);
        Assert.IsFalse(f.Ok);
        Assert.AreEqual("no plane: in and out excess aligned", f.Why);
    }

    [Test]
    public void NormalSign_FollowsTheTurn()
    {
        var f = FlybyModel.Solve(new Vector3d(3000, 0, 0), Rotated(3500, 52.00432, new Vector3d(0, 0, -1)), Mu, Floor, Ceiling);
        Assert.IsTrue(f.Ok, f.Why);
        Assert.AreEqual(0, (f.Normal - new Vector3d(0, 0, -1)).magnitude, 1e-9);
    }
}
