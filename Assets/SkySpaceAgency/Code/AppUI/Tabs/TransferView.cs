using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Sim;
using SkySpaceAgency.Transfer;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// What the Transfer tab's map draws (docs/design/ui/transfer.svg), in drawing units: the centre
    /// body at (0, 0), the outer orbit at radius 1, angles in radians counter-clockwise from +x.
    /// Built from a real plan (TransferPlan + BodySystem, both orbits projected on the plane of the
    /// departure body's orbit, radii compressed: "not to scale") or from the mockup's values.
    /// Pure: no game call, tested in the editor.
    /// </summary>
    public sealed class TransferViewModel
    {
        public string CentreName = "";
        public string DepartName = "", ArriveName = "", FlybyName;
        public float DepartRadius, ArriveRadius;          // orbit circles, 0..1
        public float DepartAngle, ArriveAngle;
        public bool HasToday;                              // the target-side body today
        public float TodayAngle;
        public bool HasFlyby;
        public float FlybyRadius, FlybyAngle;
        public bool FlybyAtDeparture;                      // a moon's flyby on the way out: marked by the departure
        public float FlownFraction = -1;                   // the vessel on the arc while flying, -1: not shown

        // The arc: angles from DepartAngle (cumulative, signed) and radii, through the flyby if any.
        public readonly List<float> ArcAngles = new();
        public readonly List<float> ArcRadii = new();

        // The tiles under the map.
        public string PhaseText = "-", TripText = "-", EncounterText = "-";

        public float Sweep => ArcAngles.Count < 2 ? 0 : ArcAngles[ArcAngles.Count - 1] - ArcAngles[0];

        /// <summary>A point of the transfer arc, t from 0 (departure) to 1 (arrival), radius eased between waypoints.</summary>
        public Vector2 ArcPoint(float t)
        {
            if (ArcAngles.Count < 2)
                return Polar(DepartRadius, DepartAngle);
            t = Mathf.Clamp01(t);
            float total = Sweep;
            float a = ArcAngles[0] + total * t;
            int seg = 0;
            while (seg < ArcAngles.Count - 2 && (total >= 0 ? a > ArcAngles[seg + 1] : a < ArcAngles[seg + 1]))
                seg++;
            float a0 = ArcAngles[seg], a1 = ArcAngles[seg + 1];
            float u = Mathf.Approximately(a0, a1) ? 1 : Mathf.Clamp01((a - a0) / (a1 - a0));
            float r = Mathf.Lerp(ArcRadii[seg], ArcRadii[seg + 1], Mathf.SmoothStep(0, 1, u));
            return Polar(r, a);
        }

        public static Vector2 Polar(float r, float angle) => new Vector2(r * Mathf.Cos(angle), r * Mathf.Sin(angle));

        public Vector2 DepartPoint => Polar(DepartRadius, DepartAngle);
        public Vector2 ArrivePoint => Polar(ArriveRadius, ArriveAngle);

        /// <summary>Radii compressed so a small inner orbit stays visible: (r / rmax)^0.6, at least 0.3.</summary>
        public static float Compress(double r, double rmax) =>
            rmax > 0 && r > 0 ? Mathf.Clamp((float)Math.Pow(r / rmax, 0.6), 0.3f, 1f) : 0.3f;

        /// <summary>An angle brought into (0, 2π]: the counter-clockwise way from one body to the next.</summary>
        public static float Forward(float delta)
        {
            const float Tau = 2 * Mathf.PI;
            delta %= Tau;
            if (delta <= 1e-4f) delta += Tau;
            return delta;
        }

        /// <summary>Degrees in (-180, 180].</summary>
        public static double Wrap180(double deg)
        {
            deg %= 360;
            if (deg > 180) deg -= 360;
            if (deg <= -180) deg += 360;
            return deg;
        }

        static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);

        /// <summary>The mockup's map (transfer.svg): Kerbin at 120, Duna at 200 of 200, departure left, arrival right.</summary>
        public static TransferViewModel Sample()
        {
            var m = new TransferViewModel
            {
                CentreName = "Kerbol",
                DepartName = "Kerbin",
                ArriveName = "Duna",
                DepartRadius = 0.6f,
                ArriveRadius = 1f,
                DepartAngle = Mathf.PI,
                ArriveAngle = 0,
                HasToday = true,
                TodayAngle = Mathf.Atan2(400 - 258, 948 - 808),
                PhaseText = "44.3° now · 44.0° go",
                TripText = "231 days",
                EncounterText = "Pe 61 km",
            };
            // The mockup's half ellipse goes over the top, clockwise on screen: a negative sweep.
            m.ArcAngles.Add(Mathf.PI);
            m.ArcAngles.Add(0);
            m.ArcRadii.Add(0.6f);
            m.ArcRadii.Add(1f);
            return m;
        }

        /// <summary>The map of a plan, or null when the plan or the system cannot give one.</summary>
        public static TransferViewModel Build(BodySystem s, TransferPlan plan, double now, double flownFraction = -1)
        {
            if (s == null || plan?.Request == null || !plan.Ok)
                return null;
            try
            {
                return BuildCore(s, plan, now, flownFraction);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The child of 'ancestor' on the way from 'body' up (or -1 when body is the ancestor).
        static int ChildOf(BodySystem s, int ancestor, int body)
        {
            if (body == ancestor)
                return -1;
            var path = s.PathToRoot(body);
            for (int i = 0; i < path.Count - 1; i++)
                if (path[i + 1] == ancestor)
                    return path[i];
            return -1;
        }

        static TransferViewModel BuildCore(BodySystem s, TransferPlan plan, double now, double flownFraction)
        {
            var q = plan.Request;
            int from = q.FromBody, to = q.ToBody;
            if (from < 0 || from >= s.Count || to < 0 || to >= s.Count)
                return null;
            int anc = s.CommonAncestor(from, to);
            int dBody = ChildOf(s, anc, from), aBody = ChildOf(s, anc, to);
            double depUT = double.IsNaN(plan.DepartureUT) ? q.UT0 : plan.DepartureUT;
            double arrUT = double.IsNaN(plan.ArrivalUT) ? depUT : plan.ArrivalUT;

            Vector3d Pos(int body, double ut)
            {
                s.StateRelativeTo(body, anc, ut, out var r, out _);
                return r;
            }

            // The plane: the departure-side body's orbit (else the arrival side's, else the vessel's).
            Vector3d r0, v0;
            int planeBody = dBody >= 0 ? dBody : aBody;
            if (planeBody >= 0)
                s.StateRelativeTo(planeBody, anc, depUT, out r0, out v0);
            else
            {
                r0 = q.R0;
                v0 = q.V0;
            }
            Vector3d n = Vector3d.Cross(r0, v0);
            if (n.sqrMagnitude <= 0 || r0.sqrMagnitude <= 0)
                return null;
            Vector3d e1 = r0.normalized;
            Vector3d e2 = Vector3d.Cross(n, e1).normalized;
            float Angle(Vector3d p) => (float)Math.Atan2(Vector3d.Dot(p, e2), Vector3d.Dot(p, e1));

            double CaptureRadius()
            {
                for (int i = plan.Steps.Count - 1; i >= 0; i--)
                    if (plan.Steps[i].Kind == StepKind.Capture && plan.Steps[i].TargetPeriapsisRadius > 0)
                        return plan.Steps[i].TargetPeriapsisRadius;
                if (q.ArrivalPeriapsisRadius > 0)
                    return q.ArrivalPeriapsisRadius;
                return s[to].LowOrbitRadius;
            }

            var m = new TransferViewModel { CentreName = s[anc].Name ?? "" };
            m.DepartName = s[from].Name ?? "";
            m.ArriveName = s[to].Name ?? "";

            double rd = dBody >= 0 ? Pos(dBody, depUT).magnitude : q.R0.magnitude;
            double ra = aBody >= 0 ? Pos(aBody, arrUT).magnitude : CaptureRadius();
            double rmax = Math.Max(rd, ra);

            float depAngle = dBody >= 0 ? Angle(Pos(dBody, depUT)) : float.NaN;
            float arrAngle = aBody >= 0 ? Angle(Pos(aBody, arrUT)) : float.NaN;
            if (float.IsNaN(depAngle)) depAngle = float.IsNaN(arrAngle) ? 0 : arrAngle + Mathf.PI;
            if (float.IsNaN(arrAngle)) arrAngle = depAngle + Mathf.PI;

            // A flyby of a body orbiting the same centre puts a waypoint on the arc; a moon's flyby on
            // the way out (its parent is the departure body) is only marked by the departure.
            int fb = plan.FlybyBody;
            float fbAngle = 0, fbRadius = 0;
            bool fbOnArc = false;
            if (fb >= 0 && fb < s.Count && s.IsAncestor(anc, fb))
            {
                int fbChild = ChildOf(s, anc, fb);
                m.FlybyName = s[fb].Name;
                if (fbChild >= 0 && fbChild != dBody && fbChild != aBody)
                {
                    double fbUT = double.NaN;
                    foreach (var st in plan.Steps)
                        if (st.Kind == StepKind.Flyby) { fbUT = st.UT; break; }
                    if (!double.IsNaN(fbUT))
                    {
                        Vector3d p = Pos(fbChild, fbUT);
                        rmax = Math.Max(rmax, p.magnitude);
                        fbAngle = Angle(p);
                        fbRadius = (float)p.magnitude; // compressed below, once rmax is known
                        fbOnArc = true;
                    }
                }
                else
                    m.FlybyAtDeparture = true;
            }

            m.DepartRadius = Compress(rd, rmax);
            m.ArriveRadius = Compress(ra, rmax);
            m.DepartAngle = depAngle;
            m.ArriveAngle = arrAngle;
            m.ArcAngles.Add(depAngle);
            m.ArcRadii.Add(m.DepartRadius);
            float at = depAngle;
            if (fbOnArc)
            {
                m.HasFlyby = true;
                m.FlybyAngle = fbAngle;
                m.FlybyRadius = Compress(fbRadius, rmax);
                at += Forward(fbAngle - at);
                m.ArcAngles.Add(at);
                m.ArcRadii.Add(m.FlybyRadius);
            }
            else if (m.FlybyName != null)
                m.FlybyAtDeparture = true;
            at += Forward(arrAngle - at);
            m.ArcAngles.Add(at);
            m.ArcRadii.Add(m.ArriveRadius);

            if (aBody >= 0)
            {
                m.HasToday = true;
                m.TodayAngle = Angle(Pos(aBody, now));
            }
            if (flownFraction >= 0)
                m.FlownFraction = Mathf.Clamp01((float)flownFraction);

            // Phase angle: the target side ahead of the departure side, now and at the departure.
            if (dBody >= 0 && aBody >= 0 && dBody != aBody)
            {
                double PhaseAt(double ut) => Wrap180((Angle(Pos(aBody, ut)) - Angle(Pos(dBody, ut))) * Mathf.Rad2Deg);
                m.PhaseText = Inv($"{PhaseAt(now):F1}° now · {PhaseAt(depUT):F1}° go");
            }
            else
                m.PhaseText = "-";
            m.TripText = KerbinCalendar.Duration(plan.TripSeconds);
            double pe = CaptureRadius() - s[to].Radius;
            m.EncounterText = pe > 0 ? Inv($"Pe {pe / 1000:F0} km") : "-";
            return m;
        }
    }

    /// <summary>Body colours of the map and the destination tiles (the mockup's, then a guess per body).</summary>
    public static class TransferBodyColors
    {
        static readonly Dictionary<string, int> Known = new()
        {
            { "Kerbol", 0xffc04a }, { "Moho", 0xb08860 }, { "Eve", 0xa070d0 }, { "Gilly", 0xb09a8a },
            { "Kerbin", 0x2d8ec4 }, { "Mun", 0xa9b0bf }, { "Minmus", 0x9fd9c9 }, { "Duna", 0xd35a36 },
            { "Ike", 0x8f8f96 }, { "Dres", 0x9a8f86 }, { "Jool", 0x6fae4a }, { "Laythe", 0x4a7ab0 },
            { "Vall", 0xa8c0d0 }, { "Tylo", 0xc8b8a8 }, { "Bop", 0x8a6a50 }, { "Pol", 0xd8c070 },
            { "Eeloo", 0xd8dce0 },
        };

        static readonly int[] Others = { 0x8fd3ff, 0xc9b2ff, 0xffb08a, 0x9fd9c9, 0xd6c48a, 0xa9b0bf };

        public static Color For(string name)
        {
            if (string.IsNullOrEmpty(name))
                return SsaPalette.Muted;
            if (Known.TryGetValue(name, out int rgb))
                return SsaPalette.Hex(rgb);
            int h = 0;
            foreach (char c in name) h = h * 31 + c;
            return SsaPalette.Hex(Others[(h & 0x7fffffff) % Others.Length]);
        }
    }

    /// <summary>
    /// The Transfer tab's map (Painter2D): the centre body, the departure and arrival orbits, the
    /// transfer arc in gold, the departure and arrival markers, the ejection node, the target today,
    /// the flyby body. Repaints only when SetModel gives a different model.
    /// </summary>
    public class TransferView : VisualElement
    {
        public TransferViewModel Model { get; private set; }
        readonly Label _depart, _arrive, _today, _node, _flyby, _empty;

        public TransferView()
        {
            AddToClassList("ssa-plot");
            AddToClassList("ssa-transfer-view");
            name = "transfer-map";
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            _depart = Note(SsaPalette.Hex(0x8fd3ff));
            _arrive = Note(SsaPalette.Hex(0xffb08a));
            _today = Note(SsaPalette.Hex(0x8f6a60));
            _node = Note(SsaPalette.Hex(0x8fc4ff));
            _flyby = Note(SsaPalette.Hex(0xc9b2ff));
            _empty = Ui.Label("Plan a transfer: its trajectory shows here.", "ssa-transfer-view__empty");
            _empty.pickingMode = PickingMode.Ignore;
            Add(_empty);
            RegisterCallback<GeometryChangedEvent>(_ => PlaceNotes());
            SetModel(null);
        }

        Label Note(Color color)
        {
            var l = Ui.Label("", "ssa-plot__note");
            l.style.color = color;
            l.pickingMode = PickingMode.Ignore;
            Add(l);
            return l;
        }

        public void SetModel(TransferViewModel model)
        {
            Model = model;
            bool has = model != null;
            _empty.style.display = has ? DisplayStyle.None : DisplayStyle.Flex;
            foreach (var l in new[] { _depart, _arrive, _today, _node, _flyby })
                l.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (has)
            {
                _depart.text = model.DepartName + " · depart";
                _arrive.text = model.ArriveName + " · arrive";
                _today.text = model.ArriveName + " today";
                _today.style.display = model.HasToday ? DisplayStyle.Flex : DisplayStyle.None;
                _node.text = "ejection node";
                _flyby.text = model.FlybyName != null ? model.FlybyName + " · flyby" : "";
                _flyby.style.display = model.FlybyName != null ? DisplayStyle.Flex : DisplayStyle.None;
            }
            PlaceNotes();
            MarkDirtyRepaint();
        }

        // Centre and scale: the outer orbit (radius 1) fits the element with a margin.
        static void Frame(Rect r, out Vector2 c, out float k)
        {
            c = r.center;
            k = Mathf.Max(1, Mathf.Min(r.width, r.height) * 0.5f - 12);
        }

        // y up in the model, down on screen.
        static Vector2 Screen(Vector2 c, float k, Vector2 p) => new Vector2(c.x + p.x * k, c.y - p.y * k);

        void Place(Label l, Vector2 at, bool right)
        {
            l.style.top = at.y;
            if (right)
            {
                l.style.left = StyleKeyword.Auto;
                l.style.right = contentRect.width - at.x;
            }
            else
            {
                l.style.right = StyleKeyword.Auto;
                l.style.left = at.x;
            }
        }

        void PlaceNotes()
        {
            var m = Model;
            Rect r = contentRect;
            if (m == null || r.width < 10 || r.height < 10)
                return;
            Frame(r, out var c, out float k);
            Vector2 d = Screen(c, k, m.DepartPoint), a = Screen(c, k, m.ArrivePoint);
            Place(_depart, d + new Vector2(d.x <= c.x ? -14 : 14, 14), d.x <= c.x);
            Place(_arrive, a + new Vector2(a.x <= c.x ? -14 : 14, 14), a.x <= c.x);
            Vector2 node = Screen(c, k, m.ArcPoint(0.06f));
            Place(_node, node + new Vector2(14, -16), false);
            if (m.HasToday)
            {
                Vector2 t = Screen(c, k, TransferViewModel.Polar(m.ArriveRadius, m.TodayAngle));
                Place(_today, t + new Vector2(14, -18), false);
            }
            if (m.FlybyName != null)
            {
                Vector2 f = m.HasFlyby ? Screen(c, k, TransferViewModel.Polar(m.FlybyRadius, m.FlybyAngle)) : d + new Vector2(0, 30);
                Place(_flyby, f + new Vector2(12, 4), false);
            }
        }

        static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10)
                return;
            var p = ctx.painter2D;
            Frame(r, out var c, out float k);
            float u = k / 200f; // the mockup's pixels, its outer orbit being 200

            // Grid cross.
            p.strokeColor = Alpha(SsaPalette.Cyan, 0.06f);
            p.lineWidth = 1;
            Line(p, new Vector2(c.x, c.y - k * 0.9f), new Vector2(c.x, c.y + k * 0.95f));
            Line(p, new Vector2(c.x - k * 1.55f, c.y), new Vector2(c.x + k * 1.55f, c.y));

            var m = Model;
            Color centre = m == null || m.CentreName == "Kerbol" ? SsaPalette.Hex(0xffc04a) : TransferBodyColors.For(m.CentreName);

            // The centre body: a faint halo, the disc, a bright core.
            p.fillColor = Alpha(SsaPalette.Hex(0xffd27a), 0.12f);
            Disc(p, c, 44 * u);
            p.fillColor = centre;
            Disc(p, c, 24 * u);
            p.fillColor = Alpha(SsaPalette.Hex(0xfff3c4), 0.8f);
            Disc(p, c, 12 * u);
            if (m == null)
                return;

            Color depC = TransferBodyColors.For(m.DepartName), arrC = TransferBodyColors.For(m.ArriveName);

            // The orbits: departure solid, arrival dashed.
            p.strokeColor = SsaPalette.Hex(0x2a5a9a);
            p.lineWidth = 2 * u + 0.5f;
            p.BeginPath(); p.Arc(c, m.DepartRadius * k, 0, 360); p.Stroke();
            p.strokeColor = Color.Lerp(arrC, SsaPalette.Night, 0.45f);
            Dashed(p, Circle(c, m.ArriveRadius * k), 4 * u, 7 * u);
            if (m.HasFlyby)
            {
                p.strokeColor = Alpha(TransferBodyColors.For(m.FlybyName), 0.35f);
                Dashed(p, Circle(c, m.FlybyRadius * k), 3 * u, 8 * u);
            }

            // The target from today to its arrival point.
            if (m.HasToday)
            {
                // The target moves the way the transfer goes (counter-clockwise in a real plan).
                float sign = m.Sweep >= 0 ? 1 : -1;
                float sweep = sign * TransferViewModel.Forward(sign * (m.ArriveAngle - m.TodayAngle));
                if (Mathf.Abs(sweep) < 2 * Mathf.PI - 0.01f)
                {
                    var pts = new List<Vector2>();
                    for (int i = 0; i <= 48; i++)
                        pts.Add(Screen(c, k, TransferViewModel.Polar(m.ArriveRadius, m.TodayAngle + sweep * i / 48f)));
                    p.strokeColor = Alpha(arrC, 0.5f);
                    p.lineWidth = 2 * u + 0.5f;
                    Poly(p, pts);
                }
            }

            // The transfer arc in gold, and the rest of its ellipse faint and dashed.
            var arc = new List<Vector2>();
            for (int i = 0; i <= 80; i++)
                arc.Add(Screen(c, k, m.ArcPoint(i / 80f)));
            var back = new List<Vector2>();
            float rest = 2 * Mathf.PI - Mathf.Abs(m.Sweep);
            if (rest > 0.05f)
            {
                float sign = m.Sweep >= 0 ? 1 : -1, a0 = m.ArcAngles[m.ArcAngles.Count - 1];
                for (int i = 0; i <= 48; i++)
                {
                    float t = i / 48f;
                    float rr = Mathf.Lerp(m.ArriveRadius, m.DepartRadius, Mathf.SmoothStep(0, 1, t));
                    back.Add(Screen(c, k, TransferViewModel.Polar(rr, a0 + sign * rest * t)));
                }
                p.strokeColor = Alpha(SsaPalette.Gold, 0.22f);
                p.lineWidth = 2 * u + 0.5f;
                Dashed(p, back, 6 * u, 8 * u);
            }
            p.strokeColor = SsaPalette.GoldLight;
            p.lineWidth = 5 * u + 1;
            p.lineCap = LineCap.Round;
            Poly(p, arc);
            p.lineCap = LineCap.Butt;

            Vector2 d = Screen(c, k, m.DepartPoint), a = Screen(c, k, m.ArrivePoint);
            p.fillColor = depC;
            Disc(p, d, 12 * u + 1);
            p.fillColor = arrC;
            Disc(p, a, 10 * u + 1);

            // The target today: a dashed ring.
            if (m.HasToday)
            {
                p.strokeColor = arrC;
                p.lineWidth = 2 * u + 0.5f;
                Dashed(p, Circle(Screen(c, k, TransferViewModel.Polar(m.ArriveRadius, m.TodayAngle)), 9 * u + 1), 3 * u, 3 * u);
            }

            // The flyby body.
            if (m.FlybyName != null)
            {
                Vector2 f = m.HasFlyby ? Screen(c, k, TransferViewModel.Polar(m.FlybyRadius, m.FlybyAngle)) : d + new Vector2(0, 26 * u);
                p.fillColor = TransferBodyColors.For(m.FlybyName);
                Disc(p, f, 8 * u + 1);
            }

            // The ejection node: a blue ring a little along the arc.
            Vector2 node = Screen(c, k, m.ArcPoint(0.06f));
            p.fillColor = SsaPalette.Hex(0x0c1640);
            Disc(p, node, 11 * u + 1);
            p.strokeColor = SsaPalette.Node;
            p.lineWidth = 4 * u + 0.5f;
            p.BeginPath(); p.Arc(node, 11 * u + 1, 0, 360); p.Stroke();
            p.fillColor = SsaPalette.Node;
            Disc(p, node, 4 * u + 0.5f);

            // The vessel while flying: a night disc in a gold ring on the arc.
            if (m.FlownFraction >= 0)
            {
                Vector2 v = Screen(c, k, m.ArcPoint(m.FlownFraction));
                p.fillColor = SsaPalette.Night;
                Disc(p, v, 8 * u + 1);
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 3 * u + 0.5f;
                p.BeginPath(); p.Arc(v, 8 * u + 1, 0, 360); p.Stroke();
            }
        }

        static void Disc(Painter2D p, Vector2 at, float r)
        {
            p.BeginPath(); p.Arc(at, r, 0, 360); p.Fill();
        }

        static void Line(Painter2D p, Vector2 a, Vector2 b)
        {
            p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke();
        }

        static void Poly(Painter2D p, List<Vector2> pts)
        {
            if (pts.Count < 2) return;
            p.BeginPath();
            p.MoveTo(pts[0]);
            for (int i = 1; i < pts.Count; i++) p.LineTo(pts[i]);
            p.Stroke();
        }

        static List<Vector2> Circle(Vector2 c, float r)
        {
            var pts = new List<Vector2>(97);
            for (int i = 0; i <= 96; i++)
            {
                float a = i / 96f * 2 * Mathf.PI;
                pts.Add(c + new Vector2(r * Mathf.Cos(a), r * Mathf.Sin(a)));
            }
            return pts;
        }

        // A polyline in dashes of `dash` px separated by `gap` px.
        static void Dashed(Painter2D p, List<Vector2> pts, float dash, float gap)
        {
            dash = Mathf.Max(dash, 0.5f);
            gap = Mathf.Max(gap, 0.5f);
            float period = dash + gap, along = 0;
            p.BeginPath();
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 a = pts[i - 1], b = pts[i];
                float len = Vector2.Distance(a, b);
                float s = 0;
                while (s < len)
                {
                    float phase = (along + s) % period;
                    bool on = phase < dash;
                    float step = Mathf.Min(len - s, on ? dash - phase : period - phase);
                    if (on)
                    {
                        p.MoveTo(Vector2.Lerp(a, b, s / len));
                        p.LineTo(Vector2.Lerp(a, b, (s + step) / len));
                    }
                    s += Mathf.Max(step, 0.01f);
                }
                along += len;
            }
            p.Stroke();
        }
    }

    /// <summary>The Δv budget ring (transfer.svg): the plan's need over what the vessel has, gold (coral when short).</summary>
    public class TransferBudgetRing : VisualElement
    {
        readonly Label _value, _of;
        public float Fraction { get; private set; }
        public bool Short { get; private set; }

        public TransferBudgetRing()
        {
            AddToClassList("ssa-transfer-ring");
            name = "budget-ring";
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            var centre = Ui.Box("ssa-transfer-ring__centre");
            centre.pickingMode = PickingMode.Ignore;
            _value = Ui.Label("-", "ssa-transfer-ring__value");
            _of = Ui.Label("", "ssa-transfer-ring__of");
            centre.Add(_value);
            centre.Add(_of);
            Add(centre);
        }

        public string ValueText => _value.text;
        public string OfText => _of.text;

        public void Set(string value, string of, float fraction, bool isShort)
        {
            fraction = float.IsNaN(fraction) ? 0 : Mathf.Clamp01(fraction);
            bool changed = !Mathf.Approximately(fraction, Fraction) || isShort != Short;
            _value.text = value ?? "";
            _of.text = of ?? "";
            Fraction = fraction;
            Short = isShort;
            EnableInClassList("ssa-transfer-ring--short", isShort);
            if (changed) MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            float size = Mathf.Min(r.width, r.height);
            if (size < 10) return;
            var p = ctx.painter2D;
            float w = size * 18f / 198f;
            float rad = size / 2 - w / 2;
            Vector2 c = r.center;
            p.lineWidth = w;
            p.strokeColor = SsaPalette.Active;
            p.BeginPath(); p.Arc(c, rad, 0, 360); p.Stroke();
            if (Fraction <= 0) return;
            p.strokeColor = Short ? SsaPalette.Alert : SsaPalette.Gold;
            p.lineCap = LineCap.Butt;
            p.BeginPath();
            p.Arc(c, rad, -90, -90 + 360 * Fraction);
            p.Stroke();
        }
    }
}
