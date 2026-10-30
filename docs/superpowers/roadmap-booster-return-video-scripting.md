# Roadmap: booster return, flight recording, scripting

Status: **not started**. Written on 2026-10-30 from the user's ideas, before any research. This is a
roadmap, not an implementation plan. Each sub-project below gets its own spec, then its own plan
(`specs/` and `plans/`), when its turn comes.

## When

This comes **after everything already open**:
1. the in-game tests still pending (`docs/testing/2026-10-24-transfers-in-game-checklist.md`,
   sections 0–16) and the fixes they bring;
2. sub-projects 4.4 (Lift for a rendezvous), 4.5 (one button: transfer + rendezvous + dock) and 4.6
   (gravity assist) of `specs/2026-10-22-transfers-rendezvous-docking-design.md`.

Follow the user's working rule: build every part, and the user tests in game at the end.

## What the user wants (2026-10-30)

- **Booster return, flown by the player's switch.** A booster (or any dropped stage) comes back and
  lands at a chosen point, the KSC or anywhere.
  - Landing **without anyone watching** (real physics far from the active vessel) is **dropped**: too
    hard for now, see "Rejected" below. The player switches to the booster to land it.
  - Background simulation in the StageRecovery style was **rejected**: the user wants a real flight.
- **A return point** set in the K2D2 window, for players who do not write scripts (as a KSP1 mod
  did). Three ways:
  - capture the current position;
  - type the coordinates;
  - pick with the picker, in the flight view (in game) or on the map.
- **Lift plans the return**: it keeps the fuel the booster needs to come back, and drops the stage at
  the right moment so the booster can land at the chosen point instead of crashing.
- **At separation**:
  - the game pauses;
  - the mod makes sure Redux's stage recovery system has saved;
  - the mod tells the player to take control of the booster.
- **A "Return" tab** lands the booster at the chosen point.
- **Flight recording (opt-in)**: a video of the whole flight, Lift and landing included, from the
  point of view of one or several cameras, capped at 720p or 1080p. The user considers it necessary
  for realism and immersion. Recording starts with the Lift when the option is on, and goes on
  through the booster's return.
- **Our own scripting language**, not Redux's Lua surface. An onboard "computer" part (on the rocket,
  or on the booster to recover it) runs what was coded.

## Phase 0: scan the game and Redux first (mandatory)

Before any spec, read everything available, so nothing is missing and nothing is rebuilt that the game
already has. Sources, in this order:
- `Modding-Docs/` (grep);
- `API-Documentation/xml/<latest beta>/Ksp2Redux.xml`. Never search API-Documentation recursively;
- the IL of `Ksp2Redux.dll` of the installed game. The existing IL tools are Mono.Cecil scripts in
  the session scratchpad (`il5/il.ps1`, `recon/cecil.ps1`, `find.ps1`); rebuild them if gone;
- the installed game files: settings, `mods/`, Redux's own UI.

Write the findings to `.research/booster-return/NOTES.md` (not versioned): one line per fact, with the
type or member name, what was read, and what stays a guess. Then add here, or to the specs, every
missing item the scan reveals.

### Questions the scan must answer

**Stage recovery and separation**
- What is Redux's "stage recovery" exactly? Look for recovery, debris, staging save, FMRS-like flows:
  - when does it save, and what?
  - does it let the player fly one stage, then come back to the other?
  - does it expose events or an API we can call or wait on?
  - how do we know "it has saved"?
- What happens to the vessel the player leaves when switching to the booster: the upper stage, still
  suborbital or in the air?
  - does it go on rails, and is it destroyed when unloaded in the atmosphere (KSP1 behaviour)?
  - at what distance and altitude?
  - This decides whether the flow is "switch and come back", or "fly one, then reload the save at
    separation to fly the other".
- What is the physics range, and the load/unload and pack/unpack rules for non-active vessels?
- Vessel switching while paused: is it allowed? Does it change the warp state, or the SAS and
  throttle states? We already know the throttle is not cleared on the old vessel; see `OwnVesselStop`.
