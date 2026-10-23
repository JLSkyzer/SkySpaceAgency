using System;
using System.Collections.Generic;
using K2D2.Transfer;
using KSP.Sim;

// Independent reference for the transfer search, tests only. It computes the ideal patched-conic
// total (no reserve) by brute force: every departure UT over two synodic periods × every time of
// flight from 0.3 to 2 Hohmann, on a fine grid, with Lambert between the two children of the common
// ancestor. Both ends use ideal coplanar costs with perfect timing at the inner levels: at each level
// the cheaper of going straight through and going through a low periapsis of that level (Oberth).
// No refinement and no code shared with TransferSearch: the best grid point is the answer, so it is a
// lower bound the production search must approach.
public static class BruteForceReference
{
    public static double IdealTotal(BodySystem s, int from, double parkingRadius, int to, double arrivalRadius,
        bool capture, double ut0, int departureSamples, int tofSamples, out double bestDepartureUT, out double bestTof)
    {
        int ancestor = s.CommonAncestor(from, to);
        var up = Chain(s, from, ancestor);   // [from, …, child of the ancestor]
        var down = Chain(s, to, ancestor);   // [to, …, child of the ancestor]
        int cF = up[up.Count - 1], cT = down[down.Count - 1];
        double mu = s[ancestor].Mu;

        double a1 = OrbitRadius(s, cF), a2 = OrbitRadius(s, cT);
        double hohmann = Math.PI * Math.Sqrt(Math.Pow((a1 + a2) / 2, 3) / mu);
        double synodic = 1 / Math.Abs(1 / s.OrbitalPeriod(cF) - 1 / s.OrbitalPeriod(cT));

        double best = double.PositiveInfinity;
        bestDepartureUT = double.NaN;
        bestTof = double.NaN;
        for (int i = 0; i < departureSamples; i++)
        {
            double t = ut0 + 2 * synodic * i / (departureSamples - 1);
            s.StateRelativeToParent(cF, t, out var rF, out var vF);
            for (int j = 0; j < tofSamples; j++)
            {
                double tof = hohmann * (0.3 + 1.7 * j / (tofSamples - 1));
                s.StateRelativeToParent(cT, t + tof, out var rT, out var vT);
                if (!Lambert.Solve(rF, rT, tof, mu, true, 0, false, out var v1, out var v2))
                    continue;
                double cost = Leave(s, up, up.Count - 1, (v1 - vF).magnitude, parkingRadius)
                    + Reach(s, down, down.Count - 1, (v2 - vT).magnitude, arrivalRadius, capture);
                if (cost < best)
                {
                    best = cost;
                    bestDepartureUT = t;
                    bestTof = tof;
                }
            }
        }
        return best;
    }

    static List<int> Chain(BodySystem s, int body, int ancestor)
    {
        var chain = new List<int>();
        for (int b = body; b != ancestor; b = s[b].ParentIndex)
            chain.Add(b);
        return chain;
    }

    // Semi-major axis of a body's orbit, from vis-viva on its epoch state.
    static double OrbitRadius(BodySystem s, int body)
    {
        var n = s[body];
        double mu = s[n.ParentIndex].Mu;
        return 1 / (2 / n.EpochPosition.magnitude - n.EpochVelocity.sqrMagnitude / mu);
    }

    // Ideal cost of leaving chain[level] at vInf, starting from a circular parking orbit in chain[0].
    static double Leave(BodySystem s, List<int> chain, int level, double vInf, double parkingRadius)
    {
        var body = s[chain[level]];
        if (level == 0)
            return Math.Sqrt(vInf * vInf + 2 * body.Mu / parkingRadius) - Math.Sqrt(body.Mu / parkingRadius);

        double inner = OrbitRadius(s, chain[level - 1]);
        double moonSpeed = Math.Sqrt(body.Mu / inner);
        double straight = Leave(s, chain, level - 1, Math.Sqrt(vInf * vInf + 2 * body.Mu / inner) - moonSpeed, parkingRadius);

        double low = body.LowOrbitRadius;
        if (low >= inner)
            return straight;
        double sma = (low + inner) / 2;
        double atLow = Math.Sqrt(body.Mu * (2 / low - 1 / sma));
        double atMoon = Math.Sqrt(body.Mu * (2 / inner - 1 / sma));
        double oberth = Math.Sqrt(vInf * vInf + 2 * body.Mu / low) - atLow
            + Leave(s, chain, level - 1, moonSpeed - atMoon, parkingRadius);
        return Math.Min(straight, oberth);
    }

    // Ideal cost of arriving at chain[level] at vInf and ending at chain[0] (capture or flyby).
    static double Reach(BodySystem s, List<int> chain, int level, double vInf, double arrivalRadius, bool capture)
    {
        var body = s[chain[level]];
        if (level == 0)
            return capture
                ? Math.Sqrt(vInf * vInf + 2 * body.Mu / arrivalRadius) - Math.Sqrt(body.Mu / arrivalRadius)
                : 0;

        double inner = OrbitRadius(s, chain[level - 1]);
        double moonSpeed = Math.Sqrt(body.Mu / inner);
        double straight = Reach(s, chain, level - 1, Math.Sqrt(vInf * vInf + 2 * body.Mu / inner) - moonSpeed, arrivalRadius, capture);

        double low = body.LowOrbitRadius;
        if (low >= inner)
            return straight;
        double sma = (low + inner) / 2;
        double atLow = Math.Sqrt(body.Mu * (2 / low - 1 / sma));
        double atMoon = Math.Sqrt(body.Mu * (2 / inner - 1 / sma));
        double oberth = Math.Sqrt(vInf * vInf + 2 * body.Mu / low) - atLow
            + Reach(s, chain, level - 1, moonSpeed - atMoon, arrivalRadius, capture);
        return Math.Min(straight, oberth);
    }
}
