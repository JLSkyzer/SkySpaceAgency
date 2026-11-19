# New UI tab migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Step 2 of the Design plan, in one go (user decision 2026-11-19, tested in game at the end): every tab of
the classic window moves to the new window, wired to the real pilots and settings; the compact flight bar and a
"coming" Return page are added; the new window becomes the default, the classic one stays behind a "Classic UI"
setting until the user validates the new one in game.

**Architecture:** One `AppTab` subclass per tab (`Code/AppUI/Tabs/<Name>Tab.cs`), built from the foundations'
components (plan 2026-11-19-new-ui-foundations). Settings are bound through `AppBind` (two-way, every key recorded);
pilots are reached through `AppTabContext.Pilot<T>()` (null in the editor, so every tab builds and ticks without the
game). Pilot status keeps flowing through the controllers' `updateUI(root, FullStatus)`: `AppPilotStatus` hands
them a detached classic `FullStatus` and mirrors it into a pill, a console note and a progress bar. The classic
window stays built (pilots keep their `page`), only hidden.

**Tech Stack:** Unity 6000.6.0f1 UI Toolkit, Redux SDK 26w42c, NUnit EditMode. Compile check without Unity:
`python <scratchpad>/quickcompile.py` (Roslyn of the editor, Unity's last response file).

**Spec:** `docs/design/ui/*.png|svg` (lift, orbit, transfer, rendezvous, land, return, nodes, settings, hud), the
foundations plan, and the classic tabs themselves (`Assets/UI/SkySpaceAgency_UI/*.uxml`, `Code/Pilots/**/*UI.cs`,
the settings classes' `setupUI`), which define *what* must exist.

## Global Constraints

- Nothing functional is lost: every setting, button, info line, message, warning and drop-down of each classic tab
  has its place in the new tab (inventory `docs/superpowers/notes/new-ui-inventory/README.md`, one section per
  tab, each line ticked with where it went). Settings files, plans and VAB files are read unchanged: same keys,
  same `Setting` objects.
- The mockups decide the look; a case they do not cover takes the closest mockup and is listed in the inventory.
- Centre views draw real data (ascent profile, orbit, transfer trajectory, approach, landing) and a sample in the
  editor (`ShowSample`).
- Tabs not in the mockups: Attitude goes into NODES (a card "Hold attitude"), Dock into RENDEZVOUS, About into
  SETTINGS.
- Alt+Y and the app bar button open the new window; Settings > Interface > "Classic UI" (`app_classic_ui`) brings
  the classic one back; the classic window's "New UI" button switches back. The VAB window and the webcast overlay
  are out of scope.
- No identity literal outside `ModIdentity.cs`; CC BY-SA, credits kept; one Unity job at a time.

## Review Focus

- A tab ticking with no vessel, no node, no target or the pilot missing: nothing throws (AppWindow logs a tick
  error once and keeps going). Test `AllTabs_BuildAndTickWithoutTheGame`.
- A setting changed in the classic window or by Reset All shows in the new control. Test `Bind_FollowsTheSetting`.
- A persisted key without a control. Test `AppSettingsCoverageTests.EveryPersistedKey_HasAControl`.
- Every tab at its mockup size and at the minimum size: no overlap or clipped text. Tests `Tab_<code>_MockupSize`,
  `Tab_<code>_MinimumSize`.
- Switching Classic UI while a window is open opens the other one, never both.

---

### Task 1: Shared pieces (done by the coordinator before the tabs)

`AppTab`/`AppTabContext`/`HudState` (`AppTab.cs`), `AppBind`, `AppDropdown`/`AppTextField`/`AppSection`/
`AppInfoTable`/`AppNote`/`AppButtonRow` (`AppMoreControls.cs`), `AppPilotStatus`, `AppTabs` (order), tab stubs,
`AppWindowView.AddTab/Tick` (running tabs lit, class `ssa-tab--running`), `AppWindow` (tick of the shown tab, compact
bar host, toolbar button state, `AppSettings.ClassicUi`, `AppSettings.CompactBar`), plugin (`ToggleDefaultWindow`,
`SetDefaultWindowOpen`, `SwitchUi`, scene rule for the new window). Commit `Add the tab migration's shared pieces`.

### Tasks 2 to 7: one tab each (parallel, own files only)

Each: read the classic tab (uxml, `*UI.cs`, the settings' `setupUI`, the controllers' `updateUI`), write the
inventory section, write `Code/AppUI/Tabs/<Name>Tab.cs` (+ its view), `ShowSample`, `Assets/Tests/UI/Tabs/<Name>TabTests.cs`.
Compile with quickcompile. Owners: NODES (+ Attitude), ORBIT, LIFT, LAND, TRANSFER, RENDEZVOUS (+ Dock),
SETTINGS (+ About, all classic settings outside the tabs, Return page, compact flight bar view `AppHudView`).

### Task 8: Window, coverage, captures

Remove the demo (`AppDemo.cs`), harness shots per tab at mockup size (`docs/design/ui/migration/`), coverage test,
fix the slider track offset and the minimum-size scrolling, CHANGELOG, checklist section 27, todo, lessons, parent
CLAUDE.md, zip, final review.

### Task 9 (after the user's in-game validation, NOT now): remove the classic window

Delete: `Assets/UI/SkySpaceAgency_UI/{SkySpaceAgency_Window,node,Orbit,Transfer,Lift,Landing,Dock,attitude,about}.uxml`,
`SkySpaceAgency.uss`, `Assets/Runtime/SsaUI/USS/*.uss` once unused, `Code/UI/SkySpaceAgencyWindow.cs`,
`Code/UI/AboutUI.cs`, `Code/UI/FullStatus.cs` users' classic parts, the `*UI.cs` pages (`NodeExUI`, `OrbitUI`,
`TransferUI` page class, `LiftUI`, `LandingUI`, `RendezvousUI` page class, `AttitudeUI`), `SsaUI/Tabs/*`, the
classic-only SsaUI controls no new tab uses, `UiLayoutHarness`/`UiLayoutTests`, `WindowLayoutTests`, the classic
Addressables entry, `AppSettings.ClassicUi` and the "New UI" button; `Pilot.page` and the settings' `setupUI`
methods (classic bindings) go too, after moving any logic they hold (static settings classes declared inside
`TransferUI.cs` and `RendezvousUI.cs` move to their own files first).
