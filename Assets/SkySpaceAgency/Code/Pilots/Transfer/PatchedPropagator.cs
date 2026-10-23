using System;
using System.Collections.Generic;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    // An instantaneous burn. The Δv is in the frame of the body orbited at that UT; all frames share
    // the same inertial Zup axes, so the vector is simply added to the velocity.
    public struct Impulse
    {
        public double UT;
        public Vector3d DeltaV;

        public Impulse(double ut, Vector3d deltaV)
        {
            UT = ut;
            DeltaV = deltaV;
        }
    }

    public enum PatchEnd { Escape, Encounter, Impact, HorizonReached, Impulse }

    // One two-body arc inside one SOI. States are relative to Body.
    public class Patch
    {
        public int Body;
        public double StartUT, EndUT;
        public Vector3d StartR, StartV;
        public Vector3d EndR, EndV; // at EndUT, before any impulse applied there

        public PatchEnd End;
        public int NextBody = -1; // body of the next patch (the same body after an impulse), -1 when the trajectory stops

        // Periapsis of this patch's conic: on a closed orbit the next passage at or after StartUT, on
        // a hyperbola its only passage, which is before StartUT when the vessel is already outgoing.
        // It can fall after EndUT (the patch ends first) or below the surface (an impact).
        public double PeriapsisRadius, PeriapsisUT;
    }

    // Patched-conic propagation through the body hierarchy: two-body motion inside each SOI, switching
    // body at SOI exits and entries, stopping at the atmosphere (or surface) and at the horizon.
    // Pure code: no game API, safe on a worker thread.
    public static class PatchedPropagator
    {
        const int SamplesPerPeriod = 200;
        const double TimeTolerance = 1e-5; // s, for event refinement

        // Impulses before 'ut' are ignored, those at 'ut' are applied to the start state, those at or
        // after 'untilUT' are ignored. A trajectory already inside a child's SOI at the start (given in
        // the parent's frame) only enters it after leaving it first.
        public static List<Patch> Propagate(BodySystem s, int body, Vector3d r, Vector3d v, double ut, double untilUT,
            IReadOnlyList<Impulse> impulses, int maxPatches = 8)
        {
            var patches = new List<Patch>();
            var pending = new List<Impulse>();
            if (impulses != null)
                foreach (var imp in impulses)
                    if (imp.UT >= ut && imp.UT < untilUT)
                        pending.Add(imp);
            pending.Sort((a, b) => a.UT.CompareTo(b.UT));
            int next = 0;
            for (; next < pending.Count && pending[next].UT <= ut; next++)
                v += pending[next].DeltaV;

            var children = Children(s);
            while (patches.Count < maxPatches)
            {
                var node = s[body];
                double mu = node.Mu;
                var patch = new Patch { Body = body, StartUT = ut, StartR = r, StartV = v };
                Periapsis(r, v, mu, ut, out patch.PeriapsisRadius, out patch.PeriapsisUT);

                double endUT = untilUT;
                PatchEnd end = PatchEnd.HorizonReached;
                if (next < pending.Count && pending[next].UT < endUT)
                {
                    endUT = pending[next].UT;
                    end = PatchEnd.Impulse;
                }
                double impact = ImpactUT(r, v, mu, ut, node.Radius + node.AtmosphereDepth);
                if (impact < endUT)
                {
                    endUT = impact;
                    end = PatchEnd.Impact;
                }
                double escape = node.ParentIndex >= 0 ? EscapeUT(r, v, mu, ut, node.Soi) : double.PositiveInfinity;
                if (escape < endUT)
                {
                    endUT = escape;
                    end = PatchEnd.Escape;
                }
                int child = FirstEncounter(s, children[body], r, v, mu, ut, endUT, out double entry);
                if (child >= 0)
                {
                    endUT = entry;
                    end = PatchEnd.Encounter;
                }

                StateAt(r, v, mu, ut, endUT, out var rEnd, out var vEnd);
                patch.EndUT = endUT;
                patch.EndR = rEnd;
                patch.EndV = vEnd;
                patch.End = end;
                patches.Add(patch);

                switch (end)
                {
                    case PatchEnd.Impulse:
                        patch.NextBody = body;
                        for (; next < pending.Count && pending[next].UT <= endUT; next++)
                            vEnd += pending[next].DeltaV;
                        break;
                    case PatchEnd.Escape:
                    {
                        patch.NextBody = node.ParentIndex;
                        s.StateRelativeToParent(body, endUT, out var br, out var bv);
                        rEnd += br;
                        vEnd += bv;
                        body = node.ParentIndex;
                        break;
                    }
                    case PatchEnd.Encounter:
                    {
                        patch.NextBody = child;
                        s.StateRelativeToParent(child, endUT, out var cr, out var cv);
                        rEnd -= cr;
                        vEnd -= cv;
                        body = child;
                        break;
                    }
                    default:
                        return patches;
                }
                r = rEnd;
                v = vEnd;
                ut = endUT;
            }
            return patches;
        }

        static List<int>[] Children(BodySystem s)
        {
            var children = new List<int>[s.Count];
            for (int i = 0; i < s.Count; i++)
                children[i] = new List<int>();
            for (int i = 0; i < s.Count; i++)
                if (s[i].ParentIndex >= 0)
                    children[s[i].ParentIndex].Add(i);
            return children;
        }

        static void StateAt(Vector3d r0, Vector3d v0, double mu, double ut0, double ut, out Vector3d r, out Vector3d v)
        {
            if (ut == ut0)
            {
                r = r0;
                v = v0;
                return;
            }
            KeplerPropagator.Propagate(r0, v0, mu, ut - ut0, out r, out v);
        }

        static double RadiusAt(Vector3d r0, Vector3d v0, double mu, double ut0, double ut)
        {
            StateAt(r0, v0, mu, ut0, ut, out var r, out _);
            return r.magnitude;
        }

        // Periapsis radius, and its UT as documented on Patch.PeriapsisUT.
        static void Periapsis(Vector3d r, Vector3d v, double mu, double ut, out double radius, out double periapsisUT)
        {
            var el = OrbitMath.Elements(r, v, mu);
            radius = el.PeriapsisRadius;
            double since = TimeSincePeriapsis(r, v, mu, el);
            periapsisUT = el.IsClosed && since > 0 ? ut - since + el.Period : ut - since;
        }

        // On a closed orbit in [0, period); on a hyperbola negative while incoming.
        static double TimeSincePeriapsis(Vector3d r, Vector3d v, double mu, OrbitElements el)
        {
            double a = el.SemiMajorAxis, e = el.Eccentricity;
            double rMag = r.magnitude, rv = Vector3d.Dot(r, v);
            if (el.IsClosed)
            {
                double eccentricAnomaly = Math.Atan2(rv / Math.Sqrt(mu * a), 1 - rMag / a);
                double meanAnomaly = eccentricAnomaly - e * Math.Sin(eccentricAnomaly);
                if (meanAnomaly < 0)
                    meanAnomaly += 2 * Math.PI;
                return meanAnomaly / Math.Sqrt(mu / (a * a * a));
            }
            // Hyperbola (a < 0): e·sinh H = r·v / sqrt(-μa).
            double sinhH = rv / Math.Sqrt(-mu * a) / e;
            double h = Math.Log(sinhH + Math.Sqrt(sinhH * sinhH + 1));
            return (e * sinhH - h) / Math.Sqrt(mu / (-a * -a * -a));
        }

        // First UT from ut0 where the radius drops below 'limit' (the atmosphere, or the surface).
        // The radius only falls from apoapsis to periapsis, so on [lo, periapsis] it crosses the
        // limit once and a bisection on the sign is safe.
        static double ImpactUT(Vector3d r, Vector3d v, double mu, double ut0, double limit)
        {
            var el = OrbitMath.Elements(r, v, mu);
            if (el.PeriapsisRadius >= limit)
                return double.PositiveInfinity;
            double since = TimeSincePeriapsis(r, v, mu, el);
            bool falling = Vector3d.Dot(r, v) < 0;
            if (r.magnitude <= limit)
            {
                if (falling)
                    return ut0;
                if (!el.IsClosed)
                    return double.PositiveInfinity; // climbing out on a hyperbola
            }
            else if (!el.IsClosed && !falling)
                return double.PositiveInfinity; // outgoing on a hyperbola

            double lo, hi;
            if (!el.IsClosed)
            {
                lo = ut0;
                hi = ut0 - since; // the periapsis ahead
            }
            else
            {
                hi = ut0 + el.Period - since; // the next periapsis
                lo = r.magnitude > limit ? ut0 : hi - el.Period / 2; // below and climbing: from the apoapsis
            }
            // r(lo) > limit > r(hi).
            return Bisect(lo, hi, t => RadiusAt(r, v, mu, ut0, t) < limit);
        }

        // First UT from ut0 where the radius reaches the SOI, on a rising branch.
        static double EscapeUT(Vector3d r, Vector3d v, double mu, double ut0, double soi)
        {
            var el = OrbitMath.Elements(r, v, mu);
            if (el.IsClosed && el.ApoapsisRadius < soi)
                return double.PositiveInfinity;
            if (r.magnitude >= soi && Vector3d.Dot(r, v) >= 0)
                return ut0;

            double since = TimeSincePeriapsis(r, v, mu, el);
            double lo, hi;
            if (el.IsClosed)
            {
                // Rising from the last periapsis to the apoapsis, otherwise from the next periapsis.
                double periapsis = since <= el.Period / 2 ? ut0 - since : ut0 - since + el.Period;
                lo = Math.Max(ut0, periapsis);
                hi = periapsis + el.Period / 2;
            }
            else
            {
                lo = Math.Max(ut0, ut0 - since);
                double step = Math.Max(1.0, soi / Math.Max(1.0, v.magnitude));
                hi = lo + step;
                for (int i = 0; i < 200 && RadiusAt(r, v, mu, ut0, hi) < soi; i++)
                {
                    lo = hi;
                    step *= 2;
                    hi = lo + step;
                }
            }
            if (RadiusAt(r, v, mu, ut0, lo) >= soi)
                return lo;
            // r(lo) < soi <= r(hi): the outside side is returned, so the parent patch starts outside.
            return Bisect(lo, hi, t => RadiusAt(r, v, mu, ut0, t) >= soi);
        }

        // The first child SOI entered in (ut0, untilUT], sampled at most every 1/200 of the vessel's
        // period (or of the hyperbola's time scale) and of each child's period, then refined by bisection.
        // Between samples, a dip into an SOI is caught where the distance stops falling.
        static int FirstEncounter(BodySystem s, List<int> children, Vector3d r0, Vector3d v0, double mu,
            double ut0, double untilUT, out double entryUT)
        {
            entryUT = double.NaN;
            if (children.Count == 0 || !(untilUT > ut0))
                return -1;

            var el = OrbitMath.Elements(r0, v0, mu);
            double a = Math.Abs(el.SemiMajorAxis);
            double step = 2 * Math.PI * Math.Sqrt(a * a * a / mu) / SamplesPerPeriod;
            foreach (int c in children)
                step = Math.Min(step, s.OrbitalPeriod(c) / SamplesPerPeriod);
            if (!(step > 0))
                step = untilUT - ut0;

            int n = children.Count;
            var prevRate = new double[n];
            var armed = new bool[n]; // outside the child's SOI since the start, so an entry counts
            for (int i = 0; i < n; i++)
            {
                ChildDistance(s, children[i], r0, v0, ut0, out double d, out prevRate[i]);
                armed[i] = d >= s[children[i]].Soi;
            }

            double prevT = ut0;
            while (prevT < untilUT)
            {
                double t = Math.Min(untilUT, prevT + step);
                StateAt(r0, v0, mu, ut0, t, out var r, out var v);
                int best = -1;
                double bestUT = double.PositiveInfinity;
                for (int i = 0; i < n; i++)
                {
                    int c = children[i];
                    double soi = s[c].Soi;
                    ChildDistance(s, c, r, v, t, out double d, out double rate);
                    double entry = double.NaN;
                    if (armed[i])
                    {
                        if (d < soi)
                            entry = Bisect(prevT, t, x => DistanceAt(s, c, r0, v0, mu, ut0, x) < soi);
                        else if (prevRate[i] < 0 && rate > 0)
                        {
                            // Closest approach between the samples: the distance rate changes sign.
                            double closest = Bisect(prevT, t, x => DistanceRateAt(s, c, r0, v0, mu, ut0, x) > 0);
                            if (DistanceAt(s, c, r0, v0, mu, ut0, closest) < soi)
                                entry = Bisect(prevT, closest, x => DistanceAt(s, c, r0, v0, mu, ut0, x) < soi);
                        }
                    }
                    else if (d >= soi)
                        armed[i] = true;
                    if (entry < bestUT)
                    {
                        bestUT = entry;
                        best = c;
                    }
                    prevRate[i] = rate;
                }
                if (best >= 0)
                {
                    entryUT = bestUT;
                    return best;
                }
                prevT = t;
            }
            return -1;
        }

        // Distance from the vessel (r, v at ut, in the parent's frame) to a child, and its rate of change.
        static void ChildDistance(BodySystem s, int child, Vector3d r, Vector3d v, double ut, out double distance, out double rate)
        {
            s.StateRelativeToParent(child, ut, out var cr, out var cv);
            var dr = r - cr;
            distance = dr.magnitude;
            rate = Vector3d.Dot(dr, v - cv) / distance;
        }

        static double DistanceAt(BodySystem s, int child, Vector3d r0, Vector3d v0, double mu, double ut0, double ut)
        {
            StateAt(r0, v0, mu, ut0, ut, out var r, out var v);
            ChildDistance(s, child, r, v, ut, out double d, out _);
            return d;
        }

        static double DistanceRateAt(BodySystem s, int child, Vector3d r0, Vector3d v0, double mu, double ut0, double ut)
        {
            StateAt(r0, v0, mu, ut0, ut, out var r, out var v);
            ChildDistance(s, child, r, v, ut, out _, out double rate);
            return rate;
        }

        // The first time in [lo, hi] where 'after' holds, given !after(lo) and after(hi). Returns the
        // 'after' side of the final bracket.
        static double Bisect(double lo, double hi, Func<double, bool> after)
        {
            for (int i = 0; i < 200 && hi - lo > TimeTolerance; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (mid <= lo || mid >= hi)
                    break;
                if (after(mid))
                    hi = mid;
                else
                    lo = mid;
            }
            return hi;
        }
    }
}
