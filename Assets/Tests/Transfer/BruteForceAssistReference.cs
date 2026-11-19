using System;
using System.Collections.Generic;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

// Independent references for the gravity-assist search (4.6), tests only. Like BruteForceReference,
// they use Lambert and KeplerPropagator but no code of the production search: no FlybyModel, no
// AssistPorkchop, no GridRefiner, no Hyperbola. The flyby and the asymptote formulas are written
// again here. Full grids, no decoupling, no refinement: the best grid point is the answer. The end
// costs are BruteForceReference's ideal ones (the cheaper of straight through and a low periapsis at
// each level).
public static class BruteForceAssistReference
{
    // The best Δv of: leave `from` (circular parking orbit), fly by `flyby` (a child of the common
    // ancestor, other than the departure's), arrive at `to` (capture into a low orbit, or flyby). The
    // departure runs over [ut0, ut0 + horizon]; each leg's flight from 0.3 to 1.5 Hohmann. +∞ when
    // nothing is feasible. Prograde transfers only.
    public static double SiblingAssist(BodySystem s, int from, double parkingRadius, int flyby, int to, double arrivalRadius,
        bool capture, double ut0, double horizon, int departures, int tof1, int tof2,
        out double tDep, out double tFly, out double tArr, out double flybyDv)
    {
        int ancestor = s.CommonAncestor(from, to);
        var up = BruteForceReference.Chain(s, from, ancestor);
        var down = BruteForceReference.Chain(s, to, ancestor);
        int cF = up[up.Count - 1], cT = down[down.Count - 1];
        if (s[flyby].ParentIndex != ancestor || flyby == cF || flyby == cT)
            throw new ArgumentException("The reference flies by a sibling of both ends only.");
        double mu = s[ancestor].Mu;
        var fb = s[flyby];
        double floor = fb.LowOrbitRadius, ceiling = 0.5 * fb.Soi;
        double aF = BruteForceReference.OrbitRadius(s, cF), aB = BruteForceReference.OrbitRadius(s, flyby),
            aT = BruteForceReference.OrbitRadius(s, cT);
        double h1 = Hohmann(aF, aB, mu), h2 = Hohmann(aB, aT, mu);

        double best = double.PositiveInfinity;
        tDep = tFly = tArr = flybyDv = double.NaN;
        for (int i = 0; i < departures; i++)
        {
            double t = ut0 + horizon * i / (departures - 1);
            s.StateRelativeToParent(cF, t, out var rF, out var vF);
            for (int a = 0; a < tof1; a++)
            {
                double tf = t + h1 * (0.3 + 1.2 * a / (tof1 - 1));
                s.StateRelativeToParent(flyby, tf, out var rB, out var vB);
                if (!Lambert.Solve(rF, rB, tf - t, mu, true, 0, false, out var v1, out var v2))
                    continue;
                double leave = BruteForceReference.Leave(s, up, up.Count - 1, (v1 - vF).magnitude, parkingRadius);
                if (leave >= best)
                    continue;
                Vector3d vin = v2 - vB;
                for (int b = 0; b < tof2; b++)
                {
                    double ta = tf + h2 * (0.3 + 1.2 * b / (tof2 - 1));
                    s.StateRelativeToParent(cT, ta, out var rT, out var vT);
                    if (!Lambert.Solve(rB, rT, ta - tf, mu, true, 0, false, out var w1, out var w2))
                        continue;
                    double fly = PoweredFlyby(vin, w1 - vB, fb.Mu, floor, ceiling);
                    if (double.IsInfinity(fly))
                        continue;
                    double cost = leave + fly + BruteForceReference.Reach(s, down, down.Count - 1, (w2 - vT).magnitude, arrivalRadius, capture);
                    if (cost < best)
                    {
                        best = cost;
                        tDep = t;
                        tFly = tf;
                        tArr = ta;
                        flybyDv = fly;
                    }
                }
            }
        }
        return best;
    }

