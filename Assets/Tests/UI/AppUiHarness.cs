using System;
using System.Collections.Generic;
using System.IO;
using SkySpaceAgency;
using SkySpaceAgency.AppUI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

// Lays out the new window (AppUI) without the game, as UiLayoutHarness does for the classic one: the
// window's Addressables uxml (style sheet and host) cloned into a runtime panel with the SDK's KerbalUI
// theme, the frame built by AppWindow.BuildView. Renders PNGs for docs/design/ui/foundations:
//   Unity.exe -batchmode -projectPath <project> -executeMethod AppUiHarness.RenderAll -uiShotsDir <dir> -quit
public static class AppUiHarness
{
    public const string AppUxmlPath = ModIdentity.AppWindowAddress;

    // The mockups are 1600 x 900 drawings of a 1560 x 860 window at (20, 20); the USS is their pixels
    // times 0.85, so a panel scale of 1 / 0.85 renders the window at the mockups' size.
    public const float MockupScale = 1f / 0.85f;

    public sealed class Shot
    {
        public string Name;
        public string Tab;
        public Vector2 Texture;      // render size, in screen pixels
        public float Scale = 1;      // panel scale
        public Rect Window;          // in panel pixels
        public Action<AppWindowView> Setup;
        public bool Hud;             // render the compact flight bar of the tab instead of the window
        public override string ToString() => Name;
    }

    static Rect MockupWindow => new Rect(20 * 0.85f, 20 * 0.85f, 1560 * 0.85f, 860 * 0.85f);

    // Each tab at its mockup's size (docs/design/ui/<tab>.png; NODES is nodes.png), sample data, then a few sizes.
    public static readonly Shot[] Shots = BuildShots();

    static Shot[] BuildShots()
    {
        var list = new List<Shot>();
        foreach (string tab in new[] { "lift", "orbit", "transfer", "rendezvous", "land", "return", "nodes", "settings" })
            list.Add(new Shot { Name = tab + "_mockup_size", Tab = tab, Texture = new Vector2(1600, 900), Scale = MockupScale, Window = MockupWindow });
        foreach (string tab in new[] { "lift", "land", "rendezvous" })
            list.Add(new Shot { Name = tab + "_minimum_narrow", Tab = tab, Texture = new Vector2(800, 520), Window = new Rect(new Vector2(20, 20), AppWindowGeometry.MinSize) });
        list.Add(new Shot { Name = "lift_default_100pc", Tab = "lift", Texture = new Vector2(1400, 805), Window = new Rect(new Vector2(20, 20), AppWindowGeometry.DefaultSize) });
        list.Add(new Shot { Name = "lift_minimised", Tab = "lift", Texture = new Vector2(1400, 120), Window = new Rect(new Vector2(20, 20), AppWindowGeometry.DefaultSize), Setup = v => v.SetMinimised(true) });
        list.Add(new Shot { Name = "hud_land", Tab = "land", Texture = new Vector2(1600, 200), Scale = MockupScale, Window = new Rect(-4000, 0, 800, 480), Hud = true });
        return list.ToArray();
    }

    public sealed class Opened : IDisposable
    {
        public PanelSettings Settings;
        public RenderTexture Texture;
        public object Panel;
        public VisualElement Root;
        public AppWindowView View;
        public AppTabContext Context;
        public AppHudView HudView;

        public void Layout()
        {
            UiLayoutHarness.Invoke(Panel, "ValidateLayout");
            UiLayoutHarness.Invoke(Panel, "ValidateLayout");
            UiLayoutHarness.Invoke(Panel, "ValidateLayout");
        }

        public UiLayoutHarness.Window AsLayoutWindow() => new UiLayoutHarness.Window { Panel = Panel, Root = Root, Shell = View, Page = View };

        public void Dispose()
        {
            if (Settings != null) Object.DestroyImmediate(Settings);
            if (Texture != null) { Texture.Release(); Object.DestroyImmediate(Texture); }
        }
    }

    public static Opened Open(string tab, Rect window, Vector2 texture, float scale = 1)
    {
        var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AppUxmlPath);
        if (asset == null)
            throw new FileNotFoundException(AppUxmlPath);

