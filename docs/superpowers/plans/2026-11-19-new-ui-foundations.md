# New UI foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Step 1 of the Design plan (`tasks/skyspaceagency/todo.md`, section « Design », validated 2026-10-30): a shared
style sheet, reusable UI Toolkit components, and the new wide, resizable window, opened as a preview beside the classic
window, with a demo that reproduces the Lift mockup from static data.

**Architecture:** Pure logic (scale, window geometry, tabs, phase timeline, pill kinds) in small static/value types,
tested in EditMode. Components are C# `VisualElement` subclasses that only add USS classes and children; every colour,
size and font is in one style sheet `SsaApp.uss`. The window is created like the classic one (`Window.Create` with an
Addressables UXML), but that UXML only carries the style sheet and an empty host: the frame is built in code.

**Tech Stack:** Unity 6000.6.0f1 UI Toolkit, Redux SDK 26w42c (UITK for KSP2 in `UitkForKsp2.dll`), NUnit EditMode.

**Spec:** `docs/design/ui/` (mockups `lift.svg`, `settings.svg`, `make_screens.py` for exact sizes and colours) and the
« Design » section of `D:/KSPReduxModding/tasks/skyspaceagency/todo.md`.

## Global Constraints

- Dark theme only. Palette: night `#0A1230`, card `#0E1840`, line `#1F2F66`, chip `#17245A`, active `#1D2C66`, text
  `#F4F7FF`, muted `#8EA3D6`, dim `#5C6EA6`, gold `#E8A93A` (gradient `#FFE29A`→`#D08A20`, light `#FFE29A`) for actions,
  cyan `#7FE7FF` for data, prograde `#D6F53C` for OK, coral `#FF7A5A` for alerts, blue `#4FA8FF` for nodes.
- Bahnschrift-like font: the theme's Noto Sans (Regular, SemiBold), shipped with the Redux SDK. Never the K2-D2 look.
- Layout of the mockups: header (logo, title, slogan, chips, minimise/close), horizontal tabs (SETTINGS on the right),
  three columns (plan / view / telemetry and safety), horizontal phase timeline, action bar (« Next: … », Pause/Abort,
  gold main button).
- Sizes: mockup pixels × 0.85 (the mockup is drawn for a 1600×900 window; 100 % gives a 1360×765 one).
- No mod-name literal outside `Code/ModIdentity.cs` (test `IdentityLiterals_OnlyInModIdentity`); slogan included.
- The classic window and Alt+Y are unchanged; the player who does not click « New UI (preview) » sees one new button.
- CC BY-SA 4.0, credits kept, CHANGELOG entry, French in-game checklist.
- One Unity job at a time.

## The two open questions

**Resizing.** `UitkForKsp2.API.Extensions.MakeResizable(element, ResizeOptions)` exists in 26w42c and 26w46a (IL of
`UitkForKsp2.dll`, both read 2026-11-19); `WindowOptions.ResizeOptions` and Redux's `WindowResizeHandle` both end on the
same `ResizeManipulator`, which writes `style.width/height` in panel pixels from `worldBound`. We call `MakeResizable` on
our frame ourselves (min 760×480, screen bounds on): typed, no reflection, an explicit target, and it is the very
manipulator the classic window already uses through `WindowResizeHandle`. The size and position are saved by our code
(`AppWindowGeometry`, keys `app_window_*`), re-clamped when the panel or the scale changes.

**Interface scale.** Each `Window.Create` window owns a cloned `PanelSettings` (`PanelFactory.CreateForWindow`), whose
`scale` UITK for KSP2 sets to the game UI scale (`UseStockScale`), and rewrites on every resolution or UI-scale change
(`ConfigureAllPanelSettings`). Our scale multiplies it: `panel.scale = UitkForKsp2Plugin.PanelSettings.scale × ours`,
checked every frame and re-applied after UITK's rewrite. Scaling the panel (not a `style.scale` transform) keeps
pointer, drag, resize and screen bounds in one unit system: the resize manipulator reads `worldBound`, which a transform
would have scaled. The classic window keeps the stock scale only.