    // From a circular equatorial parking orbit around `body` (injection phase sampled at 360 points),
    // the cheapest injection + powered flyby of `moon` whose outgoing `body`-relative velocity has the
    // asymptote wantedExcess. The flyby date runs over one moon period around tExit minus the coast
    // from the moon to the SOI; the injection's flight from 0.3 to 1.5 Hohmann. `saving` is the ideal
    // straight departure (Oberth) minus that cost.
    public static double MoonDeparture(BodySystem s, int body, double parkingRadius, int moon, Vector3d wantedExcess, double tExit,
        int flybys, int tofs, out double saving)
    {
        var B = s[body];
        var M = s[moon];
        double mu = B.Mu, aM = BruteForceReference.OrbitRadius(s, moon), pM = s.OrbitalPeriod(moon);
        double vPark = Math.Sqrt(mu / parkingRadius);
        double hoh = Hohmann(parkingRadius, aM, mu);
        double coast = (B.Soi - aM) / Math.Max(100, wantedExcess.magnitude); // a rough centre: the window spans a whole period
        double floorBody = B.LowOrbitRadius - 10_000;
        double best = double.PositiveInfinity;
        for (int k = 0; k < flybys; k++)
        {
            double tF = tExit - coast - pM / 2 + pM * k / flybys;
            s.StateRelativeToParent(moon, tF, out var rM, out var vM);
            var outs = new List<Vector3d>();
            foreach (var vOut in VelocitiesForAsymptote(rM, wantedExcess, mu))
                if (PeriapsisAboveOrBehind(rM, vOut, mu, floorBody))
                    outs.Add(vOut - vM);
            if (outs.Count == 0)
                continue;
            for (int j = 0; j < tofs; j++)
            {
                double tof = hoh * (0.3 + 1.2 * j / (tofs - 1));
                for (int p = 0; p < 360; p++)
                {
                    double phi = p * Math.PI / 180;
                    var rP = parkingRadius * new Vector3d(Math.Cos(phi), Math.Sin(phi), 0);
                    var vP = vPark * new Vector3d(-Math.Sin(phi), Math.Cos(phi), 0);
                    if (!Lambert.Solve(rP, rM, tof, mu, true, 0, false, out var v1, out var v2))
                        continue;
                    double inject = (v1 - vP).magnitude;
                    if (inject >= best)
                        continue;
                    foreach (var vinfOut in outs)
                    {
                        double c = inject + PoweredFlyby(v2 - vM, vinfOut, M.Mu, M.LowOrbitRadius, 0.5 * M.Soi);
                        if (c < best)
                            best = c;
                    }
                }
            }
        }
        double w = wantedExcess.magnitude;
        saving = Math.Sqrt(w * w + 2 * mu / parkingRadius) - vPark - best;
        return best;
    }

    static double Hohmann(double a1, double a2, double mu) => Math.PI * Math.Sqrt(Math.Pow((a1 + a2) / 2, 3) / mu);

    // Powered flyby, written again: the periapsis that turns vin into vout's direction (bisection on
    // the sum of both half-turns), then the burn along the motion there. +∞ when the turn is not
    // possible between floor and ceiling.
    public static double PoweredFlyby(Vector3d vin, Vector3d vout, double mu, double floor, double ceiling)
    {
        double a = vin.magnitude, b = vout.magnitude;
        if (!(a > 0) || !(b > 0))
            return double.PositiveInfinity;
        double cos = Vector3d.Dot(vin, vout) / (a * b);
        double turn = Math.Acos(Math.Max(-1, Math.Min(1, cos)));
        double Turn(double rp) => Math.Asin(mu / (mu + rp * a * a)) + Math.Asin(mu / (mu + rp * b * b));
        if (Turn(floor) < turn || Turn(ceiling) > turn)
            return double.PositiveInfinity;
        double lo = floor, hi = ceiling;
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Turn(mid) > turn) lo = mid; else hi = mid;
        }
        double r = 0.5 * (lo + hi);
        return Math.Abs(Math.Sqrt(b * b + 2 * mu / r) - Math.Sqrt(a * a + 2 * mu / r));
    }

    // The velocities at r whose outgoing asymptote is w, written again from the polar equation: with
    // ψ the angle from r to w along the motion and k = √(e² − 1), r (1 − cos ψ + k sin ψ) = a k²
    // (a = μ / w²). One solution per sense of motion.
    public static IEnumerable<Vector3d> VelocitiesForAsymptote(Vector3d r, Vector3d w, double mu)
    {
        double rm = r.magnitude, wm = w.magnitude;
        var n = Vector3d.Cross(r, w);
        if (n.magnitude < 1e-9 * rm * wm)
            yield break;
        double a = mu / (wm * wm);
        double psi0 = Math.Atan2(n.magnitude, Vector3d.Dot(r, w));
        var rHat = r / rm;
        foreach (int sense in new[] { 1, -1 })
        {
            double psi = sense > 0 ? psi0 : 2 * Math.PI - psi0;
            var hHat = sense * n / n.magnitude;
            var tHat = Vector3d.Cross(hHat, rHat);
            double sp = Math.Sin(psi), cp = Math.Cos(psi);
            double k = (rm * sp + Math.Sqrt(rm * rm * sp * sp + 4 * a * rm * (1 - cp))) / (2 * a);
            double e = Math.Sqrt(1 + k * k);
            double p = a * k * k, h = Math.Sqrt(mu * p);
            double fInf = Math.Acos(-1 / e), f = fInf - psi;
            yield return (mu / h * e * Math.Sin(f)) * rHat + (h / rm) * tHat;
        }
    }

    static bool PeriapsisAboveOrBehind(Vector3d r, Vector3d v, double mu, double floor)
    {
        if (Vector3d.Dot(r, v) >= 0)
            return true;
        var h = Vector3d.Cross(r, v).magnitude;
        double energy = 0.5 * v.sqrMagnitude - mu / r.magnitude;
        double e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h * h / (mu * mu)));
        return h * h / mu / (1 + e) >= floor;
    }
}