        var o = new Opened();
        o.Texture = new RenderTexture((int)texture.x, (int)texture.y, 24, RenderTextureFormat.ARGB32) { name = "app-ui-harness" };
        o.Texture.Create();
        o.Settings = UiLayoutHarness.CreatePanelSettings();
        o.Settings.name = "app-ui-harness";
        o.Settings.themeStyleSheet = UiLayoutHarness.LoadTheme();
        o.Settings.scaleMode = PanelScaleMode.ConstantPixelSize;
        o.Settings.scale = 1;
        o.Settings.targetTexture = o.Texture;
        o.Settings.clearColor = true;
        o.Settings.colorClearValue = SsaPalette.Hex(0x050816);
        var panelProp = typeof(PanelSettings).GetProperty("panel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        o.Panel = panelProp.GetValue(o.Settings);
        PanelScale.Apply(o.Settings, scale);
        var visualTree = ((IPanel)o.Panel).visualTree;

        o.Root = asset.Instantiate();
        o.Root.style.position = Position.Absolute;
        o.Root.style.left = 0; o.Root.style.top = 0; o.Root.style.right = 0; o.Root.style.bottom = 0;
        o.Root.style.backgroundColor = SsaPalette.Hex(0x050816);   // the mockups' backdrop
        visualTree.Add(o.Root);
        var host = o.Root.Q("ssa-app-host");
        o.Context = AppTabContext.Editor();
        o.View = AppWindow.BuildView(host, o.Context);
        foreach (var t in o.View.TabList) t.ShowSample();
        o.View.HeaderData.Set("Kerbal Explorer II", true, "Kerbin", "Pad", 4812, 1.46);   // the mockups' chips
        o.View.style.left = window.x; o.View.style.top = window.y;
        o.View.style.width = window.width; o.View.style.height = window.height;
        o.View.Select(tab);
        o.Layout();
        return o;
    }

    public static Opened Open(Shot s)
    {
        var o = Open(s.Tab, s.Window, s.Texture, s.Scale);
        if (s.Setup != null) { s.Setup(o.View); o.Layout(); }
        if (s.Hud)
        {
            // The compact bar sits in the same host as the window (AppWindow), shown with the tab's sample state.
            o.HudView = new AppHudView();
            o.Root.Q("ssa-app-host").Add(o.HudView);
            o.HudView.Show(o.View.TabFor(s.Tab)?.Hud ?? SampleHud());
            o.Layout();
        }
        return o;
    }

    // The hud mockup's expanded bar, for tabs whose sample has no HudState.
    public static HudState SampleHud() => new HudState
    {
        Pilot = "LAND", Phase = "Pre-brake", Phases = new[] { "Deorbit", "Pre-brake", "Coast", "Brake", "Touchdown" }, CurrentPhase = 1,
        Readouts = { ("ALTITUDE", "54.8 km"), ("SPEED", "1 012 m/s"), ("TARGET", "0.4 km") }, GaugeLabel = "HOTTEST PART", Gauge = 0.22f,
    };

    public static List<string> Check(Opened o) => UiLayoutHarness.Check(o.AsLayoutWindow());

    public static void RenderAll()
    {
        string dir = ArgValue("-uiShotsDir") ?? Path.Combine(Path.GetTempPath(), "app-ui-shots");
        Directory.CreateDirectory(dir);
        Debug.Log($"[AppUiHarness] graphics device {SystemInfo.graphicsDeviceType}");
        foreach (var s in Shots)
        {
            try
            {
                using var o = Open(s);
                foreach (var issue in Check(o))
                    Debug.Log($"[AppUiHarness] {s.Name}: {issue}");
                UiLayoutHarness.Invoke(o.Panel, "Repaint");
                UiLayoutHarness.Invoke(o.Panel, "Render");
                var previous = RenderTexture.active;
                RenderTexture.active = o.Texture;
                var tex = new Texture2D(o.Texture.width, o.Texture.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, o.Texture.width, o.Texture.height), 0, 0);
                tex.Apply();
                RenderTexture.active = previous;
                string file = Path.Combine(dir, s.Name + ".png");
                File.WriteAllBytes(file, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Debug.Log($"[AppUiHarness] {s.Name}: wrote {file}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[AppUiHarness] {s.Name}: render failed: {e}");
            }
        }
    }

    static string ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
