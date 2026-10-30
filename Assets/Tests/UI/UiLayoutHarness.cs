using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using K2UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

// Lays out the mod window without the game, to catch layout bugs in EditMode tests and to render each
// tab to a PNG.
//
// The window is K2D2_Window.uxml, cloned into a runtime panel (a PanelSettings with a RenderTexture
// target) with the UITK for KSP2 theme of the SDK package (KerbalUI.tss, the same KerbalUI.uss the
// game ships). The panel is internal to Unity: PanelSettings.panel gives it, and its public
// ValidateLayout / Repaint / Render run the style, layout and render passes. Layout works under
// -nographics; the PNG pass needs a graphics device (run Unity without -nographics):
//   Unity.exe -batchmode -projectPath <project> -executeMethod UiLayoutHarness.RenderAll -uiShotsDir <dir> -quit
//
// Each tab is shown as the game shows it (only its page visible), with sample values close to
// the in-game screenshots. Elements that the pages build from C# at run time are not there.
public static class UiLayoutHarness
{
    public const string WindowPath = "Assets/UI/K2D2_UI/K2D2_Window.uxml";
    public const string ThemePath = "Packages/ksp2redux.sdk/Controls/Assets/Theme/KerbalUI.tss";

    // K2D2_Window.uxml: the AppShell is 350 px wide (ResizeManipulator never changes the width) and
    // 500 px high by default, resizable up to 900 px.
    public const float WindowWidth = 350;
    public const float WindowHeight = 500;
    public const float TallWindowHeight = 900;

    // A setup is a tab and the state its page is put in before the layout.
    public sealed class Scenario
    {
        public string Name;
        public string Tab;
        public Action<VisualElement> Setup;
        public override string ToString() => Name;
    }

    public static readonly Scenario[] Scenarios =
    {
        new Scenario { Name = "node", Tab = "node" },
        new Scenario { Name = "orbit", Tab = "orbit", Setup = SetupOrbit },
        new Scenario { Name = "transfer", Tab = "transfer", Setup = SetupTransfer },
        new Scenario { Name = "lift", Tab = "lift" },
        new Scenario { Name = "landing_atmo", Tab = "landing", Setup = p => SetupLanding(p, atmo: true) },
        new Scenario { Name = "landing_vac", Tab = "landing", Setup = p => SetupLanding(p, atmo: false) },
        new Scenario { Name = "rendezvous", Tab = "dock" },
        new Scenario { Name = "attitude", Tab = "attitude" },
        new Scenario { Name = "about", Tab = "about" },
        // The same tabs with every foldout open (Profile, ADVANCED, INFO, ...).
        new Scenario { Name = "node_open", Tab = "node", Setup = OpenFoldouts },
        new Scenario { Name = "orbit_open", Tab = "orbit", Setup = p => { SetupOrbit(p); OpenFoldouts(p); } },
        new Scenario { Name = "orbit_capture", Tab = "orbit", Setup = SetupOrbitCapture },
        new Scenario { Name = "transfer_open", Tab = "transfer", Setup = p => { SetupTransfer(p); OpenFoldouts(p); } },
        new Scenario { Name = "lift_open", Tab = "lift", Setup = OpenFoldouts },
        new Scenario { Name = "landing_atmo_open", Tab = "landing", Setup = p => { SetupLanding(p, atmo: true); OpenFoldouts(p); } },
        new Scenario { Name = "landing_vac_open", Tab = "landing", Setup = p => { SetupLanding(p, atmo: false); OpenFoldouts(p); } },
        new Scenario { Name = "rendezvous_open", Tab = "dock", Setup = OpenFoldouts },
        new Scenario { Name = "attitude_open", Tab = "attitude", Setup = OpenFoldouts },
    };

    static void OpenFoldouts(VisualElement page)
    {
        foreach (var f in page.Query<Foldout>().ToList())
            f.SetValueWithoutNotify(true);
    }