**Loading.** A new Addressables entry `Assets/UI/SkySpaceAgency_UI/App/SkySpaceAgency_App.uxml` (the address is the path,
as for the classic window) holding the style sheet and one empty host; `Window.Create(options, VisualTreeAsset)` and
`AssetsLoader.LoadUxml` are the path already verified in game for the classic window (2026-10-20). The content is built
in code like the VAB window: components are reusable from C#, and the harness instantiates them without the game. The
entry is added by an editor method (`SsaAddressables.AddAppEntries`, own asmdef), never by editing the group YAML.

## Review Focus

- Interface scale changed while the window is open: the window must stay on screen (re-clamped), not keep a size bigger
  than the panel. Test `Geometry_ClampsToSmallerPanel`.
- Saved geometry from a bigger screen or garbage (negative, NaN): falls back to the default, never off screen. Test
  `Geometry_RejectsInvalidSaved`.
- Narrow window (minimum width): the three columns must not overlap. Test `Demo_MinimumSize_HasNoLayoutIssue`.
- A timeline with one step or a current index out of range: no exception, clamped. Test `Timeline_ClampsCurrent`.
- Tab selection saved for a tab that no longer exists: first tab. Test `Tabs_UnknownSavedTab_FallsBackToFirst`.

---

### Task 1: Pure logic — scale, geometry, tabs, timeline, pills

**Files:**
- Create: `Assets/SkySpaceAgency/Code/AppUI/UiScale.cs`, `AppWindowGeometry.cs`, `TabModel.cs`, `PhaseTimelineModel.cs`, `StatusKind.cs`
- Test: `Assets/Tests/UI/AppUiLogicTests.cs`

**Interfaces (namespace `SkySpaceAgency.AppUI`):**
- `static class UiScale`: `Min = 0.75f`, `Max = 1.5f`, `Step = 0.05f`, `Default = 1f`;
  `float Clamp(float user)` (NaN/≤0 → Default, snap to Step); `float Combine(float stock, float user)`;
  `bool NeedsApply(float current, float desired)` (|Δ| > 1e-4); `string Percent(float user)` → `"110 %"`.
- `struct AppWindowGeometry`: `DefaultSize = (1360, 765)`, `MinSize = (760, 480)`, `DefaultPosition = (60, 60)`,
  `NarrowWidth = 1100`; `static Rect Restore(Vector4 saved, Vector2 panel)` (saved x,y,w,h; invalid → default);
  `static Rect Clamp(Rect r, Vector2 panel)`; `static bool IsNarrow(float width)`; `static bool ShouldSave(Rect saved, Rect now)`.
- `class TabModel`: ctor `(IEnumerable<string> codes)`; `string Current`; `bool Select(string code)`; `void SetEnabled(string, bool)`;
  `string Restore(string saved)` (unknown/disabled → first enabled).
- `class PhaseTimelineModel`: ctor `(IReadOnlyList<string> steps, int current)`; `PhaseState StateOf(int i)`
  (`Done`, `Current`, `Pending`); `float Progress` = current/(n−1), 0 for n ≤ 1; current clamped to [0, n−1].
- `enum StatusKind { Ok, Alert, Info, Neutral }`; `static string StatusKinds.ClassFor(StatusKind)` → `ssa-pill--ok|alert|info|neutral`.

- [ ] Step 1: tests `Scale_ClampsAndSnaps` (0.5→0.75, 1.13→1.15, NaN→1), `Scale_Combine` (1.2×1.1=1.32),
  `Scale_Percent` ("110 %"), `Geometry_RejectsInvalidSaved`, `Geometry_ClampsToSmallerPanel`, `Geometry_IsNarrow`,
  `Tabs_UnknownSavedTab_FallsBackToFirst`, `Tabs_DisabledTabNotSelectable`, `Timeline_States`, `Timeline_ClampsCurrent`,
  `Pill_Classes`.
- [ ] Step 2: run, expect compile failure. Step 3: implement. Step 4: run the UI tests, expect PASS. Step 5: commit
  `Add the new UI's pure logic: scale, window geometry, tabs, phase timeline`.

### Task 2: Style sheet and components

