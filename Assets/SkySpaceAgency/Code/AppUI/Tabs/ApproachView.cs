using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>What the approach view draws (the tab fills it from the pilot or the sample).</summary>
    public struct ApproachState
    {
        public bool HasTarget;
        /// <summary>True: the target's docking port frame (the corridor and the holds are drawn).
        /// False: the target's orbit frame (prograde to the right, radial up), before the docking.</summary>
        public bool PortFrame;
        public double Distance;     // m
        public double Angle;        // radians from the axis, positive up
        public string Note;         // under our vessel: "hold at 50 m", "182 km"…
        public string Message;      // centred, when there is nothing to draw ("Choose a target")

        public bool SameAs(ApproachState o) =>
            HasTarget == o.HasTarget && PortFrame == o.PortFrame && Note == o.Note && Message == o.Message &&
            Close(Distance, o.Distance, Math.Max(0.05, Math.Abs(Distance) * 0.002)) && Close(Angle, o.Angle, 0.002);

        static bool Close(double a, double b, double eps) =>
            double.IsNaN(a) ? double.IsNaN(b) : !double.IsNaN(b) && Math.Abs(a - b) <= eps;
    }

    /// <summary>
    /// The Rendezvous tab's APPROACH view (docs/design/ui/rendezvous.svg), drawn with Painter2D:
    /// distance rings 10 / 50 / 200 m around the target, the approach corridor along its port axis,
    /// the hold points, and our vessel with a dashed line to the port. Coordinates are the mockup's
    /// (view box x 456..1160, y 224..680), scaled uniformly to the element. It repaints only when
    /// Set receives a different state.
    /// </summary>
    public class ApproachView : VisualElement
    {
        static readonly Rect Box = new Rect(456, 224, 704, 456);
        static readonly Vector2 Centre = new Vector2(808, 440);

        readonly List<(Label label, float radius)> _rings = new();
        readonly Label _corridor, _note, _message;

        public ApproachState State { get; private set; }

        public ApproachView()
        {
            AddToClassList("ssa-plot");
            AddToClassList("ssa-rendezvous-approach");
            name = "approach-view";
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            foreach (var (text, r) in new[] { ("10 m", ApproachGeometry.R10), ("50 m", ApproachGeometry.R50), ("200 m", ApproachGeometry.R200) })
                _rings.Add((NewLabel(text, "ssa-plot__note", SsaPalette.Dim), r));
            _corridor = NewLabel("approach corridor", "ssa-plot__note", SsaPalette.Ok);
            _note = NewLabel("", "ssa-plot__note", SsaPalette.GoldLight);
            _note.AddToClassList("ssa-rendezvous-approach__centred");
            _message = NewLabel("", "ssa-plot__note", SsaPalette.Muted);
            _message.AddToClassList("ssa-rendezvous-approach__centred");
            State = new ApproachState { Distance = double.NaN, Message = "Choose a target" };
            RegisterCallback<GeometryChangedEvent>(_ => PlaceLabels());
            PlaceLabels();
        }

        Label NewLabel(string text, string cls, Color color)
        {
            var l = Ui.Label(text, cls);
            l.style.color = color;
            l.pickingMode = PickingMode.Ignore;
            Add(l);
            return l;
        }

        /// <summary>Takes a new state; repaints only when it differs from the drawn one.</summary>
        public bool Set(ApproachState s)
        {
            if (s.SameAs(State)) return false;
            State = s;
            PlaceLabels();
            MarkDirtyRepaint();
            return true;
        }

        public string NoteText => _note.text;
        public string MessageText => _message.text;

        float Scale(Rect r) => Mathf.Min(r.width / Box.width, r.height / Box.height);

        // The target's place in the element: the box's centre, moved as in the mockup.
        Vector2 Origin(Rect r) => r.center + (Centre - Box.center) * Scale(r);

        Vector2 Vessel(Rect r) => Origin(r) + ApproachGeometry.Place(State.Distance, State.Angle) * Scale(r);

        void PlaceLabels()
        {
            Rect r = contentRect;
            if (float.IsNaN(r.width) || r.width < 10) r = new Rect(0, 0, Box.width, Box.height);
            float k = Scale(r);
            Vector2 o = Origin(r);
            foreach (var (label, radius) in _rings)
            {
                label.style.left = o.x + (radius * 0.7f + 6) * k;
                label.style.top = o.y - radius * 0.7f * k - 14 * k;
            }
            bool port = State.HasTarget && State.PortFrame;
            _corridor.style.display = port ? DisplayStyle.Flex : DisplayStyle.None;
            _corridor.style.left = StyleKeyword.Auto;
            _corridor.style.right = r.width - (o.x + (ApproachGeometry.CorridorLength - 10) * k);
            _corridor.style.top = o.y - 100 * k;

            bool vessel = State.HasTarget && !double.IsNaN(State.Distance);
            _note.text = State.Note ?? "";
            _note.style.display = vessel && !string.IsNullOrEmpty(State.Note) ? DisplayStyle.Flex : DisplayStyle.None;
            if (vessel)
            {
                Vector2 v = Vessel(r);
                _note.style.left = v.x;
                _note.style.top = v.y + 24 * k;
            }
            _message.text = State.Message ?? "";
            _message.style.display = string.IsNullOrEmpty(State.Message) ? DisplayStyle.None : DisplayStyle.Flex;
            _message.style.left = o.x;
            _message.style.top = o.y + (ApproachGeometry.R50 + 18) * k;
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 10 || r.height < 10) return;
            float k = Scale(r);
            Vector2 o = Origin(r);
            var p = ctx.painter2D;

            // Distance rings: dashed cyan, 18 %.
            p.strokeColor = new Color(SsaPalette.Cyan.r, SsaPalette.Cyan.g, SsaPalette.Cyan.b, 0.18f);
            p.lineWidth = 1;
            foreach (float radius in new[] { ApproachGeometry.R10, ApproachGeometry.R50, ApproachGeometry.R200 })
                DashedCircle(p, o, radius * k, 4 * k, 6 * k);

            bool port = State.HasTarget && State.PortFrame;
            if (port)
            {
                // Approach corridor along the port axis.
                Vector2 a = o + new Vector2(ApproachGeometry.CorridorLength, -ApproachGeometry.CorridorHalfWidth) * k;
                Vector2 b = o + new Vector2(ApproachGeometry.CorridorLength, ApproachGeometry.CorridorHalfWidth) * k;
                p.fillColor = new Color(SsaPalette.Ok.r, SsaPalette.Ok.g, SsaPalette.Ok.b, 0.06f);
                p.strokeColor = new Color(SsaPalette.Ok.r, SsaPalette.Ok.g, SsaPalette.Ok.b, 0.35f);
                p.BeginPath(); p.MoveTo(o); p.LineTo(a); p.LineTo(b); p.ClosePath(); p.Fill(); p.Stroke();
            }

            DrawStation(p, o, k, port);

            if (port)
            {
                // Hold points on the axis, at 50 m and 10 m.
                p.strokeColor = SsaPalette.GoldLight;
                p.lineWidth = 1.5f * k;
                foreach (float radius in new[] { ApproachGeometry.R50, ApproachGeometry.R10 })
                {
                    Vector2 h = o + new Vector2(radius, 0) * k;
                    p.BeginPath();
                    p.MoveTo(h + new Vector2(0, -6) * k); p.LineTo(h + new Vector2(6, 0) * k);
                    p.LineTo(h + new Vector2(0, 6) * k); p.LineTo(h + new Vector2(-6, 0) * k);
                    p.ClosePath();
                    p.Stroke();
                }
            }

            if (!State.HasTarget || double.IsNaN(State.Distance)) return;

            // Our vessel and the dashed line from the port (or the target's centre) to it.
            Vector2 v = Vessel(r);
            Vector2 from = port ? o + new Vector2(50, 0) * k : o;
            p.strokeColor = SsaPalette.GoldLight;
            p.lineWidth = 3 * k;
            if (Vector2.Distance(from, v) > 12 * k)
                DashedLine(p, from, v - (v - from).normalized * 12 * k, 8 * k, 6 * k);
            DrawVessel(p, v, o, k);
        }

        static void DrawStation(Painter2D p, Vector2 o, float k, bool port)
        {
            Vector2 P(float x, float y) => o + new Vector2(x, y) * k;
            void Rect(float x, float y, float w, float h)
            {
                p.BeginPath(); p.MoveTo(P(x, y)); p.LineTo(P(x + w, y)); p.LineTo(P(x + w, y + h)); p.LineTo(P(x, y + h)); p.ClosePath();
            }
            p.fillColor = SsaPalette.Hex(0x2a5fb0);
            p.strokeColor = SsaPalette.Hex(0x9ec8ff);
            p.lineWidth = 1;
            foreach (var (x, y) in new[] { (-34f, -58f), (-34f, 14f), (8f, -58f), (8f, 14f) })
            {
                Rect(x, y, 26, 44); p.Fill(); p.Stroke();
            }
            p.fillColor = SsaPalette.Hex(0xdfe5f0);
            Rect(-40, -12, 80, 24); p.Fill();
            if (port)
            {
                p.fillColor = SsaPalette.GoldLight;
                Rect(40, -6, 10, 12); p.Fill();
            }
        }

        // The mockup's vessel: a trapezoid with a gold nose, the nose towards the target.
        static void DrawVessel(Painter2D p, Vector2 at, Vector2 target, float k)
        {
            Vector2 u = target - at;
            u = u.sqrMagnitude < 1e-6f ? Vector2.left : u.normalized;
            Vector2 w = new Vector2(-u.y, u.x);
            Vector2 G(float x, float y) => at + (x * w - y * u) * k;
            p.fillColor = SsaPalette.Hex(0xdfe5f0);
            p.BeginPath(); p.MoveTo(G(-10, -12)); p.LineTo(G(10, -12)); p.LineTo(G(7, 10)); p.LineTo(G(-7, 10)); p.ClosePath(); p.Fill();
            p.fillColor = SsaPalette.GoldLight;
            p.BeginPath(); p.MoveTo(G(-4, -17)); p.LineTo(G(4, -17)); p.LineTo(G(4, -12)); p.LineTo(G(-4, -12)); p.ClosePath(); p.Fill();
        }

        static void DashedCircle(Painter2D p, Vector2 c, float radius, float dash, float gap)
        {
            if (radius <= 0) return;
            float circumference = 2 * Mathf.PI * radius;
            int n = Mathf.Max(4, Mathf.FloorToInt(circumference / (dash + gap)));
            float step = 360f / n, on = step * dash / (dash + gap);
            for (int i = 0; i < n; i++)
            {
                p.BeginPath();
                p.Arc(c, radius, i * step, i * step + on);
                p.Stroke();
            }
        }

        static void DashedLine(Painter2D p, Vector2 a, Vector2 b, float dash, float gap)
        {
            float len = Vector2.Distance(a, b);
            if (len <= 0) return;
            Vector2 d = (b - a) / len;
            p.BeginPath();
            for (float s = 0; s < len; s += dash + gap)
            {
                p.MoveTo(a + d * s);
                p.LineTo(a + d * Mathf.Min(len, s + dash));
            }
            p.Stroke();
        }
    }
}