- Staging events, and how a separated stage becomes a vessel: id, name, control source, the
  `ControlOwner` rule already read in the IL for the jettison.

**Control of the booster**
- What a dropped stage needs to be controllable: probe core or command part, SAS level, electric
  charge, RCS.
- Can the mod detect at the Lift start that the booster cannot be controlled, and refuse or warn?
- Grid fins and control surfaces: how KSP2 drives them (`Data_ControlSurface`, deploy states), their
  authority and drag, and whether the SAS uses them.
- Landing legs (already handled by `LandingGear`), engine relight limits (ignitions, ullage), and
  throttle limits (minimum throttle) of booster engines.

**Atmospheric descent physics**
- Aerodynamic lift of a falling booster: can we steer the impact point with attitude?
- Wind: none expected in KSP2. Verify.
- Terrain at the landing point, the KSC pads (`KscLocation`), and a "landing pad" surface object if
  any.
- Reuse what we have: the drag model and atmosphere (`ReentryPredictor`, `AtmosphericPredictor`,
  `VesselAeroLookup`, `ReentryContext`), the heating model of 2026-10-30 (`Pilots/Landing/Heating/`),
  the braking simulation, and the precision terminal guidance (`TerminalGuidance`).

**Picker and positions**
- Flight-view picking: raycast on the terrain from the mouse. Which terrain colliders exist, and which
  layer?
  - The map picker exists already (`ReduxPlacement`).
  - The current position comes from lat/lon/altitude, as `KscLocation` reads them.
- Can Redux's waypoints (`ReduxWaypoints`) store the return point so it shows in game?

**Cameras and recording**
- Redux's camera system: the views and modes, the active camera, any extra or part cameras, their
  API, and whether a second camera can render to a `RenderTexture` without breaking the main view
  (URP renderer, post-processing, UI layers).
- Is there any camera part (hull camera)? If not, does a point of view need a new part (with the
  Redux-Modeling-Kit) or a free camera attached to a part transform?
- The cost of an extra render in URP, and the frame pacing when the game runs below the recording
  rate.
- Can the UI be kept out of the recorded image?
- Audio: is it possible and wanted? It is not asked for. Note what Wwise allows.
- The pause and time warp states during a recording.
- For the overlay (R7):
  - reading per-engine ignition and thrust, and engine part positions;
  - propellant amounts per stage;
  - composing an overlay into a captured frame under URP.

