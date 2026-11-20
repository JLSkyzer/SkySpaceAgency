# SETTINGS, RETURN and the compact flight bar — inventory

Plan `docs/superpowers/plans/2026-11-19-new-ui-tab-migration.md`. Mockups `docs/design/ui/settings.png`,
`return.png`, `hud.png`. Code `Assets/SkySpaceAgency/Code/AppUI/Tabs/SettingsTab.cs` (+ `SettingsText.cs`),
`ReturnTab.cs` (+ `ReturnProfileView.cs`), `AppHudView.cs`; style `Assets/UI/SkySpaceAgency_UI/App/Tabs/Settings.uss`;
tests `Assets/Tests/UI/Tabs/SettingsTabTests.cs`.

SETTINGS lays out the mockup: categories on the left (General, Interface, Staging, Warp, Logs, About), the chosen
category's card in the middle, the About card on the right, "Reset" and "Done" at the bottom.

## Classic About tab (`Code/UI/AboutUI.cs`, `about.uxml`)

- [x] Title label "`ModIdentity.AboutTitle`" (based on K2-D2, the droid astropilot) → Settings > About (first line)
- [x] Logo (`k2d2_big_icon.png`) → About card, the new badge (`AppLogo`): the new branding replaces the K2-D2 icon
- [x] Version label (`SWMetadata.SWInfo.Version`) → About card, under the name ("version x.y.z"; in the editor
  "development build · editor")
- [x] Thanks text (KSP2 Modding Society, @Munix, @schlosrat, @DunaColonist, @Cheese), word for word → Settings > About,
  THANKS (`SettingsText.Thanks`)
- [x] Button "Reset All Settings" (main file + `land_atmo` + `land_vac`) → action bar "Reset", same calls
  (`SettingsTab.ResetAll`); now asks for a second click within 4 s ("Reset all?")
- [x] Toggle "Debug Mode" (`debug_mode`) → Settings > Logs, "Debug mode"
- [x] The About tab reached by the title bar's "i" button (toggle back to the previous tab) → the SETTINGS tab and its
  About category; "Done" goes back to the tab shown before Settings
- [x] Vestigial empty "settings" element → nothing (it held nothing)

## Classic window title bar (`Code/UI/SkySpaceAgencyWindow.cs`, `SkySpaceAgency_Window.uxml`)

- [x] Toggle "Auto-staging" (`staging.auto_staging`) → Settings > Staging, "Auto staging" (LIFT binds it too)
- [x] Button "i" (About) → SETTINGS tab (see above)
- [x] Button "New UI" (`SwitchUi(false)`) → stays in the classic window; its counterpart here is Settings > Interface
  > "Classic UI"
- [x] Close button → the new window's own close button (header, not this tab)
- [ ] Window position / size keys (`main_window_pos`, `main_window_size`, `main_window_width`) and the classic tab
  key (`main_page`) → no control: window state, not settings (the new window has its own `app_window_*`, `app_main_page`)

## Global settings classes

- [x] `SkySpaceAgencySettings.debug_mode` → Settings > Logs
- [ ] `SkySpaceAgencySettings.current_tab` (`current_tab`) → no control: read by nothing, written by nothing
  (`TabbedPage` keeps its own field)
- [ ] `SkySpaceAgencySettings.settings_visible`, `GlobalSetting.settings_visible` (empty keys, never saved) → no
  control: the old settings gear, gone since the "i" button
- [x] `StagingSettings.auto_staging` → Settings > Staging
- [x] `StagingSettings.freeze_duration` (`staging.freeze_duration`, game seconds) → Settings > Staging, "Freeze the
  pilots after staging" (had no control in the classic window; persisted key, so it gets one)
- [x] `BurnWarpSettings.mode` (`burn.warp_mode`: Off (1x) / Physics (x4) / Rails (native)) → Settings > Warp, "Warp
  during burns", with the current mode described under it (`BurnWarpModes.Describe`); also in NODES > ADVANCED
- [ ] `DroneSettings` (`drone.show_details`, `drone.wanted_speed`, `drone.wanted_altitude`, `drone.speed_limit`,
  `speed.kill_h_speed_ratio`, `speed.altitude_ratio`) → no control: the Drone pilot is not added
  (`SkySpaceAgencyPlugin`: `// pilots_manager.AddPilot(new DronePilot())`) and no classic page binds them
