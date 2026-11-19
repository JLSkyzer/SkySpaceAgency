using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The NODES tab's BURN view (docs/design/ui/nodes.png): a navball seen from the front, its centre
    /// the vessel's nose (gold chevron), the horizon from the pitch, and the burn vector (blue
    /// maneuver marker) placed by its angle to the nose. Drawn with Painter2D; Set repaints only when
    /// the values move. Geometry in NodesLogic (MarkerOffset, HorizonOffset, GroundArc).
    /// </summary>
    public class NodesBurnView : VisualElement
    {
        // The mockup's ball: radius 170 px of the 1600 x 900 drawing.
        const float MockupRadius = 170;

        readonly Label _note;
        StatusKind _noteKind = StatusKind.Neutral;

        public bool HasBurn { get; private set; }
        public float AngleDeg { get; private set; } = float.NaN;
        public Vector2 Direction { get; private set; }
        public float PitchDeg { get; private set; }
        public float MaxAngle { get; private set; } = 0.5f;
        public string NoteText => _note.text;
        public int Repaints { get; private set; }

        public NodesBurnView()
        {
            AddToClassList("ssa-nodes-burn");
            name = "nodes-burn";
            pickingMode = PickingMode.Ignore;
            _note = Ui.Label("", "ssa-nodes-burn__note");
            _note.pickingMode = PickingMode.Ignore;
            Add(_note);
            generateVisualContent += Draw;
            Set(false, float.NaN, Vector2.zero, 0, MaxAngle);
        }

        /// <summary>Updates the view; true when it changed enough to be repainted.</summary>
        public bool Set(bool hasBurn, float angleDeg, Vector2 direction, float pitchDeg, float maxAngle)
        {
            bool changed = hasBurn != HasBurn
                || (hasBurn && (Moved(angleDeg, AngleDeg, 0.05f) || (direction - Direction).sqrMagnitude > 1e-4f))
                || Moved(pitchDeg, PitchDeg, 0.2f)
                || !Mathf.Approximately(maxAngle, MaxAngle)
                || Repaints == 0;
            if (!changed) return false;
            HasBurn = hasBurn;
            AngleDeg = angleDeg;
            Direction = direction;
            PitchDeg = float.IsNaN(pitchDeg) ? 0 : pitchDeg;
            MaxAngle = maxAngle;
            SetNote(NodesLogic.AlignNote(hasBurn, angleDeg, maxAngle, out var kind), kind);
            Repaints++;
            MarkDirtyRepaint();
            return true;
        }

        static bool Moved(float a, float b, float eps) =>
            float.IsNaN(a) != float.IsNaN(b) || (!float.IsNaN(a) && Mathf.Abs(a - b) > eps);

        void SetNote(string text, StatusKind kind)
        {
            _note.text = text;
            _note.RemoveFromClassList("ssa-nodes-burn__note--" + _noteKind.ToString().ToLowerInvariant());
            _noteKind = kind;
            _note.AddToClassList("ssa-nodes-burn__note--" + kind.ToString().ToLowerInvariant());
        }

        static Color A(Color c, float a) => new Color(c.r, c.g, c.b, a);

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 20 || r.height < 40) return;
            const float noteSpace = 26;
            float radius = Mathf.Max(10, Mathf.Min(r.width, r.height - noteSpace) * 0.46f);
            float k = radius / MockupRadius;
            var c = new Vector2(r.x + r.width / 2, r.y + (r.height - noteSpace) / 2 + 4);
            var p = ctx.painter2D;

            // Sky: the ball itself.
            p.fillColor = SsaPalette.Hex(0x0c1a4a, 0.85f);
            p.BeginPath(); p.Arc(c, radius, 0, 360); p.Fill();

            // Ground, below the horizon.
            float off = NodesLogic.HorizonOffset(PitchDeg, radius);
            Vector2? arc = NodesLogic.GroundArc(off, radius);
            p.fillColor = SsaPalette.Hex(0x2e2420, 1);
            if (arc.HasValue)
            {
                p.BeginPath();
                if (arc.Value.y - arc.Value.x >= 359.9f)
                    p.Arc(c, radius, 0, 360);
                else
                {
                    p.MoveTo(c + Polar(radius, arc.Value.x));
                    p.Arc(c, radius, arc.Value.x, arc.Value.y);
                    p.ClosePath();
                }
                p.Fill();
            }

            // Grid: the horizon ring and a meridian, as the mockup draws them.
            p.strokeColor = A(SsaPalette.Muted, 0.22f);
            p.lineWidth = 1.2f;
            Ellipse(p, c + new Vector2(0, off), radius, radius * 0.5f * Mathf.Cos(PitchDeg * Mathf.Deg2Rad));
            Ellipse(p, c, radius * 0.5f, radius);
            if (Mathf.Abs(off) < radius)
            {
                float half = Mathf.Sqrt(radius * radius - off * off);
                p.strokeColor = A(SsaPalette.Muted, 0.5f);
                p.BeginPath(); p.MoveTo(c + new Vector2(-half, off)); p.LineTo(c + new Vector2(half, off)); p.Stroke();
            }

            // Rim.
            p.strokeColor = A(SsaPalette.Node, 0.45f);
            p.lineWidth = 2 * k;
            p.BeginPath(); p.Arc(c, radius, 0, 360); p.Stroke();

            // The burn vector.
            if (HasBurn && !float.IsNaN(AngleDeg))
            {
                Vector2 m = c + NodesLogic.MarkerOffset(AngleDeg, Direction, radius);
                Color blue = NodesLogic.MarkerBehind(AngleDeg) ? A(SsaPalette.Node, 0.4f) : SsaPalette.Node;
                p.strokeColor = blue;
                p.lineWidth = 3.5f * k;
                p.lineCap = LineCap.Round;
                p.BeginPath(); p.Arc(m, 22 * k, 0, 360); p.Stroke();
                foreach (float deg in new[] { -90f, 150f, 30f })
                {
                    Vector2 d = Polar(1, deg);
                    p.BeginPath(); p.MoveTo(m + d * 22 * k); p.LineTo(m + d * 33 * k); p.Stroke();
                }
                p.fillColor = blue;
                p.BeginPath(); p.Arc(m, 8 * k, 0, 360); p.Fill();
            }

            // The vessel's nose, in the middle.
            p.strokeColor = SsaPalette.GoldLight;
            p.lineWidth = 3 * k;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;
            p.BeginPath();
            p.MoveTo(c + new Vector2(-22, 2) * k); p.LineTo(c + new Vector2(-9, 2) * k);
            p.LineTo(c + new Vector2(0, 11) * k); p.LineTo(c + new Vector2(9, 2) * k);
            p.LineTo(c + new Vector2(22, 2) * k);
            p.Stroke();
        }

        static Vector2 Polar(float r, float deg) =>
            new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)) * r;

        static void Ellipse(Painter2D p, Vector2 c, float rx, float ry)
        {
            if (rx < 0.5f || Mathf.Abs(ry) < 0.5f) return;
            p.BeginPath();
            for (int i = 0; i <= 48; i++)
            {
                float t = i / 48f * Mathf.PI * 2;
                var v = c + new Vector2(Mathf.Cos(t) * rx, Mathf.Sin(t) * ry);
                if (i == 0) p.MoveTo(v); else p.LineTo(v);
            }
            p.Stroke();
        }
    }
}