    // As 14.png: Ap and Pe ticked, Inclination unticked (OrbitUI disables its field).
    static void SetupOrbit(VisualElement page)
    {
        page.Q<K2Toggle>("ap_enabled").SetValueWithoutNotify(true);
        page.Q<K2Toggle>("pe_enabled").SetValueWithoutNotify(true);
        page.Q<FloatField>("ap_km").SetValueWithoutNotify(200);
        page.Q<FloatField>("pe_km").SetValueWithoutNotify(200);
        page.Q<FloatField>("inc_deg").SetValueWithoutNotify(0.43f);
        page.Q<FloatField>("inc_deg").SetEnabled(false);
    }

    // The plan summary of a capture from a flyby (OrbitUI.Summary), with its capture line.
    static void SetupOrbitCapture(VisualElement page)
    {
        SetupOrbit(page);
        var capture = new K2D2.OrbitPlanning.CaptureInfo { AtPeriapsis = true, Radius = 370000, DeltaV = 254 };
        page.Q<Label>("summary").text =
            string.Join("\n",
                K2D2.OrbitPlanning.OrbitUI.CaptureLine(capture, "Mun", 200000),
                "1. T+00:42:10  Capture at periapsis (170.0 km): 254.0 m/s",
                "2. T+01:20:05  Set Pe 100.0 km at apoapsis: 61.3 m/s",
                "Total Δv: 315.3 m/s",
                "Final orbit: Ap 500.0 km, Pe 100.0 km, inclination 12.00°",
                "Step 1/2: node created for 'Capture at periapsis (170.0 km)'. Start in the Node tab flies the whole plan.");
    }

    static void SetupTransfer(VisualElement page)
    {
        var body = page.Q<DropdownField>("target_body");
        body.choices = new List<string> { "Duna" };
        body.SetValueWithoutNotify("Duna");
        page.Q<FloatField>("inc_deg").SetEnabled(false);
    }

    // As 16.png and 17.png: Propulsive mode on an atmospheric body (the note is shown), one stage
    // (the stage choice is disabled), a custom target.
    static void SetupLanding(VisualElement page, bool atmo)
    {
        page.Q("landing_mode_group_atmo").Show(atmo);
        page.Q("landing_mode_group_vac").Show(!atmo);
        // As LandingUI.UpdateProfilePanels: the ADVANCED settings of the same profile.
        page.Q("atmo_settings_panel").Show(atmo);
        page.Q("vac_settings_panel").Show(!atmo);
        string suffix = atmo ? "_atmo" : "_vac";
        var stage = page.Q<DropdownField>("land_stage" + suffix);
        stage.choices = new List<string> { "Stage 1 (current): 0 m/s" };
        stage.SetValueWithoutNotify("Stage 1 (current): 0 m/s");
        stage.SetEnabled(false);
        var waypoint = page.Q<DropdownField>("waypoint_drop" + suffix);
        waypoint.choices = new List<string> { "KSC", "Custom (15.50, 155.60)" };
        waypoint.SetValueWithoutNotify("Custom (15.50, 155.60)");
        page.Q<FloatField>("target_latitude" + suffix).SetValueWithoutNotify(15.50471f);
        page.Q<FloatField>("target_longitude" + suffix).SetValueWithoutNotify(155.6024f);
        if (page.Q<Label>("collision_value") is Label collision)
            collision.text = "Detected";
    }

    // ---- The panel ----

    public sealed class Window : IDisposable
    {
        public PanelSettings Settings;
        public RenderTexture Texture;
        public object Panel;
        public VisualElement Root;
        public VisualElement Shell;
        public ScrollView Scroll;
        public VisualElement Page;

        public void Layout()
        {
            Invoke(Panel, "ValidateLayout");
            // TabbedPage builds its buttons on GeometryChangedEvent: a second pass lays them out.
            Invoke(Panel, "ValidateLayout");
        }

        public void Dispose()
        {
            if (Settings != null) Object.DestroyImmediate(Settings);
            if (Texture != null) { Texture.Release(); Object.DestroyImmediate(Texture); }
        }
    }

