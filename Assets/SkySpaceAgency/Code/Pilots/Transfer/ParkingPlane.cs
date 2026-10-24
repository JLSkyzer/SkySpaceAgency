using System;
using System.Collections.Generic;
using System.Threading;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    public class ParkingPlan
    {
        public bool Ok;
        public string Refusal;

        public Vector3d PlaneNormal = new Vector3d(0, 0, 1); // unit, inertial Zup frame of the launch body
        public double OrbitRadius;

        public double WindowUT = double.NaN;  // the transfer's first burn, from the nominal parking orbit
        public double TransferDeltaV;
        public string Explanation = "";

        // The plan from a nominal circular parking orbit in that plane. The Transfer tab plans again
        // from the orbit actually reached.
        public TransferPlan Transfer;
    }

    // The parking-orbit plane a launch should aim for, so that the transfer leaves from it without a
    // plane change:
    //  - toward a moon of the launch body (or a moon's moon), the plane of that moon's orbit;
    //  - otherwise, a plane holding the departure asymptote that TransferSearch finds from an
    //    equatorial orbit. Of all such planes, the least inclined that the site can reach:
    //    inclination max(|declination|, |site latitude|). There are two (the asymptote on the
    //    ascending or the descending side): both are planned and the cheaper transfer wins. The
    //    window moves a little with the plane, so the choice is repeated until the asymptote of the
    //    plan from that plane is within 0.05° of it.
    //  - without a direction (child to parent), the equatorial plane.
    // Pure code: no game API, safe on a worker thread.
    public static class ParkingPlane
    {
        const double Deg = Math.PI / 180;
        const double ToleranceDeg = 0.05;
        const int MaxRounds = 3;

        public static ParkingPlan Plan(BodySystem s, int launchBody, int targetBody, double siteLatitudeRad,
            double ut0, double orbitRadius, TransferRequest template, CancellationToken ct)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (launchBody < 0 || launchBody >= s.Count || targetBody < 0 || targetBody >= s.Count)
                throw new ArgumentException("Unknown body index.");
            if (launchBody == targetBody)
                return Refuse(TransferSearch.SameBody, orbitRadius);
            if (!(orbitRadius > 0) || double.IsInfinity(orbitRadius) || double.IsNaN(ut0))
                return Refuse("Parking orbit: invalid radius or time.", orbitRadius);

            double latDeg = double.IsNaN(siteLatitudeRad) ? 0 : Math.Abs(siteLatitudeRad) / Deg;
            latDeg = Math.Min(90, latDeg);
            var body = s[launchBody];

            // Toward a moon: its orbit plane.
            int moon = MoonOnTheWay(s, launchBody, targetBody);
            if (moon >= 0)
            {
                s.StateRelativeToParent(moon, ut0, out var rm, out var vm);
                var n = Vector3d.Cross(rm, vm).normalized;
                var plan = Find(s, launchBody, targetBody, n, ut0, orbitRadius, template, ct);
                return Result(plan, n, orbitRadius, Inv($"the {s[moon].Name} orbit plane"), body);
            }

            var equatorial = Find(s, launchBody, targetBody, new Vector3d(0, 0, 1), ut0, orbitRadius, template, ct);
            if (!equatorial.Ok)
                return Refuse(equatorial.Refusal, orbitRadius);
            var d = Departure(s, launchBody, equatorial, ut0, orbitRadius);
            if (d.magnitude == 0)
                return Result(equatorial, new Vector3d(0, 0, 1), orbitRadius, "equatorial: the transfer needs no particular plane", body);

            // The two planes, both planned; then again from the winner's asymptote.
            TransferPlan best = null;
            Vector3d bestN = Vector3d.zero;
            foreach (var n in Planes(d, latDeg))
            {
                var plan = Find(s, launchBody, targetBody, n, ut0, orbitRadius, template, ct);
                if (plan.Ok && (best == null || plan.TotalDeltaV < best.TotalDeltaV))
                {
                    best = plan;
                    bestN = n;
                }
            }
            if (best == null)
                return Result(equatorial, new Vector3d(0, 0, 1), orbitRadius,
                    "equatorial: no transfer was found from a plane holding the departure", body);
            for (int round = 0; round < MaxRounds; round++)
            {
                var dNow = Departure(s, launchBody, best, ut0, orbitRadius);
                if (dNow.magnitude == 0 || OutOfPlaneDeg(dNow, bestN) <= ToleranceDeg)
                    break;
                // The plane nearer to the current one: the same side of the asymptote.
                Vector3d next = Vector3d.zero;
                foreach (var n in Planes(dNow, latDeg))
                    if (next.magnitude == 0 || Vector3d.Dot(n, bestN) > Vector3d.Dot(next, bestN))
                        next = n;
                var plan = Find(s, launchBody, targetBody, next, ut0, orbitRadius, template, ct);
                if (!plan.Ok)
                    break;
                best = plan;
                bestN = next;
            }

            var dFinal = Departure(s, launchBody, best, ut0, orbitRadius);
            double dec = Math.Asin(Clamp(dFinal.normalized.z)) / Deg;
            string why = Math.Abs(dec) >= latDeg
                ? Inv($"it holds the departure asymptote (declination {dec:F2}°)")
                : Inv($"it holds the departure asymptote (declination {dec:F2}°) and the site's latitude ({latDeg:F2}°) allows no lower one");
            return Result(best, bestN, orbitRadius, why, body);
        }

        // The child of launchBody on the way down to targetBody, or -1.
        static int MoonOnTheWay(BodySystem s, int launchBody, int targetBody)
        {
            if (!s.IsAncestor(launchBody, targetBody))
                return -1;
            int b = targetBody;
            while (s[b].ParentIndex != launchBody)
                b = s[b].ParentIndex;
            return b;
        }

        // The direction the transfer leaves the launch body along, relative to it; zero when none.
        static Vector3d Departure(BodySystem s, int launchBody, TransferPlan plan, double ut0, double orbitRadius)
        {
            if (plan.ExcessBody == launchBody && plan.RequiredExcess.magnitude > 0)
                return plan.RequiredExcess.normalized;

            // Otherwise the outgoing asymptote of the first burn, from the nominal orbit.
            var q = plan.Request;
            double mu = s[launchBody].Mu;
            foreach (var step in plan.Steps)
            {
                if (step.Body != launchBody || step.DeltaV.magnitude == 0)
                    continue;
                KeplerPropagator.Propagate(q.R0, q.V0, mu, step.UT - q.UT0, out var r, out var v);
                var a = Hyperbola.Asymptote(r, v + step.DeltaV, mu);
                return a.magnitude > 0 ? a.normalized : Vector3d.zero;
            }
            return Vector3d.zero;
        }

        // The prograde planes that hold d with inclination max(|declination|, latitude): normals
        // n = cos θ e ± sin θ f, with e the unit vector of z normal to d and f = d × e (horizontal),
        // so that n · z = cos θ cos δ = cos i.
        static List<Vector3d> Planes(Vector3d d, double latDeg)
        {
            d = d.normalized;
            var list = new List<Vector3d>();
            double cosDec = Math.Sqrt(d.x * d.x + d.y * d.y);
            if (cosDec < 1e-9)
            {
                // A polar asymptote: only polar planes hold it.
                list.Add(Vector3d.Cross(d, new Vector3d(1, 0, 0)).normalized);
                return list;
            }
            double dec = Math.Asin(Clamp(d.z)) / Deg;
            double inc = Math.Max(Math.Abs(dec), latDeg) * Deg;
            var e = (new Vector3d(0, 0, 1) - d.z * d).normalized;
            var f = Vector3d.Cross(d, e);
            double cosTheta = Clamp(Math.Cos(inc) / cosDec);
            double sinTheta = Math.Sqrt(Math.Max(0, 1 - cosTheta * cosTheta));
            list.Add((cosTheta * e + sinTheta * f).normalized);
            if (sinTheta > 1e-9)
                list.Add((cosTheta * e - sinTheta * f).normalized);
            return list;
        }

        // A transfer from a circular orbit of normal n, starting at its ascending node.
        static TransferPlan Find(BodySystem s, int from, int to, Vector3d n, double ut0, double radius,
            TransferRequest template, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            n = n.normalized;
            var node = Vector3d.Cross(new Vector3d(0, 0, 1), n);
            node = node.magnitude > 1e-9 ? node.normalized : new Vector3d(1, 0, 0);
            double speed = Math.Sqrt(s[from].Mu / radius);
            var t = template ?? new TransferRequest();
            var q = new TransferRequest
            {
                FromBody = from,
                ToBody = to,
                R0 = radius * node,
                V0 = speed * Vector3d.Cross(n, node),
                UT0 = ut0,
                Arrival = t.Arrival,
                ArrivalPeriapsisRadius = t.ArrivalPeriapsisRadius,
                ArrivalApoapsisRadius = t.ArrivalApoapsisRadius,
                ArrivalInclinationDeg = t.ArrivalInclinationDeg,
                MaxTripSeconds = t.MaxTripSeconds,
            };
            return TransferSearch.Find(s, q, ct);
        }

        static ParkingPlan Result(TransferPlan plan, Vector3d n, double radius, string why, BodyNode body)
        {
            if (!plan.Ok)
                return Refuse(plan.Refusal, radius, plan);
            double inc = Math.Acos(Clamp(n.z)) / Deg;
            return new ParkingPlan
            {
                Ok = true,
                PlaneNormal = n,
                OrbitRadius = radius,
                WindowUT = plan.DepartureUT,
                TransferDeltaV = plan.TotalDeltaV,
                Transfer = plan,
                Explanation = Inv($"Parking orbit {(radius - body.Radius) / 1000:F0} km, inclination {inc:F2}°: {why}. ") +
                              Inv($"Transfer window at UT {plan.DepartureUT:F0}, {plan.TotalDeltaV:F0} m/s."),
            };
        }

        static ParkingPlan Refuse(string why, double radius, TransferPlan plan = null) => new ParkingPlan
        {
            Ok = false,
            Refusal = why,
            OrbitRadius = radius,
            Transfer = plan,
        };

        static double OutOfPlaneDeg(Vector3d d, Vector3d n) =>
            Math.Asin(Clamp(Math.Abs(Vector3d.Dot(d.normalized, n.normalized)))) / Deg;

        static double Clamp(double x) => Math.Max(-1, Math.Min(1, x));
        static string Inv(FormattableString text) => FormattableString.Invariant(text);
    }
}
