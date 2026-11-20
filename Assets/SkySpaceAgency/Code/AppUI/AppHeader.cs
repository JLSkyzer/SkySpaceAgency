using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>The badge of the header (docs/design/branding): a night disc in a gold ring, the white rocket, a gold swoosh.</summary>
    public class AppLogo : VisualElement
    {
        public AppLogo()
        {
            AddToClassList("ssa-logo");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width <= 0) return;
            // Mockup units: a 48 px badge centred at (62, 62); k maps them to this element.
            float k = Mathf.Min(r.width, r.height) / 52f;
            Vector2 c = r.center;
            Vector2 P(float x, float y) => c + new Vector2(x, y) * k;
            var p = ctx.painter2D;

            p.fillColor = SsaPalette.Night;
            p.BeginPath();
            p.Arc(c, 24 * k, 0, 360);
            p.Fill();
            p.strokeColor = SsaPalette.Gold;
            p.lineWidth = 4 * k;
            p.BeginPath();
            p.Arc(c, 24 * k, 0, 360);
            p.Stroke();

            p.fillColor = SsaPalette.Text;
            p.BeginPath();
            p.MoveTo(P(-4, 14));
            p.LineTo(P(-4, -8));
            p.QuadraticCurveTo(P(0, -20), P(4, -8));
            p.LineTo(P(4, 14));
            p.ClosePath();
            p.Fill();

            p.strokeColor = SsaPalette.Gold;
            p.lineWidth = 3 * k;
            p.lineCap = LineCap.Round;
            p.BeginPath();
            p.MoveTo(P(-16, 8));
            p.QuadraticCurveTo(P(2, 16), P(18, -2));
            p.Stroke();
        }
    }

    /// <summary>A small square button of the header with a drawn glyph (minimise or close).</summary>
    public class AppIconButton : VisualElement
    {
        public enum Glyph { Minimise, Close }
        readonly Glyph _glyph;
        public event Action clicked;

        public AppIconButton(Glyph glyph, Action onClick = null)
        {
            _glyph = glyph;
            AddToClassList("ssa-icon-button");
            name = glyph == Glyph.Close ? "close-button" : "minimise-button";
            if (onClick != null) clicked += onClick;
            this.AddManipulator(new Clickable(Click));
            generateVisualContent += Draw;
        }

        /// <summary>What a click does; also the way tests press the button.</summary>
        public void Click() => clicked?.Invoke();

        void Draw(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width <= 0) return;
            float k = r.width / 28f;   // mockup: a 28 px button
            var p = ctx.painter2D;
            p.strokeColor = SsaPalette.Muted;
            p.lineWidth = 2.5f * k;
            p.lineCap = LineCap.Round;
            p.BeginPath();
            if (_glyph == Glyph.Minimise)
            {
                p.MoveTo(new Vector2(7, 14) * k);
                p.LineTo(new Vector2(21, 14) * k);
            }
            else
            {
                p.MoveTo(new Vector2(7, 7) * k);
                p.LineTo(new Vector2(21, 21) * k);
                p.MoveTo(new Vector2(21, 7) * k);
                p.LineTo(new Vector2(7, 21) * k);
            }
            p.Stroke();
        }
    }

    /// <summary>
    /// The window header: badge, title and slogan (from ModIdentity), the chips (vessel, body and
    /// situation, Δv...), and the minimise and close buttons. The whole header is the drag handle.
    /// </summary>
    public class AppHeader : VisualElement
    {
        public const string UssClass = "ssa-header";

        public VisualElement Chips { get; }
        public AppIconButton MinimiseButton { get; }
        public AppIconButton CloseButton { get; }

        public AppHeader(string title, string slogan)
        {
            AddToClassList(UssClass);
            name = "app-header";
            Add(new AppLogo());
            var brand = Ui.Box("ssa-brand");
            brand.Add(Ui.Label(title?.ToUpperInvariant() ?? "", "ssa-brand__title"));
            brand.Add(Ui.Label(slogan?.ToUpperInvariant() ?? "", "ssa-brand__slogan"));
            Add(brand);
            Chips = Ui.Box("ssa-chips");
            Add(Chips);
            MinimiseButton = new AppIconButton(AppIconButton.Glyph.Minimise);
            CloseButton = new AppIconButton(AppIconButton.Glyph.Close);
            Add(MinimiseButton);
            Add(CloseButton);
        }

        public AppChip AddChip(string label, string value, StatusKind? dot = null)
        {
            var chip = new AppChip(label, value, dot);
            // A narrow window keeps the first chip (the vessel) only.
            if (Chips.childCount > 0) chip.AddToClassList("ssa-chip--extra");
            Chips.Add(chip);
            return chip;
        }
    }
}