**Files:**
- Create: `Assets/UI/SkySpaceAgency_UI/App/SsaApp.uss`, `Assets/SkySpaceAgency/Code/AppUI/SsaPalette.cs` (colour
  constants and runtime gradient textures), `AppControls.cs` (`AppCard`, `AppTile`, `AppSwitch`, `AppField`,
  `AppSlider`, `AppSegmented`, `AppStageBar`, `AppPill`, `AppChip`, `AppButton`), `AppHeader.cs`, `AppTabBar.cs`,
  `AppTimeline.cs`, `AppActionBar.cs`.
- Test: `Assets/Tests/UI/AppControlsTests.cs`

**Interfaces:** every control takes its text in its constructor and exposes a setter for its live value
(`AppTile.SetValue(string value, string unit = null)`, `AppSwitch.value` (`BaseField<bool>`-like, event `changed`),
`AppSlider.Value` with `Fraction`, `AppSegmented.Selected` + `changed`, `AppPill.Set(StatusKind, string)`,
`AppTimeline.SetSteps(IReadOnlyList<string>, int current)`, `AppTabBar.AddTab(code, label)`, `AppTabBar.Select(code)` +
`selected` event, `AppActionBar.SetNote(string)`, `AppActionBar.AddButton(string text, AppButton.Kind kind, Action)`).
USS class prefix `ssa-`. Variables on `.ssa-app` (not `:root`, so nothing leaks into the classic window).
Gradients (window, sky, gold) are 1×64 or 64×64 textures built once in `SsaPalette`.

- [ ] Steps: tests `Switch_TogglesOnClick`, `Segmented_SelectsAndRaises`, `Slider_FillFollowsValue`, `Pill_KindClass`,
  `Timeline_DotClasses`, `TabBar_SelectRaisesAndMarksActive`, `ActionBar_ButtonKinds`; implement; run; commit
  `Add the new UI's style sheet and components`.

### Task 3: The window, the Lift demo and the opening path

**Files:**
- Create: `Assets/UI/SkySpaceAgency_UI/App/SkySpaceAgency_App.uxml`, `Code/AppUI/AppWindowView.cs` (`AppPage`: columns,
  timeline, actions; `AppWindowView`: header, tabs, pages, narrow mode, minimise), `Code/AppUI/AscentProfileView.cs`
  (Painter2D), `Code/AppUI/AppDemo.cs` (Lift demo, placeholder tabs, Settings), `Code/AppUI/AppWindow.cs` (`AppSettings`:
  keys `app_ui_scale`, `app_main_page`, `app_window_pos`, `app_window_width`, `app_window_height`; `PanelScale`;
  `AppWindow` MonoBehaviour: open/close, scale, re-clamp), `Assets/Editor/SsaTools/SsaAddressables.cs` + asmdef.
- Modify: `ModIdentity.cs` (`AppWindowId`, `AppWindowUxml`, `Slogan = "Fly further"`), `SkySpaceAgencyPlugin.cs`
  (`EnsureAppWindow`, scene gating), `SkySpaceAgency_Window.uxml` (button `new-ui-button`), `SkySpaceAgencyWindow.cs`,
  `DragManipulator.cs` (optional `CanStart` filter: drag from the header only).
- Test: `Assets/Tests/UI/AppWindowTests.cs`, harness `Assets/Tests/UI/AppUiHarness.cs` (`RenderAll` to PNG).

- [ ] Steps: tests `Demo_DefaultSize_HasNoLayoutIssue`, `Demo_MinimumSize_HasNoLayoutIssue` (narrow mode),
  `Demo_HasHeaderTabsAndLiftPage`, `Scale_AppliedToPanelSettings` (panel content shrinks by 1/scale),
  `ClassicWindow_HasNewUiButton`; implement; run all EditMode tests; add the Addressables entry in batch; commit
  `Add the new wide window as a preview, with the Lift demo`.

### Task 4: Captures, docs, build

- [ ] Render `AppUiHarness.RenderAll` (with graphics) into `docs/design/ui/foundations/` (lift at 1600×900 via scale
  1.176 to compare with `lift.png`, lift at default, minimum size, settings, components). Compare to the mockups and
  fix the gaps. Commit `Captures of the new UI foundations`.
- [ ] CHANGELOG, checklist section 26, todo/lessons, parent CLAUDE.md bullet; build `Deploy to Zip File`; final review
  by a sub-agent; fix; commit and push; parent pointer.
