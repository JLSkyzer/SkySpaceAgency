using K2D2.Node;
using NUnit.Framework;

public class BurnWarpTests
{
    // ---- AlignmentLead ----

    [Test]
    public void AlignmentLead_TurnTimePlusSettle()
    {
        // 106° at 3°/s: 35.3 s of turn + 8 s of settling.
        Assert.That(BurnWarp.AlignmentLead(106, 3), Is.EqualTo(106.0 / 3 + 8).Within(1e-9));
        Assert.That(BurnWarp.AlignmentLead(106, 3), Is.EqualTo(43.3).Within(0.05));
    }

    [Test]
    public void AlignmentLead_SmallAngle_IsTheFloor()
    {
        Assert.That(BurnWarp.AlignmentLead(5, 3), Is.EqualTo(BurnWarp.MinLead));
        Assert.That(BurnWarp.MinLead, Is.EqualTo(20));
    }

    [Test]
    public void AlignmentLead_SlowTurn_IsCapped()
    {
        Assert.That(BurnWarp.AlignmentLead(179, 1), Is.EqualTo(BurnWarp.MaxLead));
        Assert.That(BurnWarp.MaxLead, Is.EqualTo(120));
    }

    [Test]
    public void AlignmentLead_UnknownRate_UsesTheDefaultRate()
    {
        double expected = BurnWarp.AlignmentLead(106, BurnWarp.DefaultTurnRate);
        Assert.That(BurnWarp.AlignmentLead(106, 0), Is.EqualTo(expected));
        Assert.That(BurnWarp.AlignmentLead(106, -2), Is.EqualTo(expected));
        Assert.That(BurnWarp.AlignmentLead(106, double.NaN), Is.EqualTo(expected));
        Assert.That(BurnWarp.DefaultTurnRate, Is.EqualTo(3));
    }

    [Test]
    public void AlignmentLead_UnknownAngle_IsTheCap()
    {
        Assert.That(BurnWarp.AlignmentLead(double.NaN, 3), Is.EqualTo(BurnWarp.MaxLead));
    }

    // ---- MaxBurnRate ----

    [Test]
    public void MaxBurnRate_FivePercentRule_Alone()
    {
        // 500 m/s left, 5 m/s², 0.02 s frames: 5 % of 500 = 25 m/s per frame = 250 x 0.1 m/s.
        Assert.That(BurnWarp.FivePercentRate(500, 5, 0.02), Is.EqualTo(250));
    }

    [Test]
    public void MaxBurnRate_TenSecondRule_Alone()
    {
        // 100 s of burn left must last at least 10 s of real time: rate <= 10.
        Assert.That(BurnWarp.TenSecondRate(500, 5), Is.EqualTo(10));
    }

    [Test]
    public void MaxBurnRate_IsTheLowerOfBothRules()
    {
        Assert.That(BurnWarp.MaxBurnRate(500, 5, 0.02), Is.EqualTo(10));
        // A slow frame makes the 5 % rule the stricter one: 0.05·500/(5·1) = 5.
        Assert.That(BurnWarp.MaxBurnRate(500, 5, 1), Is.EqualTo(5));
    }

    [Test]
    public void MaxBurnRate_ShortBurn_Is1()
    {
        Assert.That(BurnWarp.MaxBurnRate(5, 5, 0.02), Is.EqualTo(1));
    }

    [Test]
    public void MaxBurnRate_NoThrustOrBadInput_Is1()
    {
        Assert.That(BurnWarp.MaxBurnRate(500, 0, 0.02), Is.EqualTo(1));
        Assert.That(BurnWarp.MaxBurnRate(500, -1, 0.02), Is.EqualTo(1));
        Assert.That(BurnWarp.MaxBurnRate(double.NaN, 5, 0.02), Is.EqualTo(1));
        Assert.That(BurnWarp.MaxBurnRate(500, 5, 0), Is.EqualTo(1));
        Assert.That(BurnWarp.MaxBurnRate(500, 5, double.NaN), Is.EqualTo(1));
    }

    // ---- HighestIndexAtOrBelow ----

    static readonly float[] Rates = { 1, 2, 4, 10, 50, 100, 1000 };

    [Test]
    public void HighestIndexAtOrBelow_RoundsDown()
    {
        Assert.That(BurnWarp.HighestIndexAtOrBelow(Rates, 10), Is.EqualTo(3));
        Assert.That(BurnWarp.HighestIndexAtOrBelow(Rates, 49), Is.EqualTo(3));
        Assert.That(BurnWarp.HighestIndexAtOrBelow(Rates, 250), Is.EqualTo(5));
        Assert.That(BurnWarp.HighestIndexAtOrBelow(Rates, 1e9), Is.EqualTo(6));
    }

