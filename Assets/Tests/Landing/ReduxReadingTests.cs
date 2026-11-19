using SkySpaceAgency.Landing;
using NUnit.Framework;

// ReduxReading (K2-D2 1.4.0): waits for a Redux impact prediction that is fresh, made after the
// attitude settled, and steady (two results 1 s apart within 500 m), or gives up after 40 s.
public class ReduxReadingTests
{
    const double R = 600000;               // Kerbin
    const double MetersPerDegree = R * System.Math.PI / 180;

    static ReduxImpact Impact(double snapshot_UT, double now, double latitude = 0, double impact_in_s = 100)
    {
        return new ReduxImpact
        {
            state = ReduxImpactState.Impact,
            latitude = latitude,
            longitude = 10,
            snapshot_UT = snapshot_UT,
            age = now - snapshot_UT,
            impact_UT = now + impact_in_s,
        };
    }

    [Test]
    public void Usable_OnlyFreshFutureImpacts()
    {
        Assert.IsTrue(Impact(99, 100).IsUsable(100));
        Assert.IsFalse(Impact(89, 100).IsUsable(100), "11 s old");
        Assert.IsFalse(Impact(99, 100, impact_in_s: -1).IsUsable(100), "impact in the past");
        var calculating = Impact(99, 100);
        calculating.state = ReduxImpactState.Calculating;
        Assert.IsFalse(calculating.IsUsable(100));
    }

    [Test]
    public void TwoAgreeingResultsOneSecondApart_Ready()
    {
        var reading = new ReduxReading();
        Assert.IsFalse(reading.Update(Impact(100, 100), 0.1, R, 100));
        Assert.IsFalse(reading.Update(Impact(100.5, 100.5), 0.1, R, 100.5), "only 0.5 s apart");
        // 300 m further: within 500 m.
        Assert.IsTrue(reading.Update(Impact(101, 101, latitude: 300 / MetersPerDegree), 0.1, R, 101));
        Assert.IsTrue(reading.result.IsUsable(101));
    }

    [Test]
    public void DisagreeingResults_KeepWaiting_ThenAgree()
    {
        var reading = new ReduxReading();
        reading.Update(Impact(100, 100), 0.1, R, 100);
        // 2 km further: not steady; this one becomes the new reference.
        Assert.IsFalse(reading.Update(Impact(101, 101, latitude: 2000 / MetersPerDegree), 0.1, R, 101));
        Assert.IsTrue(reading.Update(Impact(102, 102, latitude: 2100 / MetersPerDegree), 0.1, R, 102));
    }

    [Test]
    public void ResultsFromBeforeTheAttitudeSettled_DoNotCount()
    {
        var reading = new ReduxReading();
        // Still turning (5°): nothing counts.
        Assert.IsFalse(reading.Update(Impact(100, 100), 5, R, 100));
        Assert.IsFalse(reading.Update(Impact(101, 101), 5, R, 101));
        // Settled at 102: the result taken at 101.5 is from before.
        Assert.IsFalse(reading.Update(Impact(101.5, 102), 0.2, R, 102));
        Assert.IsFalse(reading.Update(Impact(102.2, 102.2), 0.2, R, 102.2));
        Assert.IsTrue(reading.Update(Impact(103.3, 103.3), 0.2, R, 103.3));
    }

    [Test]
    public void LooseAttitude_SettlesAfter15Seconds()
    {
        var reading = new ReduxReading();
        reading.Update(Impact(100, 100), 1.5, R, 100);
        Assert.IsFalse(reading.Update(Impact(110, 110), 1.5, R, 110), "1.5° is not settled before 15 s");
        reading.Update(Impact(116, 116), 1.5, R, 116);
        Assert.IsTrue(reading.Update(Impact(117, 117), 1.5, R, 117));
    }

    [Test]
    public void StaleOrPastResults_Rejected_UntilTimeout()
    {
        var reading = new ReduxReading();
        for (double t = 100; t < 140; t += 1)
            Assert.IsFalse(reading.Update(Impact(t - 11, t), 0.1, R, t), $"stale result at {t}");
        // 40 s without a usable result: gives up, and the result is not usable.
        Assert.IsTrue(reading.Update(Impact(130, 140.5, impact_in_s: -5), 0.1, R, 140.5));
        Assert.IsFalse(reading.result.IsUsable(140.5));
    }

    [Test]
    public void NotBefore_IgnoresEarlierResults()
    {
        var reading = new ReduxReading();
        Assert.IsFalse(reading.Update(Impact(100, 100), 0.1, R, 100, not_before_UT: 105, require_steady: false));
        Assert.IsTrue(reading.Update(Impact(105, 105), 0.1, R, 105, not_before_UT: 105, require_steady: false));
    }

    [Test]
    public void Reset_StartsOver()
    {
        var reading = new ReduxReading();
        reading.Update(Impact(100, 100), 0.1, R, 100);
        reading.Reset();
        Assert.IsFalse(reading.Update(Impact(101, 101), 0.1, R, 101), "first result after the reset");
        Assert.IsTrue(reading.Update(Impact(102, 102), 0.1, R, 102));
    }

    [Test]
    public void Distance_IsGreatCircle()
    {
        var a = Impact(0, 0);
        var b = Impact(0, 0, latitude: 1);
        Assert.AreEqual(MetersPerDegree, ReduxReading.Distance(a, b, R), 1e-6);
    }
}
