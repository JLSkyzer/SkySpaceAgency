using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace K2D2.Transfer
{
    // Refinement of a 2-D search grid (departure UT × time of flight): the best local minima, each
    // polished by Nelder–Mead on the same cost. Pure code.
    static class GridRefiner
    {
        // Up to 'count' refined points, cheapest first. 'cost' is ∞ outside the search domain.
        public static List<(double t, double tof, bool prograde)> Refine(double[,] grid, bool[,] gridDir, double t0, double dt,
            double tof0, double dTof, Func<double, double, bool, double> cost, int count, CancellationToken ct)
        {
            int n = grid.GetLength(0), m = grid.GetLength(1);
            var minima = new List<(int i, int j)>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < m; j++)
                {
                    double c = grid[i, j];
                    if (double.IsInfinity(c) || double.IsNaN(c))
                        continue;
                    bool isMin = true;
                    for (int di = -1; di <= 1 && isMin; di++)
                        for (int dj = -1; dj <= 1; dj++)
                        {
                            int a = i + di, b = j + dj;
                            if ((di != 0 || dj != 0) && a >= 0 && a < n && b >= 0 && b < m && grid[a, b] < c)
                            {
                                isMin = false;
                                break;
                            }
                        }
                    if (isMin)
                        minima.Add((i, j));
                }
            minima.Sort((p, q) => grid[p.i, p.j].CompareTo(grid[q.i, q.j]));

            var result = new List<(double t, double tof, bool prograde, double cost)>();
            foreach (var (i, j) in minima.Take(count))
            {
                bool d = gridDir[i, j];
                double x = t0 + dt * i, y = tof0 + dTof * j;
                double f = NelderMead((a, b) => cost(a, b, d), ref x, ref y, 0.5 * dt, 0.5 * dTof, ct);
                if (!(f <= grid[i, j]))
                {
                    x = t0 + dt * i;
                    y = tof0 + dTof * j;
                    f = grid[i, j];
                }
                result.Add((x, y, d, f));
            }
            return result.OrderBy(r => r.cost).Select(r => (r.t, r.tof, r.prograde)).ToList();
        }

        // 2-D Nelder–Mead from (x, y) with initial steps dx, dy. Returns the best value; x, y are moved there.
        public static double NelderMead(Func<double, double, double> f, ref double x, ref double y, double dx, double dy, CancellationToken ct)
        {
            var px = new[] { x, x + dx, x };
            var py = new[] { y, y, y + dy };
            var fv = new[] { f(px[0], py[0]), f(px[1], py[1]), f(px[2], py[2]) };
            for (int iter = 0; iter < 100; iter++)
            {
                ct.ThrowIfCancellationRequested();
                // Order: 0 best, 2 worst.
                for (int a = 0; a < 2; a++)
                    for (int b = a + 1; b < 3; b++)
                        if (fv[b] < fv[a])
                        {
                            (px[a], px[b]) = (px[b], px[a]);
                            (py[a], py[b]) = (py[b], py[a]);
                            (fv[a], fv[b]) = (fv[b], fv[a]);
                        }
                double size = Math.Max(Math.Abs(px[2] - px[0]) / dx, Math.Abs(py[2] - py[0]) / dy);
                if (size < 1e-3 || (!double.IsInfinity(fv[2]) && fv[2] - fv[0] < 1e-4))
                    break;
                double cx = 0.5 * (px[0] + px[1]), cy = 0.5 * (py[0] + py[1]);
                double rx = 2 * cx - px[2], ry = 2 * cy - py[2], fr = f(rx, ry);
                if (fr < fv[0])
                {
                    double ex = 3 * cx - 2 * px[2], ey = 3 * cy - 2 * py[2], fe = f(ex, ey);
                    if (fe < fr) { px[2] = ex; py[2] = ey; fv[2] = fe; }
                    else { px[2] = rx; py[2] = ry; fv[2] = fr; }
                }
                else if (fr < fv[1])
                {
                    px[2] = rx; py[2] = ry; fv[2] = fr;
                }
                else
                {
                    double kx = 0.5 * (cx + px[2]), ky = 0.5 * (cy + py[2]), fk = f(kx, ky);
                    if (fk < fv[2]) { px[2] = kx; py[2] = ky; fv[2] = fk; }
                    else
                        for (int a = 1; a < 3; a++)
                        {
                            px[a] = 0.5 * (px[0] + px[a]);
                            py[a] = 0.5 * (py[0] + py[a]);
                            fv[a] = f(px[a], py[a]);
                        }
                }
            }
            int best = 0;
            for (int a = 1; a < 3; a++)
                if (fv[a] < fv[best])
                    best = a;
            x = px[best];
            y = py[best];
            return fv[best];
        }
    }
}