    static object Invoke(object target, string method)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (m == null)
            throw new MissingMethodException(target.GetType().FullName, method);
        return m.Invoke(target, null);
    }

    public static ThemeStyleSheet LoadTheme() => AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);

    // In the editor, PanelSettings.Reset (run by CreateInstance) asks the internal
    // GetOrCreateDefaultTheme hook for a theme, which writes "Assets/UI Toolkit/UnityThemes" into
    // the project. The hook is pointed at the SDK theme for the time of the call.
    static PanelSettings CreatePanelSettings()
    {
        var hook = typeof(PanelSettings).GetField("GetOrCreateDefaultTheme", BindingFlags.Static | BindingFlags.NonPublic);
        object saved = hook?.GetValue(null);
        try
        {
            hook?.SetValue(null, (Func<ThemeStyleSheet>)LoadTheme);
            return ScriptableObject.CreateInstance<PanelSettings>();
        }
        finally
        {
            hook?.SetValue(null, saved);
        }
    }

    public static Window Open(Scenario scenario, float height)
    {
        var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(WindowPath);
        if (asset == null)
            throw new FileNotFoundException(WindowPath);

        var w = new Window();
        int texW = Mathf.CeilToInt(WindowWidth + 40), texH = Mathf.CeilToInt(height + 60);
        w.Texture = new RenderTexture(texW, texH, 24, RenderTextureFormat.ARGB32) { name = "ui-harness" };
        w.Texture.Create();
        w.Settings = CreatePanelSettings();
        w.Settings.name = "ui-harness";
        w.Settings.themeStyleSheet = LoadTheme();
        w.Settings.scaleMode = PanelScaleMode.ConstantPixelSize;
        w.Settings.scale = 1;
        w.Settings.targetTexture = w.Texture;
        w.Settings.clearColor = true;
        w.Settings.colorClearValue = new Color(0.08f, 0.09f, 0.11f, 1);
        var panelProp = typeof(PanelSettings).GetProperty("panel", BindingFlags.Instance | BindingFlags.NonPublic);
        w.Panel = panelProp.GetValue(w.Settings);
        var visualTree = ((IPanel)w.Panel).visualTree;

        w.Root = asset.Instantiate();
        w.Root.style.position = Position.Absolute;
        w.Root.style.left = 0; w.Root.style.top = 0; w.Root.style.width = texW; w.Root.style.height = texH;
        visualTree.Add(w.Root);

        w.Shell = w.Root.Q("MainFrame");
        w.Shell.style.left = 10; w.Shell.style.top = 40;
        w.Shell.style.height = height;
        w.Scroll = w.Root.Q<ScrollView>("tab-scroll-view");

        // As TabbedPage.ShowContent: only the chosen tab's template instance is displayed.
        var content = w.Root.Q("Content");
        foreach (var child in content.Children())
            child.Show(child.name == scenario.Tab);
        var tab = content.Children().First(c => c.name == scenario.Tab);
        // As K2Page: the main page is shown, the settings page hidden.
        w.Page = tab.Q("page") ?? tab;
        tab.Q("settings")?.Show(false);
        w.Page.Show(true);
        scenario.Setup?.Invoke(w.Page);
        w.Layout();
        return w;
    }

    // ---- Checks ----

    const float Tol = 1f;      // rounding tolerance, px
    const float MinControl = 20;

    public static bool IsDisplayed(VisualElement e)
    {
        for (var v = e; v != null; v = v.hierarchy.parent)
            if (v.resolvedStyle.display == DisplayStyle.None)
                return false;
        return true;
    }

    public static string PathOf(VisualElement e)
    {
        var parts = new List<string>();
        for (var v = e; v != null && v.name != "Content"; v = v.hierarchy.parent)
            parts.Add(string.IsNullOrEmpty(v.name) ? v.GetType().Name : $"{v.GetType().Name}#{v.name}");
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    static string Describe(VisualElement e)
    {
        string text = e is TextElement t && !string.IsNullOrEmpty(t.text) ? $" \"{Short(t.text)}\"" : "";
        return PathOf(e) + text;
    }

    static string Short(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;

    static string R(Rect r) => $"({r.x:0.#}, {r.y:0.#}, {r.width:0.#}×{r.height:0.#})";

    // Element whose parent clips it on purpose or that is meant to stick out.
    static bool Exempt(VisualElement e)
    {
        for (var v = e; v != null; v = v.hierarchy.parent)
        {
            // The AppShell title bar: the dashes label is a long run of dashes cut by its container.
            if (v.name == "header") return true;
            // The inside of a text input (its scrolling text) and the knob of a K2Toggle, which
            // slides over the track's edges by design (K2Toggle.uss).
            if (v.ClassListContains("unity-text-element--inner-input-field-component")) return true;
            if (v.ClassListContains(K2Toggle.inputKnobUssClassName)) return true;
            if (v != e && v.ClassListContains("unity-base-text-field__input")) return true;
            // The ScrollView's scrollers and viewport machinery.
            if (v.ClassListContains("unity-scroller")) return true;
            // The dropdown arrow: KerbalUI.uss (the game's theme) pulls it 7 px right with a
            // negative margin, past the input's edge, on every dropdown of the game.
            if (v.ClassListContains("unity-base-popup-field__arrow")) return true;
            // K2Compass places its graduation labels itself, on zero-size anchors.
            if (v.ClassListContains("k2compas")) return true;
            // K2Slider's min/max labels are pulled 2 px up towards the track on purpose
            // (K2Slider.uss, ".k2-slider-main #min_max_bar Label").
            if (v.name == "min_max_bar") return true;
        }
        return false;
    }

    // Every layout problem in the displayed part of the window.
    public static List<string> Check(Window w)
    {
        var issues = new List<string>();
        var all = new List<VisualElement>();
        Collect(w.Page, all);

        foreach (var e in all)
        {
            if (Exempt(e)) continue;
            Rect r = e.layout;
            var parent = e.hierarchy.parent;
            bool absolute = e.resolvedStyle.position == Position.Absolute;

            // Outside the parent's rect (the ScrollView's content may run past its viewport).
            if (parent != null && !absolute && !parent.ClassListContains(ScrollView.viewportUssClassName))
            {
                Rect p = parent.layout;
                if (r.xMin < -Tol || r.yMin < -Tol || r.xMax > p.width + Tol || r.yMax > p.height + Tol)
                    issues.Add($"outside its parent: {Describe(e)} {R(r)} in {PathOf(parent)} {p.width:0.#}×{p.height:0.#}");
            }

            // A control squeezed below a usable size.
            if (IsControl(e) && r.width > 0 && r.width < MinControl - 0.01f)
                issues.Add($"control narrower than {MinControl} px: {Describe(e)} width {r.width:0.#}");

            // A Label whose text does not fit its rect.
            if (e is Label label && !string.IsNullOrEmpty(label.text) && label.visible && !AutoSized(label))
            {
                var pad = Padding(label);
                float innerW = r.width - pad.x, innerH = r.height - pad.y;
                bool wraps = label.resolvedStyle.whiteSpace == WhiteSpace.Normal || label.resolvedStyle.whiteSpace == WhiteSpace.PreWrap;
                if (wraps)
                {
                    Vector2 size = label.MeasureTextSize(label.text, Mathf.Max(innerW, 1), VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined);
                    if (size.y > innerH + Tol)
                        issues.Add($"text clipped (height {size.y:0.#} > {innerH:0.#}): {Describe(e)}");
                }
                else
                {
                    Vector2 size = label.MeasureTextSize(label.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                    if (size.x > innerW + Tol)
                        issues.Add($"text clipped (width {size.x:0.#} > {innerW:0.#}): {Describe(e)}");
                }
            }
        }

        // In-flow siblings whose rects overlap.
        foreach (var parent in all.Prepend(w.Page))
        {
            if (Exempt(parent)) continue;
            var kids = parent.hierarchy.Children()
                .Where(c => c.resolvedStyle.display != DisplayStyle.None && c.resolvedStyle.position != Position.Absolute)
                .ToList();
            for (int i = 0; i < kids.Count; i++)
                for (int j = i + 1; j < kids.Count; j++)
                {
                    Rect a = kids[i].layout, b = kids[j].layout;
                    float ox = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin);
                    float oy = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
                    if (ox > Tol && oy > Tol)
                        issues.Add($"siblings overlap: {Describe(kids[i])} {R(a)} and {Describe(kids[j])} {R(b)}");
                }
        }
        return issues;
    }

    // A label with "-unity-text-auto-size: best-fit" shrinks its text to fit (KerbalUI.uss does it
    // on OabButton labels). The resolved value is only on the internal computed style.
    static readonly PropertyInfo ComputedStyleProp = typeof(VisualElement).GetProperty("computedStyle", BindingFlags.Instance | BindingFlags.NonPublic);

    static bool AutoSized(TextElement t)
    {
        try
        {
            object computed = ComputedStyleProp?.GetValue(t);
            var member = computed?.GetType().GetProperty("unityTextAutoSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (member?.GetValue(computed) is TextAutoSize size)
                return size.mode == TextAutoSizeMode.BestFit;
        }
        catch (Exception) { } // computedStyle is a ref-returning property in some versions
        return t.ClassListContains("oab-button__label");
    }

    static Vector2 Padding(VisualElement e)
    {
        var s = e.resolvedStyle;
        return new Vector2(s.paddingLeft + s.paddingRight + s.borderLeftWidth + s.borderRightWidth,
                           s.paddingTop + s.paddingBottom + s.borderTopWidth + s.borderBottomWidth);
    }

    static bool IsControl(VisualElement e) =>
        e is Button
        || e.ClassListContains(K2Toggle.inputUssClassName)
        || e.ClassListContains("unity-toggle__input")
        || e.ClassListContains("unity-base-field__input");

    static void Collect(VisualElement e, List<VisualElement> into)
    {
        foreach (var c in e.hierarchy.Children())
        {
            if (c.resolvedStyle.display == DisplayStyle.None) continue;
            into.Add(c);
            Collect(c, into);
        }
    }

    // ---- PNG renders ----

    // Unity.exe -batchmode -projectPath <project> -executeMethod UiLayoutHarness.RenderAll -uiShotsDir <dir> -quit
    public static void RenderAll()
    {
        string dir = ArgValue("-uiShotsDir") ?? Path.Combine(Path.GetTempPath(), "ui-shots");
        Directory.CreateDirectory(dir);
        Debug.Log($"[UiLayoutHarness] graphics device {SystemInfo.graphicsDeviceType}, theme {(LoadTheme() != null ? ThemePath : "none")}");
        foreach (var s in Scenarios)
        {
            try
            {
                string file = Path.Combine(dir, s.Name + ".png");
                Render(s, file);
                Debug.Log($"[UiLayoutHarness] {s.Name}: wrote {file}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[UiLayoutHarness] {s.Name}: render failed: {e}");
            }
        }
    }

    // Renders the whole tab: the window is made tall enough for its content not to scroll.
    public static void Render(Scenario s, string file)
    {
        float height;
        using (var probe = Open(s, WindowHeight))
        {
            float overflow = probe.Scroll.contentContainer.layout.height - probe.Scroll.contentViewport.layout.height;
            height = WindowHeight + Mathf.Max(0, overflow) + 4;
            foreach (var issue in Check(probe))
                Debug.Log($"[UiLayoutHarness] {s.Name} @{WindowHeight}: {issue}");
        }
        using var w = Open(s, height);
        Invoke(w.Panel, "Repaint");
        Invoke(w.Panel, "Render");
        var previous = RenderTexture.active;
        RenderTexture.active = w.Texture;
        var tex = new Texture2D(w.Texture.width, w.Texture.height, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, w.Texture.width, w.Texture.height), 0, 0);
        tex.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(file, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }

    static string ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
