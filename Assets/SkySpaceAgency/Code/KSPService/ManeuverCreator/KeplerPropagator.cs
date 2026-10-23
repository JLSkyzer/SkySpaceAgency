using System;
using KSP.Sim;

namespace KSP2FlightAssistant.MathLibrary
{
    // Self-contained two-body Kepler orbit propagator (the "universal variable" formulation -
    // Curtis, "Orbital Mechanics for Engineering Students"). Added for precision landing's
    // deorbit-burn search (LandingTargeting.cs): it answers "if I burned this much at this
    // point, where would I be at some later time" using nothing but plain position/velocity
    // vectors and a gravitational parameter - no KSP2/Redux API involved at all. That's
    // deliberate: this whole feature has repeatedly tripped over surprises in how Redux
    // represents the live vessel's orbit (CurrentPatchedConicsOrbit not being a
    // PatchedConicsOrbit, etc.) - a hypothetical/what-if orbit doesn't need to touch any of
    // that, it's just textbook orbital mechanics on numbers we already have.
    //
    // New and untested in-game as of writing, same as the rest of this feature - but this part
    // in particular is standard, well-documented math (not a guess about undocumented engine
    // behavior), so the main risk is an arithmetic slip rather than an API surprise.
    public static class KeplerPropagator
    {
        public static void Propagate(Vector3d r0, Vector3d v0, double mu, double dt, out Vector3d r1, out Vector3d v1)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            double vr0 = Vector3d.Dot(r0, v0) / r0mag;
            double alpha = 2.0 / r0mag - v0mag * v0mag / mu;

            double sqrtMu = Math.Sqrt(mu);
            double chi = sqrtMu * Math.Abs(alpha) * dt;

            double z;
            bool converged = false;
            for (int i = 0; i < 100; i++)
            {
                KeplerFunction(chi, r0mag, vr0, alpha, sqrtMu, dt, out double F, out double dF);

                if (Math.Abs(dF) < 1e-12)
                    break;

                double ratio = F / dF;
                chi -= ratio;

                if (Math.Abs(ratio) < 1e-8)
                {
                    converged = true;
                    break;
                }
            }
            // On very eccentric orbits, Newton from the mean-motion guess can land near periapsis
            // (small dF = r) and jump around without settling (found by the transfer Lambert tests).
            // F rises strictly with chi (dF = r > 0), so a bracketed solve always converges. Only
            // this non-converged case takes it, so every result that converged before is unchanged.
            if (!converged)
                chi = SolveBracketed(r0mag, vr0, alpha, sqrtMu, dt, sqrtMu * Math.Abs(alpha) * dt);

            z = alpha * chi * chi;
            double Cf = StumpffC(z);
            double Sf = StumpffS(z);

            double f = 1 - (chi * chi / r0mag) * Cf;
            double g = dt - (chi * chi * chi / sqrtMu) * Sf;

            r1 = f * r0 + g * v0;
            double r1mag = r1.magnitude;

            double fdot = (sqrtMu / (r1mag * r0mag)) * (alpha * chi * chi * chi * Sf - chi);
            double gdot = 1 - (chi * chi / r1mag) * Cf;

            v1 = fdot * r0 + gdot * v0;
        }

        // Universal Kepler equation F(chi) = 0 and its derivative dF/dchi (= r at chi).
        static void KeplerFunction(double chi, double r0mag, double vr0, double alpha, double sqrtMu, double dt,
            out double F, out double dF)
        {
            double z = alpha * chi * chi;
            double C = StumpffC(z);
            double S = StumpffS(z);

            F = (r0mag * vr0 / sqrtMu) * chi * chi * C
                + (1 - alpha * r0mag) * chi * chi * chi * S
                + r0mag * chi - sqrtMu * dt;

            dF = (r0mag * vr0 / sqrtMu) * chi * (1 - alpha * chi * chi * S)
                + (1 - alpha * r0mag) * chi * chi * C
                + r0mag;
        }

        // Newton kept inside a bracket [lo, hi] of the root, with bisection when a step leaves it.
        // F(0) = -sqrtMu*dt, so the root has the sign of dt; the far bound is found by doubling.
        static double SolveBracketed(double r0mag, double vr0, double alpha, double sqrtMu, double dt, double guess)
        {
            if (dt == 0)
                return 0;
            double sign = Math.Sign(dt);
            double far = Math.Max(Math.Abs(guess), 1.0) * sign;
            for (int i = 0; i < 200; i++)
            {
                KeplerFunction(far, r0mag, vr0, alpha, sqrtMu, dt, out double Ffar, out _);
                if (Ffar * sign > 0)
                    break;
                far *= 2;
            }
            double lo = Math.Min(0, far), hi = Math.Max(0, far);

            double chi = 0.5 * (lo + hi);
            for (int i = 0; i < 200; i++)
            {
                KeplerFunction(chi, r0mag, vr0, alpha, sqrtMu, dt, out double F, out double dF);
                if (F < 0)
                    lo = chi;
                else
                    hi = chi;

                double next = dF > 0 ? chi - F / dF : double.NaN;
                if (!(next > lo && next < hi))
                    next = 0.5 * (lo + hi);
                if (Math.Abs(next - chi) < 1e-8 || hi - lo < 1e-12 * Math.Max(1, Math.Abs(chi)))
                    return next;
                chi = next;
            }
            return chi;
        }

        static double StumpffC(double z)
        {
            if (z > 1e-6) return (1 - Math.Cos(Math.Sqrt(z))) / z;
            if (z < -1e-6) return (Math.Cosh(Math.Sqrt(-z)) - 1) / (-z);
            return 0.5;
        }

        static double StumpffS(double z)
        {
            if (z > 1e-6)
            {
                double sz = Math.Sqrt(z);
                return (sz - Math.Sin(sz)) / (sz * sz * sz);
            }
            if (z < -1e-6)
            {
                double sz = Math.Sqrt(-z);
                return (Math.Sinh(sz) - sz) / (sz * sz * sz);
            }
            return 1.0 / 6.0;
        }
    }
}