**Scripting**
- What runs on an unloaded vessel: `PartComponentModule` updates ("executed even on unloaded
  vessels", `Modding-Docs/Game reference/Part modules/General overview of custom modules.md`).
- Saving per-part module data in the save (our own `Data_` class, with `[KSPState]`), so a script
  and its state survive save and load.
- Creating a new part (the computer):
  - `Modding-Docs/Part modding guides/`;
  - Redux-Modeling-Kit;
  - the Patch Manager for the part definition;
  - tech-tree placement in campaigns, and campaign features.

## Sub-projects (proposed order)

### R1. Return point in the K2D2 window
- A return point per vessel and per stage, with a name, body, latitude, longitude and terrain
  height. It is saved; say where (plan store or part module data) once the scan is done.
- Set it by:
  - capturing the current position;
  - typing it in;
  - the map picker (existing);
  - the flight-view picker (new: a raycast on the terrain);
  - choosing the KSC pads, or a saved list.
- Show it: a waypoint if Redux allows it, and the distance and bearing in the tab.
- **Only one booster counts**: the real booster, the stage that takes the rocket out of the atmosphere
  or nearly. Other stages (upper stages, side boosters, anything dropped later) get no return point
  and no recovery: never handle them.
- Missing item added: the body must match, and the point must not be under the sea unless that is
  asked.

### R2. Return tab: propulsive return landing in an atmosphere
This is the big new piece. Precision landing today works only on airless bodies, or under
parachutes.
- **Phases**:
  1. flip;
  2. boostback burn toward the point (closed loop, `TargetOrbitBurn`-style guidance on the impact
     point);
  3. coast with the engines first;
  4. optional entry burn, sized by the heating model and the heat guard (2026-10-30);
  5. aerodynamic descent steered to the point (grid fins if any);
  6. landing burn with terminal guidance (reuse `TerminalGuidance`, `FinalDescent` and the legs).
- **Before starting**, refuse with numbers when the booster cannot make it:
  - Δv, with a margin;
  - TWR;
  - no control;
  - heat.
- Also usable alone: drop a booster by hand, switch, then press Return. This tests R2 without R3.
- Warp: 1x or low physics warp only, per the user's rule for landings and reentry.
- Missing items added:
  - an abort and divert mode (land anywhere safe, or ditch in the sea) when the margin runs out;
  - a log of predicted against actual impact point, to calibrate the drag, as `reentry_drag_scale`
    does.

### R3. Lift plans the return
- At the Lift start, with a return point set, compute the booster's return budget. Its total is the
  sum of:
  - the boostback, depending on the speed and downrange at separation;
  - the entry burn;
  - the landing burn;
  - a margin.
- During the ascent, predict this budget again from the current state. Cut the booster engines and
  separate when its fuel left equals the budget, or earlier if the ascent would no longer be
  recoverable.
- Also check that the upper stage can still reach its goal: orbit, transfer, rendezvous. If both
  cannot be met, say so before the launch and let the player choose: recover the booster, or expend
  it.
- Reuse the per-stage Δv and staging tools: `StageBudget`, `StagedBrakePlan`, `LandingStages`,
  `BurnStaging`.
- Missing items added:
  - "the booster" is the stage that takes the rocket out of the atmosphere (see R1); propose it
    automatically, the player can change it in the tab;
  - fuel crossfeed, which can drain the booster's reserve: check it.

### R4. Separation: pause, save, switch
- At the planned separation:
  1. separate;
  2. pause;
  3. wait for, or trigger, Redux's stage recovery save (depending on Phase 0);
  4. show a message: take control of the booster, then open Return.
- What happens to the upper stage while the player flies the booster depends on Phase 0. It may need
  the upper stage left in a stable state (orbit, or a safe coast) before the switch, or the flow "fly
  the booster, then reload the separation save".
- The upper stage's own pilot (Lift) must stop cleanly when the active vessel changes, and must be
  resumable. Lift does not resume today: check it.

### R5. Flight recording
- Opt-in in the K2D2 window:
  - resolution 720p or 1080p;
  - frame rate;
  - one or several points of view;
  - an output folder under `<mod>/recordings/`.
- Recording covers the Lift, the separation, the switch to the booster and its landing. It follows
  the active vessel, or a chosen vessel when the camera allows it.
- **Encoding**: Unity has no runtime video encoder. Render to a `RenderTexture`, read it back
  asynchronously (`AsyncGPUReadback`), and pipe the frames to an external encoder (ffmpeg).
  Points to settle:
  - ffmpeg's licence and how the player gets it: shipping it, or the player installs it;
  - disk space;
  - a game-time frame clock, so the video plays at the right speed whatever the fps;
  - pause and warp: stop, or skip frames;
  - keeping the UI out.
- The performance cost of each extra render must be measured in game, never assumed. Show it in
  the tab.
- **Two recordings**, one per vessel: the booster's and the upper stage's (the rocket that goes on to
  orbit). Both start with the Lift, when the option is on.
- **The booster's recording** stops by itself 10 to 20 s after the booster has landed.
- **The upper stage's recording** goes on until the player stops it. Constraints:
  - it can only be stopped once the booster has landed and its own recording has stopped. Before
    that, the stop button is refused, with the reason;
  - after that, the player stops it whenever they want;
  - for the rest of the mission, the player can start a new recording at any time, for as long as
    they want.
- **No file size or duration limit** (decided by the user on 2026-10-30). Only show the size and the
  free disk space, and warn when the disk is nearly full.
- **The recording pauses with the game.** It follows the game, not the wall clock: nothing is
  recorded while the game is paused, and the video carries on as soon as the game runs again.
- **Timeline at the vessel switch** (to settle with Phase 0 and R4). The player flies the booster to
  the ground first, then comes back to the upper stage, by reloading the separation save if Redux's
  stage recovery works that way. When the upper stage's flight resumes, its recording continues from
  the separation point, and its overlay replays the booster's telemetry recorded during its landing
  (see R7), in sync with the game time, up to touchdown.
- Missing item added: the recording must survive the vessel switch, the pause and a save reload.

### R6. Our own scripting language and computer part
- A small language interpreted in C# inside the mod:
  - sandboxed: no file or network access, an instruction budget per frame;
  - its syntax is ours, to design in its own spec.
- Library: the mod's pilots as functions (`lift`, `circularize`, `transfer`, `rendezvous`, `dock`,
  `deorbit`, `land`, `return`, `record`), plus state reads (altitude, speed, fuel, stage) and waits
  (`wait until …`).
