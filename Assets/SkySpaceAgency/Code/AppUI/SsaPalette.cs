using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The mockups' palette (docs/design/ui/make_screens.py) for code that draws (Painter2D), and the
    /// gradients USS cannot express, built once as small textures and used as background images (a
    /// background image follows the element's border radius).
    /// </summary>
    public static class SsaPalette
    {
        public static readonly Color Night = Hex(0x0a1230);
        public static readonly Color Card = Hex(0x0e1840);
        public static readonly Color Line = Hex(0x1f2f66);
        public static readonly Color Active = Hex(0x1d2c66);
        public static readonly Color Text = Hex(0xf4f7ff);
        public static readonly Color Muted = Hex(0x8ea3d6);
        public static readonly Color Dim = Hex(0x5c6ea6);
        public static readonly Color Gold = Hex(0xe8a93a);
        public static readonly Color GoldLight = Hex(0xffe29a);
        public static readonly Color GoldDark = Hex(0xd08a20);
        public static readonly Color Cyan = Hex(0x7fe7ff);
        public static readonly Color Ok = Hex(0xd6f53c);
        public static readonly Color Alert = Hex(0xff7a5a);
        public static readonly Color AlertLight = Hex(0xff9a7a);
        public static readonly Color Node = Hex(0x4fa8ff);
        public static readonly Color Ground = Hex(0x3f9b52);

        public static Color Hex(int rgb, float alpha = 1) =>
            new Color(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, alpha);

        static Texture2D _window, _sky, _gold;

        /// <summary>The window background: #111c44 at the top to #080e26 at the bottom.</summary>
        public static Texture2D WindowGradient => _window ??= Vertical(Hex(0x111c44), Hex(0x080e26));

        /// <summary>The view cards (orbit, profile): #0a1230 at the top to #13306a at the bottom.</summary>
        public static Texture2D SkyGradient => _sky ??= Vertical(Hex(0x0a1230), Hex(0x13306a));

        /// <summary>Actions: #ffe29a top left to #d08a20 bottom right.</summary>
        public static Texture2D GoldGradient => _gold ??= Diagonal(GoldLight, GoldDark);

        public static void SetBackground(VisualElement e, Texture2D gradient)
        {
            e.style.backgroundImage = new StyleBackground(gradient);
        }

        static Texture2D NewTexture(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "ssa-gradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        // Row 0 of a texture is its bottom.
        static Texture2D Vertical(Color top, Color bottom)
        {
            const int h = 64;
            var t = NewTexture(1, h);
            for (int y = 0; y < h; y++)
                t.SetPixel(0, y, Color.Lerp(bottom, top, y / (float)(h - 1)));
            t.Apply(false, false);
            return t;
        }

        static Texture2D Diagonal(Color topLeft, Color bottomRight)
        {
            const int n = 32;
            var t = NewTexture(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    t.SetPixel(x, y, Color.Lerp(topLeft, bottomRight, (x + (n - 1 - y)) / (2f * (n - 1))));
            t.Apply(false, false);
            return t;
        }
    }
}