    [Test]
    public void HighestIndexAtOrBelow_UnderOneOrNoRates_Is0()
    {
        Assert.That(BurnWarp.HighestIndexAtOrBelow(Rates, 1), Is.EqualTo(0));
        Assert.That(BurnWarp.HighestIndexAtOrBelow(Rates, 0.5), Is.EqualTo(0));
        Assert.That(BurnWarp.HighestIndexAtOrBelow(Rates, double.NaN), Is.EqualTo(0));
        Assert.That(BurnWarp.HighestIndexAtOrBelow(new float[0], 100), Is.EqualTo(0));
        Assert.That(BurnWarp.HighestIndexAtOrBelow(null, 100), Is.EqualTo(0));
    }

    // ---- Throttle ----

    [Test]
    public void Throttle_UsesTheGameTimeOfAFrame()
    {
        // 2 m/s left, 10 m/s², a 0.02 s frame at x50 = 1 s of game time: 2 / (10·1) = 0.2.
        Assert.That(BurnWarp.Throttle(2, 10, 0.02, 50), Is.EqualTo(0.2).Within(1e-6));
        // At x500 the frame is 10 s of game time: 2 / 100, floored at 0.05.
        Assert.That(BurnWarp.Throttle(2, 10, 0.02, 500), Is.EqualTo(BurnWarp.MinThrottle).Within(1e-6));
    }

    [Test]
    public void Throttle_AtLeastOneSecondTimeConstant()
    {
        // At 1x a 0.02 s frame still uses the 1 s time constant: 5 / (10·1) = 0.5.
        Assert.That(BurnWarp.Throttle(5, 10, 0.02, 1), Is.EqualTo(0.5).Within(1e-6));
    }

    [Test]
    public void Throttle_IsClamped()
    {
        Assert.That(BurnWarp.Throttle(500, 10, 0.02, 1), Is.EqualTo(1));
        Assert.That(BurnWarp.Throttle(0.01, 10, 0.02, 1), Is.EqualTo(BurnWarp.MinThrottle).Within(1e-6));
        Assert.That(BurnWarp.MinThrottle, Is.EqualTo(0.05).Within(1e-6));
    }

    [Test]
    public void Throttle_NoThrust_Is0()
    {
        Assert.That(BurnWarp.Throttle(2, 0, 0.02, 1), Is.EqualTo(0));
        Assert.That(BurnWarp.Throttle(2, -1, 0.02, 1), Is.EqualTo(0));
        Assert.That(BurnWarp.Throttle(double.NaN, 10, 0.02, 1), Is.EqualTo(0));
    }

    // ---- MeasuredTurnRate ----

    [Test]
    public void MeasuredTurnRate_FirstSample_IsTaken()
    {
        // 1° less in 0.1 s of real time: 10°/s.
        Assert.That(BurnWarp.MeasuredTurnRate(double.NaN, 50, 49, 0.1), Is.EqualTo(10).Within(1e-9));
        Assert.That(BurnWarp.MeasuredTurnRate(0, 50, 49, 0.1), Is.EqualTo(10).Within(1e-9));
    }

    [Test]
    public void MeasuredTurnRate_IsLowPassed()
    {
        double rate = BurnWarp.MeasuredTurnRate(3, 50, 49, 0.1);   // sample 10°/s
        Assert.That(rate, Is.GreaterThan(3));
        Assert.That(rate, Is.LessThan(10));
        // Repeated samples converge to the true rate.
        for (int i = 0; i < 200; i++)
            rate = BurnWarp.MeasuredTurnRate(rate, 50, 49, 0.1);
        Assert.That(rate, Is.EqualTo(10).Within(1e-3));
    }

    [Test]
    public void MeasuredTurnRate_IgnoresIncreasesAndBadTime()
    {
        Assert.That(BurnWarp.MeasuredTurnRate(3, 49, 50, 0.1), Is.EqualTo(3));
        Assert.That(BurnWarp.MeasuredTurnRate(3, 50, 50, 0.1), Is.EqualTo(3));
        Assert.That(BurnWarp.MeasuredTurnRate(3, 50, 49, 0), Is.EqualTo(3));
        Assert.That(BurnWarp.MeasuredTurnRate(3, 50, 49, -1), Is.EqualTo(3));
        Assert.That(BurnWarp.MeasuredTurnRate(3, double.NaN, 49, 0.1), Is.EqualTo(3));
        Assert.That(BurnWarp.MeasuredTurnRate(3, 50, 49, double.NaN), Is.EqualTo(3));
    }
}