- A "computer" part that holds and runs a script:
  - a new part: mesh, Patch Manager definition, tech node;
  - its own module data, saved with the vessel.
- An editor in the K2D2 window, with errors and a log.
- Scripts run on the loaded vessel. Use on unloaded vessels is limited to what Phase 0 shows is safe.
  Autonomous landing out of view stays out of scope (see "Rejected").
- Missing items added: example scripts (booster return, launch to orbit), and versioned scripts
  stored per vessel.

### R7. Finishing: SpaceX-style video overlay
This comes last, after R1–R6. It adds a broadcast overlay on the recorded videos, like SpaceX's
Starship webcasts.
- **Both vehicles on screen**: the booster on one side, the upper stage on the other.
- **Per vehicle**:
  - speed and altitude;
  - fuel, per propellant, as a gauge;
  - inclination;
  - an **engine diagram as seen from below**, like Starship's. Each engine is a dot, lit or off,
    placed from the real engine positions projected onto the stage's base plane.
- **In the booster's video**, the upper stage shows **"SIGNAL LOST"**: no data for it, by design.
- **In the upper stage's video**, the booster's data come from the telemetry recorded while the
  player flew it (game time, speed, altitude, fuel, engine states, landed). They are replayed in sync
  up to the booster's touchdown, then the booster panel shows "LANDED".
- **Telemetry recorder**: a time series per vessel, stored in game time, written during the flight
  and saved next to the videos, so a save reload does not lose it. The same file can later feed a
  post-flight replay or a chart.
- **Rendering**: the overlay is drawn into the recorded frames only, not over the player's screen,
  unless an option shows it live too. Scan for:
  - how to compose UI Toolkit or a `RenderTexture` overlay into the captured frame;
  - fonts and licences (the overlay must not copy SpaceX's assets; it only takes the style).
- Mission clock: T+ from liftoff on both panels.

## Rejected (2026-10-30)

- **Autonomous landing out of view, with real physics.** It runs into the physics range, float
  precision far from the active vessel, terrain colliders loaded only around the active vessel, and
  rails warp being blocked. The user dropped it for now. It may come back later as an option with a
  performance cost.
- **Background simulation, StageRecovery style.** It is not a real flight, and there is nothing to
  film.
- **Redux's Lua surface for scripting.** We write our own language.

## Rules that apply

- CC BY-SA 4.0; add a CHANGELOG entry for every visible change.
- Pure code tested in EditMode; thin glue.
- No Redux bug reports: changes to the game go through Harmony patches in our mod.
- Never commit game DLLs.
- Every in-game finding goes into the parent `CLAUDE.md` with its date. Anything not verified in game
  stays marked "not verified".
