# New UI inventory (plan `../../plans/2026-11-19-new-ui-tab-migration.md`)

Every setting, button, info line, message, warning, drop-down and foldout of the classic window, ticked with
where it is in the new window. One file per tab:

| Classic tab | New tab | File | Ticked | Not moved (reason in the file) |
|---|---|---|---|---|
| Node, Att. | NODES (Attitude: card "Hold attitude") | [nodes.md](nodes.md) | 52 | avatar, tooltips (the kit has none), empty hidden settings pages, Flight Plan block (disabled in the classic tab too) |
| Orbit | ORBIT | [orbit.md](orbit.md) | 28 | — |
| Lift | LIFT | [lift.md](lift.md) | 47 | dead "Create Node" buttons (always hidden), the graph's grey pitch lines and pressure gradient, avatar; `lift.ascent_lead.<body>.*` are written by the pilot, not a control |
| Landing | LAND | [land.md](land.md) | 48 | `land.max_throttle_rate_per_sec` (no classic control either) |
| Transfer | TRANSFER | [transfer.md](transfer.md) | 36 | empty settings sub-page |
| Rendezvous (dock) | RENDEZVOUS | [rendezvous.md](rendezvous.md) | 31 | static title, empty settings panel |
| About, title-bar toggles, global settings | SETTINGS (+ RETURN page, compact flight bar) | [settings.md](settings.md) | 31 | window state keys, unused `current_tab`, unsaved `settings_visible`, DronePilot settings (the pilot is never added) |

The test `Assets/Tests/UI/AppSettingsCoverageTests.cs` checks that every persisted key found in the code outside
`Code/AppUI/` is bound by a new-UI control, except the keys listed in it with their reason (the same ones as above).
