using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class CircularizeGuidanceTests
{
    // Kerbin.
    const double Mu = 3.5316e12;
    const double R = 600000;

    // Apoapsis (on the +x axis) of a 100 x -430 km suborbital Kerbin ellipse.
    const double Ra = R + 100000;
    const double Rp = R - 430000;

    static void StateAtApoapsis(out Vector3d r, out Vector3d v)
    {
        double speed = VisVivaEquation.CalculateVelocity(Ra, Ra, Rp, Mu);
        r = new Vector3d(Ra, 0, 0);
        v = new Vector3d(0, speed, 0);
    }

    static double Period()
    {
        double a = (Ra + Rp) / 2;
        return 2 * Math.PI * Math.Sqrt(a * a * a / Mu);
    }

    [Test]
    public void CircularOrbit_NeedsAboutZeroDeltaV()
    {
        double rMag = R + 100000;
        var r = new Vector3d(rMag, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(Mu / rMag), 0);

        Vector3d dv = CircularizeGuidance.DeltaVToCircular(r, v, Mu);

        Assert.That(dv.magnitude, Is.LessThan(1e-6));
    }

    [Test]
    public void AtApoapsis_IsPurelyHorizontal_AndMatchesVisViva()
    {
        StateAtApoapsis(out Vector3d r, out Vector3d v);

        Vector3d dv = CircularizeGuidance.DeltaVToCircular(r, v, Mu);
        CircularizeGuidance.Split(dv, r, v, out double radial, out double horizontal);

        double expected = VisVivaEquation.CalculateVelocity(Ra, Ra, Ra, Mu)
            - VisVivaEquation.CalculateVelocity(Ra, Ra, Rp, Mu);
        Assert.That(expected, Is.GreaterThan(100), "sanity: a suborbital apoapsis needs a real burn");
        Assert.That(radial, Is.EqualTo(0).Within(1e-6));
        Assert.That(horizontal, Is.EqualTo(expected).Within(1e-6));
        Assert.That(dv.magnitude, Is.EqualTo(expected).Within(1e-6));
    }

    [Test]
    public void DescendingPastApoapsis_RadialComponentIsPositive()
    {
        StateAtApoapsis(out Vector3d ra, out Vector3d va);
        KeplerPropagator.Propagate(ra, va, Mu, 60, out Vector3d r, out Vector3d v);
        Assert.That(Vector3d.Dot(r, v), Is.LessThan(0), "sanity: descending");

        Vector3d dv = CircularizeGuidance.DeltaVToCircular(r, v, Mu);
        CircularizeGuidance.Split(dv, r, v, out double radial, out double horizontal);

        // Kills the downward speed exactly.
        double verticalSpeed = Vector3d.Dot(v, r.normalized);
        Assert.That(radial, Is.GreaterThan(0));
        Assert.That(radial, Is.EqualTo(-verticalSpeed).Within(1e-6));
        Assert.That(horizontal, Is.GreaterThan(0));
    }

    [Test]
    public void AscendingBeforeApoapsis_RadialComponentIsNegative()
    {
        StateAtApoapsis(out Vector3d ra, out Vector3d va);
        KeplerPropagator.Propagate(ra, va, Mu, Period() - 60, out Vector3d r, out Vector3d v);
        Assert.That(Vector3d.Dot(r, v), Is.GreaterThan(0), "sanity: ascending");

        Vector3d dv = CircularizeGuidance.DeltaVToCircular(r, v, Mu);
        CircularizeGuidance.Split(dv, r, v, out double radial, out double horizontal);

        Assert.That(radial, Is.LessThan(0));
        Assert.That(radial, Is.EqualTo(-Vector3d.Dot(v, r.normalized)).Within(1e-6));
    }

    [Test]
    public void ApplyingTheDeltaV_GivesACircularVelocity()
    {
        StateAtApoapsis(out Vector3d ra, out Vector3d va);
        KeplerPropagator.Propagate(ra, va, Mu, 120, out Vector3d r, out Vector3d v);

        Vector3d after = v + CircularizeGuidance.DeltaVToCircular(r, v, Mu);

        Assert.That(Vector3d.Dot(after, r.normalized), Is.EqualTo(0).Within(1e-6));
        Assert.That(after.magnitude, Is.EqualTo(Math.Sqrt(Mu / r.magnitude)).Within(1e-6));
        // Same direction of travel: the horizontal speed keeps its sign.
        Assert.That(Vector3d.Dot(after, v), Is.GreaterThan(0));
    }

    [Test]
    public void SplitComponents_RebuildTheDeltaV()
    {
        StateAtApoapsis(out Vector3d ra, out Vector3d va);
        KeplerPropagator.Propagate(ra, va, Mu, 90, out Vector3d r, out Vector3d v);

        Vector3d dv = CircularizeGuidance.DeltaVToCircular(r, v, Mu);
        CircularizeGuidance.Split(dv, r, v, out double radial, out double horizontal);

        Assert.That(Math.Sqrt(radial * radial + horizontal * horizontal), Is.EqualTo(dv.magnitude).Within(1e-6));
    }

    [Test]
    public void ZeroRadius_GivesNaN()
    {
        Vector3d dv = CircularizeGuidance.DeltaVToCircular(Vector3d.zero, new Vector3d(0, 100, 0), Mu);

        Assert.That(double.IsNaN(dv.x) && double.IsNaN(dv.y) && double.IsNaN(dv.z));
    }

    [Test]
    public void NoHorizontalSpeed_GivesZero()
    {
        var r = new Vector3d(Ra, 0, 0);
        var v = new Vector3d(-200, 0, 0);

        Vector3d dv = CircularizeGuidance.DeltaVToCircular(r, v, Mu);

        Assert.That(dv.x, Is.EqualTo(0));
        Assert.That(dv.y, Is.EqualTo(0));
        Assert.That(dv.z, Is.EqualTo(0));
    }
}
