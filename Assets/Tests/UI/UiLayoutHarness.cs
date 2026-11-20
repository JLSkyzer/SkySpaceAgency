using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

// Lays out a window of the mod without the game, to catch layout bugs in EditMode tests: a runtime panel
// (a PanelSettings with a RenderTexture target) with the UITK for KSP2 theme of the SDK package
// (KerbalUI.tss, the same KerbalUI.uss the game ships). The panel is internal to Unity: PanelSettings.panel
// gives it, and its public ValidateLayout / Repaint / Render run the style, layout and render passes.
// Layout works under -nographics; the PNG pass (AppUiHarness.RenderAll) needs a graphics device.
// The checks look for overlapping siblings, squeezed controls, clipped labels and elements outside their
// parent. (Before 2026-11-20 this file also opened the classic window's tabs; AppUiHarness opens the new one.)
public static class UiLayoutHarness
{
    public const string ThemePath = "Packages/ksp2redux.sdk/Controls/Assets/Theme/KerbalUI.tss";

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
            // The tab bar and the columns react to GeometryChangedEvent: further passes lay them out.
            Invoke(Panel, "ValidateLayout");
        }

        public void Dispose()
        {
            if (Settings != null) Object.DestroyImmediate(Settings);
            if (Texture != null) { Texture.Release(); Object.DestroyImmediate(Texture); }
        }
    }

    public static object Invoke(object target, string method)
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
    public static PanelSettings CreatePanelSettings()
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
            // The inside of a text input (its scrolling text).
            if (v.ClassListContains("unity-text-element--inner-input-field-component")) return true;
            if (v != e && v.ClassListContains("unity-base-text-field__input")) return true;
            // The ScrollView's scrollers and viewport machinery.
            if (v.ClassListContains("unity-scroller")) return true;
            // The dropdown arrow: KerbalUI.uss (the game's theme) pulls it 7 px right with a
            // negative margin, past the input's edge, on every dropdown of the game.
            if (v.ClassListContains("unity-base-popup-field__arrow")) return true;
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
}