- [x] Shortcut (`ModIdentity.DefaultWindowShortcut`, Space Warp config section Keybinds "Toggle window") → Settings >
  General, "Open the window" (shown as "Alt + Y"; the note says it is rebound in Redux's mod settings)

## The new window's own settings (`AppUI/AppWindow.cs`, `AppSettings`)

- [x] `app_ui_scale` (75–150 %, step 5, `UiScale`) → Settings > Interface, "Interface scale" (through
  `Context.SetScale`; the setting alone in the editor)
- [x] `app_classic_ui` → Settings > Interface, "Classic UI" (in game `Plugin.SwitchUi`, which swaps the windows; in
  the editor the setting only)
- [x] `app_compact_bar` → Settings > General, "Compact flight bar while a pilot runs"
- [ ] `app_main_page`, `app_window_pos`, `app_window_height`, `app_window_width` → no control: window state

## RETURN (planned page, `ReturnTab`)

No Return pilot exists yet (roadmap `docs/superpowers/roadmap-booster-return-video-scripting.md`): no settings, no
classic tab. The page is the mockup's layout, every control disabled.

- [x] Tab badge "Planned" → `ReturnTab.Badge` (for the coordinator's tab bar) and, on the page, an info pill
  "Planned: no Return pilot yet, nothing here flies" at the top of BOOSTER
- [x] BOOSTER: stage box, recovery computer box, Return point box, "Pick on map", "Coordinates", switches "Start Return on
  taking control", "Record the booster", "Ditch at sea if short" → all disabled, generic texts (mockup values in
  `ShowSample`)
- [x] RETURN PROFILE view → `ReturnProfileView`, the mockup's drawing (ascent, separation, keep-out cone, flip +
  boostback, dashed way back, entry and landing burns, pad); an illustration, not live data
- [x] RECOVERY BUDGET tiles (Δv to keep, Separate at, Boostback, Landing) → "—" (mockup values in `ShowSample`)
- [x] CLEARANCE AT SEPARATION (three checks) → rows with neutral dots (OK dots in `ShowSample`)
- [x] Pill "Engines light only when all checks pass" → bottom of RECOVERY BUDGET
- [x] Timeline Separation → Landed → `ReturnTab.Phases`, dimmed
- [x] Action note "Planned: booster recovery, after the transfers and rendezvous work", Abort, Return → disabled

## Compact flight bar (`AppHudView`)

- [x] Expanded variant: badge, pilot (caps) and phase, phase dots (done / current / to come), separator, readouts,
  gauge with percentage, Stop (coral square), open the window → `Show(HudState)`; hidden on null
- [x] Alert variant (`HudState.Alert`): coral border, dark red bar, coral dot, "<pilot> · <alert>", detail on the
  right → class `ssa-hud--alert`
- [x] Public API kept: `event openWindow`, `Show(HudState)`

## Choices not covered by the mockup

- Mockup rows with no setting behind them are left out (no new keys): Language, Units, Accent colour, "Explain each
  step before it flies", category "Recording" (video recording is roadmap R5), category "Pilots" (the pilots' settings
  live in their tabs).
- Categories Interface, Staging, Warp, Logs, About: General's layout (label left, control right, a muted note under).
- The shortcut is shown in a read-only field box (the mockup draws a drop-down): it can only be rebound in Redux's
  mod settings, and the value shown is the default (`DefaultWindowShortcut`), not a rebound one (the plugin keeps
  its configured value private).
- "Reset" asks for a second click within 4 s; the classic Reset All did not ask.
- "Done" returns to the tab shown before Settings (else the first tab): the context has no "close the window" hook.
- About card: "All credits: About" link; the side card keeps the mockup's two credit lines, the About category has
  the full list (README, NOTICE: Floutier, IanMealworm, LeoMarinDev, Lexi / KSP2Community, Mole, schlosrat, Opus,
  cheese3660, Halbann, KSP2Community) and the classic thanks.
- Side pill: "Landing settings are saved per profile (vacuum / atmosphere)" (the mockup's idea, worded on what exists).
- Compact bar: placed at the top centre of the screen (72 px down); the alert variant keeps Stop and open buttons (the
  mockup's alert bar shows none) so a guard never hides Stop; the "minimal" one-line variant is not built; Stop is always shown, disabled when the pilot gives no Stop.
- Return: an info pill at the top of BOOSTER says the page is planned; the recovery-computer box says "looked for at
  separation" instead of a found/not found state.

## Not verified in game

- `Plugin.SwitchUi(true)` from the Settings switch: the classic window opens, this one closes, never both.
- The version line (`SWMetadata.SWInfo.Version`) and the shortcut text ("Alt + Y" from `KeyboardShortcut.ToString()`,
  whose format was not read in the IL: falls back to "the default shortcut").
- Reset in game: every control follows (the settings' listeners), the panel scale goes back to 100 %.
- The compact flight bar: placement over the game's UI at each UI scale, its font, the alert colours, Stop and open
  buttons; it is not scaled by the interface scale unless the panel's scale applies to the host (it should: same panel).
- The Return drawing at other window sizes (labels are placed in fractions of the view).

## Addendum 2026-11-20
- [x] `app_small_window` (Small window: Scale / Reflow) -> SETTINGS > Interface, segmented choice + note.
