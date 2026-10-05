# SkySpaceAgency

**SkySpaceAgency is a remix of [K2-D2 (Redux port)](https://github.com/IanMealworm/K2D2Redux) by [@IanMealworm](https://github.com/IanMealworm)**, itself a port of [Christophe Floutier's original K2-D2](https://github.com/cfloutier/k2d2). All credit for the existing autopilots goes to them; this repository starts from IanMealworm's work at commit [`7ea025c`](https://github.com/IanMealworm/K2D2Redux/commit/7ea025c) (version 1.3.0, 2026-09-23), with its full history kept.

> In game the mod is named **SkySpaceAgency** (mod id `SkySpaceAgency`, `SkySpaceAgency.dll`). It is a separate mod from K2-D2: do not install both, they share the `Alt-O` shortcut and the same autopilots. Changes made in this remix are listed in [`CHANGELOG.md`](CHANGELOG.md).

---

## K2-D2 (Redux port)

An astromech-style autopilot suite for Kerbal Space Program 2, ported to the **Redux** modding framework from [Christophe Floutier's original K2-D2](https://github.com/cfloutier) (built for SpaceWarp1).

K2-D2 gives you one panel (`Alt-O` or the AppBar icon) with a set of autopilots:

- **Node** - executes the next maneuver node, with auto-circularize at Ap/Pe and one-click buttons to create a node at the next apoapsis or periapsis
- **Orbit** - set a target apoapsis, periapsis and/or inclination and create every maneuver node needed to get there in one click; execute them with Node
- **Lift** - automated ascent guidance with a configurable altitude/heading profile, an optional roll program, and automatic circularization at the end of the climb
- **Landing** - automated descent, braking, and touchdown, with an optional Precision Landing mode (currently bodies with no atmosphere only) that targets a specific site via Redux's waypoint system
- **Docking** - automated final approach and docking
- **Attitude** - point-and-hold attitude control (a simple plane autopilot)

## Status

This is a from-scratch port of the original code onto Redux's APIs, not a compatibility shim - it required chasing down and fixing a number of real differences between the old SpaceWarp1/UitkForKsp2 environment and Redux (custom UI controls, orbit representation, and more). The full blow-by-blow of everything found and fixed is in [`NOTICE.md`](NOTICE.md), kept as a development log for anyone porting a similar mod and hitting the same walls.

**Confirmed working (tested in-game):**
- Full UI - all tabs, styling, custom controls
- Node autopilot
- Lift/ascent autopilot - flies the configured profile to orbit, including an optional roll program, then creates and flies its own circularization node
- Landing autopilot - descent, braking, and touchdown, including collision detection and precision landing on bodies with no atmosphere
- Docking autopilot - final approach and main-thrust kill-speed/brake
- Attitude hold
- Auto-staging, with a player-facing on/off toggle in the window's title bar

**Known limitations:**
- Precision Landing on atmospheric bodies is still a work in progress and isn't exposed in the UI yet - only bodies with no atmosphere have a player-facing Precision Landing option for now.
- Landing's braking plan ignores atmospheric drag (which only helps) and assumes the active engines stay as they are: auto-staging during the landing burn is not planned for.

## Installation

1. Install [Redux](https://ksp2redux.org) for Kerbal Space Program 2.
2. Make sure you're on KSP2 beta snapshot 26w33a or newer (Redux Launcher: Settings [gear icon to the right of the "Mods" tab] > Release Channel > Beta, then update to the latest beta).
3. Download the latest SkySpaceAgency release and extract it into a `SkySpaceAgency` folder inside your KSP2 `mods` folder.

## Credits

- **[Christophe Floutier](https://github.com/cfloutier)** - original K2-D2 mod for SpaceWarp1
- **[@IanMealworm](https://github.com/IanMealworm)** - Redux port ([K2D2Redux](https://github.com/IanMealworm/K2D2Redux)), the base of this remix
- **[Mole](https://github.com/Mole1803)** - original Circularize work
- **[schlosrat](https://forum.kerbalspaceprogram.com/index.php?/profile/141963-schlosrat/)** - original testing and code help, especially node creation
- **Opus** - named the mod
- **[cheese3660](https://github.com/cheese3660)** - [SpaceWarp](https://github.com/Halbann) and [AutoBurn](https://github.com/cheese3660/AutoBurn), which the original mod was built on
- **[Halbann](https://github.com/Halbann)** - [LazyOrbit](https://github.com/Halbann/LazyOrbit), which the original mod's first steps were based on
- **[KSP2Community](https://github.com/KSP2Community)** - the Redux modding framework and the `Redux.Template` project scaffold this port is built on

## Changelog

See [`CHANGELOG.md`](CHANGELOG.md) for release notes.

## License

CC BY-SA 4.0 - see [`LICENSE.md`](LICENSE.md). Same license as the original K2-D2, inherited as required by its ShareAlike terms.
