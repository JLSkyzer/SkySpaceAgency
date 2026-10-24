# Transfers, rendezvous and docking — design

- **Date:** 2026-10-22.
- **Status:** draft for the user's review.
- **Mod:** SkySpaceAgency (KSP2 Redux 0.2.9 / 26w42c, Unity 6000.6.0f1).
- **Builds on:** the Orbit tab, the closed-loop burns (`TargetOrbitBurn`, `BurnWarp`), the Lift pilot and the K2-D2 Docking Assistant.

**Notation:**
- **[user]**: a decision the user made in the brainstorm on 2026-10-22;
- **[call]**: a decision the designer took, which the user can correct;
- **[builder]**: left to the implementer, within the stated bounds.

## 1. Intent

The user does not know how to fly transfers, and the ones they try cost too much Δv (Duna, for example). The mod must take them anywhere in the system, cheaply, and explain what it does:
- a transfer from orbit around any body to any other body;
- a launch from any body at the right moment for a transfer or a rendezvous;
- a rendezvous with a vessel, wherever it is;
- docking with that vessel.

The mod flies all of it by itself, unless the player asks it only to prepare the maneuvers.

**Success:**
- From a low Kerbin orbit, one press brings the vessel into a low Duna orbit for a Δv close to the ideal patched-conic transfer, and every step is announced.
- From the launch pad, one press and then the rendezvous button end docked to a station.

## 2. Goals, non-goals, anti-goals

### Goals

1. **Any body to any body [user].**
   - The departure is wherever the vessel is, around any body. Kerbin is never assumed.
   - Targets: a moon of the current body, the parent body, a sibling moon, another planet, or a moon of another planet.
2. **Δv first [user].** Among the possible plans, the cheapest wins, even if that means waiting for a later window or a longer trip: time warp exists.
   - An optional maximum trip duration caps the search [call]. The default is no cap, with the search horizon limited to two synodic periods.
3. **The mod flies everything [user]:**
   - the wait in warp until the window;
   - any change of the current orbit;
   - the injection;
   - the mid-course corrections;
   - the arrival.

   An option, **Prepare only**, creates the maneuver nodes for the player to fly [user].
4. **Arrival type [user]:**
   - **Encounter**: a flyby with the periapsis at a chosen altitude.
   - **Orbit**: capture into an orbit of the target. The default is low and circular; the player can change Ap, Pe and inclination [user].
   - "Low" means atmosphere depth + 10 km, or highest terrain + 10 km on an airless body [call].
5. **Explain [user].** When the current orbit must change first to make the transfer cheaper or possible (plane, altitude), the mod does it and the tab says why, with numbers. Example: "Your orbit is inclined 28° to the departure direction: changing plane first saves 340 m/s."
6. **Gravity assist option [user].** When a flyby of another body saves Δv, the mod proposes it and shows the saving.
7. **Lift tab, new goals [user]:**
   - **Lift and prepare orbit for transfer**, with a choice among all bodies. The mod waits on the pad for the right launch time [user], reaches the optimal parking orbit for that transfer, and stops there. The transfer starts from the Transfer tab [user].
   - **Lift for rendezvous**, from any body. The mod waits for the launch time that puts the vessel in the target's plane and close to it, then chains into the rendezvous [user].
8. **Rendezvous and docking [user]:**
   - The mod meets the target vessel precisely, then docks, handling both rotation and translation.
   - It picks the ports automatically, and the player can change them [user].
   - With RCS it translates with RCS. Without RCS it tries with the main engine [user] and says the approach is less precise.
9. **One button for a far target [user].** When the target vessel orbits another body, one button chains transfer, rendezvous and docking. The optimal-orbit and gravity-assist logic of the transfer applies [user].
10. **Delivery [user].** All six sub-projects are built and installed, then tested in game at the end.

### Non-goals (this design)

- Aerobraking, aerocapture and lift guidance in an atmosphere [call]. The capture is propulsive.
- Landing on arrival. That is the Land tab's job; the player chains it by hand.
- Lagrange points, n-body trajectories, and low-thrust spirals (ion engines).
- Rendezvous with an asteroid or a body. Only vessels and their ports are targets.
- Undocking and reorganising stations.
- More than one gravity assist per plan [call]. Multi-assist tours (Eve-Eve-Jool…) can come later.

### Anti-goals (failure, even if it technically works)

- **Stranding.** The vessel is never left on a trajectory it cannot finish for lack of Δv.
  - Before anything is burned, the plan's total Δv, capture and reserve included, is checked against the vessel's.
  - If the vessel falls short, the start is refused and the numbers are shown.
- **Silent orbit changes.** Any burn that is not obvious (a plane change, a phasing orbit, lowering the parking orbit, an assist) is announced with its reason and cost before the mod flies it.
- **Collision.** During docking the vessel never hits the target off-axis or above a safe speed. At the first doubt it stops, then backs off.
- **No way out.** One button stops every phase. A stop never leaves a burn running or the warp on.

## 3. Constraints

- **The mod's existing rules:**
  - licence CC BY-SA 4.0, and a CHANGELOG entry for every visible change;
  - English UI and logs;
  - K2D2 namespaces kept;
  - no game DLL committed;
  - Alt-O stays shared with K2-D2.
- **Physics in our own pure code**, testable in EditMode:
  - patched conics, with two-body motion inside each SOI;
  - our `KeplerPropagator` (universal variables);
  - the frames of part 2: inertial Zup, z = rotation axis.
- **Body data** comes from the game at run time, never hard-coded: `GetAllCelestialBodies`, `referenceBody`, `sphereOfInfluence`, `gravParameter`, `Orbit`, `GetTruePositionAtUT`. The tests use a synthetic Kerbol system with stock-like values.
- **Fly closed-loop.** The game's own patches are read only as a cross-check, and logged: `PatchedConicSolver.CurrentTrajectory`, `PatchEndTransition`, `UniversalTimeAtSoiEncounter`, `ClosestApproachDistance`.
  - Redux also has n-body patch types (`PatchedNBodyOrbit`). The first task must find out from the IL whether the vessel's real motion follows patched conics [builder].
  - If it does not, the mid-course corrections absorb the difference.
- **Burns** reuse `TargetOrbitBurn` and `BurnWarp`, extended to targets a closed elliptic orbit does not describe: a hyperbolic excess velocity vector, or an arrival periapsis.
  - Long burns are centred on the planned point.
  - A burn longer than about 10 % of the orbit period is shown with a warning [call].
- **Prepare only** keeps the rule learnt on 2026-10-20: the game has dropped every second node, so only one game node exists at a time. The next one is created from the orbit actually reached, as in the Orbit tab.
- **UI:** built on the existing `K2D2_Window.uxml` system, one template per tab. A new UXML must be added as a template of `K2D2_Window.uxml`, because only that file is an Addressables entry.
- **Campaign features:** the mod may be disabled, so everything is gated on `Enabled`, as today.
- **One pilot at a time.** Starting a transfer, a rendezvous or a docking stops the others, as `ResetControllers` does today.
- **The user tests at the end [user].** Each sub-project logs what the in-game check needs, under one prefix per pilot: `[Transfer]`, `[Rendezvous]`, `[Docking]`, `[Lift]`.
- **Resuming a plan [user].** A running plan survives a save and reload, a scene change and a vessel switch, then resumes where it was.
  - The plan is serialized per vessel: stages, burn targets, correction times, arrival parameters and the current phase. It is stored next to the mod's settings, keyed by the vessel's id [builder].
  - When that vessel is next active in flight, the mod rebuilds the plan from the vessel's actual state. It re-plans the current step from the orbit reached, never replaying an old burn.
  - The tab shows "Resume plan to Duna (next: mid-course correction in 42 d)", and the player confirms with Resume or Cancel. It does not resume without the player [call].
  - This applies from sub-project 4.1 onward, so the pilot architecture carries it from the start.
- **Staging during burns [user].** During its burns, the mod drops empty stages by itself, using the existing auto-staging logic (`StagingPilot`) or an equivalent [builder].
  - Staging is never fired during a docking approach.
  - A stage that holds a docking port, a parachute or the command part is never dropped by the mod.
- **Δv check per stage [user].** The margin check uses the game's per-stage Δv (`VesselDeltaV.StageInfo`, vacuum). The total covers the plan, and the stage or stages left at the arrival must alone cover the capture plus its reserve. Otherwise the start is refused with the numbers per stage.
- **The target vessel is never touched [user].** The mod pilots only the player's vessel: no switch to the target, and no SAS or port action on it.

## 4. The six sub-projects, in order [user]

Each sub-project gets its own plan and its own reviews. Later ones reuse earlier ones.

### 4.1 Transfer to a body (new Transfer tab)

**Inputs:**
- target body, from a list of every body grouped by parent;
- arrival type (Encounter / Orbit) and its parameters;
- Prepare only;
- Use gravity assist (inactive toggle until 4.6);
- Max trip duration (optional).

**Planning:** the case depends on the body hierarchy.

| Case | Example | Method |
|---|---|---|
| Child → its parent | Mun → Kerbin | Escape burn that puts the periapsis around the parent at the arrival altitude. |
| Parent → its child | Kerbin → Mun | Injection from the parking orbit, timed by a Lambert search inside the parent's SOI, then capture or flyby at the child's periapsis. |
| Siblings | Kerbin → Duna, Laythe → Vall | Lambert at the common parent over departure date × time of flight (porkchop grid, then refinement). Hyperbolic excess at both ends. Departure burn from the parking orbit (Oberth). |
| Different branches | Mun → Duna, Ike → Laythe | Composition: climb to the common ancestor, Lambert there, then descend. Leaving a moon, the trajectory must exit its planet's SOI with the required excess velocity. |

**Cost of a candidate:**
- **Sum:** departure Δv + capture Δv (0 for an encounter) + any change of the current orbit + a reserve for corrections (2 % of the total, at least 10 m/s [call]).
- **Selection:** without a duration cap the cheapest wins. With a cap, the cheapest within the duration wins.

**Different branches:** in the tests, the plan must stay within 10 % of the ideal patched-conic total [call].
- The method is [builder]. Either a direct escape from the moon aimed at the required exit from the planet's SOI, or a staged departure through an eccentric orbit of the planet when that is cheaper.
- The plan shows each stage.

**Change of the current orbit:** before the departure, the mod compares two plans and keeps the cheaper.
- **Direct:** depart from the orbit as it is, folding any plane change into the injection.
- **Orbit change first:** change the plane and/or the altitude first, then depart.
- The tab says why, for example: "Orbit plane change first: the departure direction is 28° out of your orbit plane, which saves 340 m/s."
- When the departure direction's declination exceeds the orbit's inclination, a plane change or a broken-plane mid-course correction is needed. Both are compared [builder].

**Window:** the tab shows
- the departure date (Y/D/H) and the time to wait;
- the trip duration and the arrival date;
- the Δv of each step and the total;
- the vessel's Δv and the margin.

The start is refused if the margin is negative.

**Flight sequence:**
1. Warp to the window. The warp stops before every burn and every SOI change.
2. Change of orbit, if one is planned.
3. Injection: a closed-loop burn toward the required hyperbolic excess vector.
4. Escape: exit the SOI, then check against the plan.
5. Closed-loop corrections aimed at the arrival periapsis (B-plane: periapsis altitude and inclination at the target):
   - one shortly after leaving the SOI;
   - one at mid-course;
   - one some time before entering the target's SOI;
   - each one skipped if it is below 1 m/s.
6. Arrival: capture at the periapsis toward the chosen orbit, or let the encounter pass. For a capture whose orbit has another inclination, the approach correction sets the plane, not a burn after the capture [call].
7. Done, for example: "In orbit of Duna: 112 × 108 km, 0.4°".

**Prepare only:** same plan, but the mod creates one game node at a time for the next step. After the player flies it, the mod re-plans from the orbit reached, as the Orbit tab does.

**Refusals, each with a message:**
- the vessel is neither in a closed orbit nor on an escape the plan can use;
- no engine;
- not enough Δv;
- no window within the cap;
- the target is the current body;
- the capture orbit is below the atmosphere or the terrain.

### 4.2 Lift and prepare orbit for transfer (Lift tab)

- **Goal selector** in the Lift tab: Orbit (today's behaviour, unchanged), Prepare transfer to [body], or Rendezvous with [vessel] (see 4.4).
- **Plane and altitude:** for a transfer, the 4.1 plan gives the required parking-orbit plane and the parking altitude. The plane contains the departure direction, or is the target moon's plane for a parent → child transfer.
- **Launch time:** when the launch site passes under that plane, on whichever pass (ascending or descending) comes sooner and costs less. The heading (azimuth) accounts for the body's rotation.
- **Site latitude above the plane's inclination:** the closest plane is used, and the tab shows the plane change that remains.
- **On the pad:** the mod waits in warp until the launch time minus the ascent lead, then launches with today's Lift ascent at the computed heading. The ascent, coast and circularisation are unchanged, apart from the heading and the target altitude.
- **At the end:** the mod stops in the parking orbit and shows "Ready for the Duna transfer: open the Transfer tab". It does not start the transfer [user].
- **Departure body:** any body Lift supports today. Lift already reads the current body's atmosphere and radius.
- **Distant windows:** the window itself may be far off. The parking orbit is planned for the next window that 4.1 finds (within the cap), and the tab says when that window is.

### 4.3 Rendezvous and docking around the same body (Rendezvous tab, replacing the Dock tab)

**Tab [call]:** the existing Dock tab becomes **Rendezvous**.
- **Target list:** every vessel, grouped by body. The game's current target is pre-selected.
- **Port pair:** chosen automatically, and can be changed.
- **Buttons:** Rendezvous + dock, Rendezvous only, and Dock only (enabled within 2 km).
- **Reuse:** the K2-D2 assistant's kill-speed and final-approach controllers are reused or rewritten as needed [builder].

**Rendezvous:**
1. **Plane match:** if the relative inclination exceeds 0.05° [call], a closed-loop burn at the relative node.
2. **Phasing:** the cheapest transfer within a few revolutions.
   - If needed, wait in a phasing orbit: lower to catch up, higher to fall back.
   - The number of revolutions is chosen by Δv first [user], with a cap of 10 revolutions [call].
3. **Intercept:** a Lambert intercept to a point 200 m short of the target on the same orbit [call], with one closed-loop correction halfway.
4. **Braking:** cancel the relative speed at 200 m.
5. **Logs:** the miss distance and the relative speed at each step.

**Docking.**

*Port choice:*
- On both vessels, the mod pairs free ports: `Data_DockingNode.CurrentState == Ready` and `PartComponentModule_DockingNode.IsCompatibleWith`.
- It prefers the pair that needs the least rotation of our vessel, on the side of the target facing us.
- The tab shows the pair, and the player can pick another one from two lists.
- Before the approach, the mod sets "control from here" on our port (`Module_DockingNode.OnControlFromHere`) and targets the target's port (`SetTargetByID(part)`).

*Port axes:*
- They come from the view-side docking transform (`Module_DockingNode.GetDockingTransform`), which is only readable while both vessels are loaded, within physics range.
- The aim is the port face, not the part's centre of mass. Aiming at the centre of mass was a known limit of the old assistant.

*Approach, with hold points [call]:*
1. Align on the target port's axis at 50 m.
2. Hold at 50 m, then at 10 m.
3. Final approach at 0.2–0.5 m/s, inside the capture cone (`CaptureMinFwdDot`, `CaptureMaxRvel`).
4. Roll alignment when the port requires it (`MinRollDot`).

*Translation with RCS:*
- RCS is turned on (`SetActionGroup(RCS)`), and at least one thruster must be `Operational`.
- The mapping from `FlightCtrlState X/Y/Z` to the vessel's axes is **measured**, not assumed. A short probe pulses each axis and reads the velocity change, as the landing rotation probe does. This settles the old comment saying the axes are wrong.

*Translation without RCS [user]:*
- The main engine does the corrections: turn, short burn, then turn back to the port axis.
- The final approach is a coast at about 0.2 m/s.
- The tab says "No RCS: docking with the main engine, less precise".
- Hold points are farther out and tolerances wider [builder].

*Safety:*
- The mod aborts, backs off along the axis to 20 m and retries once when:
  - the lateral error at a given distance leaves a cone;
  - the closing speed exceeds the limit for that distance.
- It refuses to dock when:
  - the target spins faster than 1°/s around the approach axis [call]. Below that, the guidance tracks the target port's moving axis. The mod never acts on the target [user];
  - the target is out of physics range at the docking stage.

*End:* on `VesselDockedMessage` or `CurrentState == Docked`, the tab shows "Docked". SAS is released to Stability Assist, and RCS goes back to its state before the approach.

### 4.4 Lift for rendezvous (Lift tab)

- **Goal "Rendezvous with [vessel]".** The launch time is chosen on two conditions:
  - the launch site passes under the target's orbit plane;
  - after the ascent, the phase angle allows a rendezvous within a few revolutions. A phasing wait in orbit is acceptable, with priority to Δv [user].
- **Parking orbit:** slightly below the target's [call]. For example, 10 km below the target's orbit (or its periapsis − 10 km), and at least atmosphere + 10 km.
- **Then:** the mod chains into 4.3, rendezvous and then docking if asked [user].
- **Target plane out of reach:** if the target's inclination is below the site's latitude, the closest plane is used, and the plane-match burn of 4.3 does the rest. Its cost is shown.

### 4.5 One button for a target around another body

- **Button:** in the Rendezvous tab, a target vessel orbiting another body offers "Transfer + rendezvous + dock" [user].
- **Sequence:** 4.1 with the arrival orbit set to the target's orbit plane, at an altitude slightly below it [call], then 4.3. This folds the capture and the plane match into the transfer's arrival correction instead of separate burns.
- **Gravity assists:** 4.6 applies to the transfer leg when it is enabled [user].
- **The plan:** it shows every leg and the total Δv, and is refused if the vessel's Δv cannot cover it.

### 4.6 Gravity assists

- **Option "Use gravity assist":** the transfer search also considers plans with **one** flyby of an intermediate body [call]. Two kinds of flyby are covered:
  - a sibling of the departure and target bodies, at the level of their common parent (Eve, or Kerbin itself, on the way to Jool);
  - a departure assist from a moon of the departure body (a Mun flyby on the way out of Kerbin).
- **Search:**
  - porkchop over departure date, flyby date and arrival date;
  - patched-conic flyby, powered at the periapsis when needed, the Δv of a powered flyby counting in the total;
  - flyby periapsis above the atmosphere or the terrain + 10 km.
- **Offer:**
  - an assist is offered only when it saves at least 5 % of the direct plan's Δv [call] and fits the duration cap;
  - the tab shows both plans side by side, for example "Direct: 2 140 m/s, 1 y 120 d — Via Eve: 1 760 m/s, 2 y 30 d";
  - the player chooses, and the cheaper one is pre-selected [user: Δv first].
- **Flight:** the corrections before the flyby aim at its periapsis (B-plane). The ones after it aim at the target.

## 5. Shared pieces (expected; exact shape [builder])

- **Pure maths**, all tested, in `Pilots/Transfer/` or a shared `OrbitalMechanics/`:
  - Lambert solver (universal variables; multi-revolution, for the rendezvous);
  - hyperbolic departure and arrival (excess velocity ↔ burn at the periapsis, asymptote direction);
  - body ephemeris from the game's orbits, composed up the hierarchy;
  - SOI transitions;
  - B-plane targeting;
  - porkchop search with refinement;
  - launch-window solver (plane crossing, azimuth with rotation);
  - phasing planner;
  - docking guidance (hold points, cone, speed profile);
  - RCS axis mapping.
- **Game glue:**
  - body and vessel snapshots (state, Δv, engines, RCS, ports);
  - the burn executor, extended to excess-velocity and periapsis targets;
  - warp across SOI changes;
  - node creation for Prepare only.
- **Refusal and explanation messages:** collected in one place per tab, as Landing does.

## 6. Verification

- **EditMode tests**, on a synthetic Kerbol system:
  - Lambert against published test cases;
  - Kerbin → Duna and Kerbin → Mun windows close to the community Δv-map figures (external estimates, labelled as such in the test comments);
  - Mun → Duna within 10 % of the ideal patched-conic total;
  - capture and flyby periapsis reached within tolerance after the simulated corrections;
  - rendezvous ending under 200 m in simulation;
  - docking guidance simulated down to contact inside the capture cone, with and without RCS;
  - launch window reaching the plane within 0.5° in simulation;
  - assist search finding a known Eve assist to Jool on the synthetic system, when it is cheaper.
- **Existing tests:** all stay green.
- **In game, at the end [user]:** a single checklist of the log lines of each pilot:
  - planned vs actual Δv;
  - miss distances;
  - SOI times vs the game's patches;
  - the RCS probe result;
  - the trace of the docking approach.

## 6b. Decisions from the builder check (2026-10-22)

- **Test references [call]:**
  - The "ideal patched-conic total" is computed in the tests by an independent brute-force optimizer: a fine grid over every free time, with no refinement shared with the production search.
  - The Kerbin → Duna and Kerbin → Mun windows must fall within ±10 % of the community Δv-map figures, which are external estimates.
  - Exception for Ike → Laythe: 12 % over the brute-force ideal is allowed (decided 2026-10-24). That reference leaves out the arrival moon's phasing, which the flown plan budgets and cannot avoid. [call]
- **Plane after the ascent [call]:**
  - In 4.2 and 4.4, Lift recomputes the heading during the ascent to steer toward the plane, using the same azimuth formula with the current state.
  - After circularisation, a small closed-loop plane trim burn inside Lift brings the plane error under 0.1°.
  - "Ready for the transfer" promises the plane within 0.1°.
- **Prepare only [call]:**
  - It covers 4.1, 4.3 (rendezvous), 4.5 and 4.6. Docking is always flown.
  - The mod creates the next node only, does not warp, and keeps every check and explanation.
  - After the player flies a node, the mod re-plans from the orbit reached.
- **Failure in a chain [call]:**
  - A failure in 4.4 or 4.5 stops the chain safely: no burn running, warp off, the vessel left on its current orbit.
  - The tab says which step failed, why, and what is left to do. The plan is kept, so that Resume re-plans from there.
  - The mod does not re-plan on its own.
  - The exceptions are those already in the design: a correction below its threshold is skipped, and a docking abort backs off and retries once.

## 7. Left to the builder

- The method for departures across branches, within the 10 % bound.
- The exact correction times, skip thresholds and porkchop resolution. The tab must stay responsive: spread heavy searches over several frames, or run them once at Plan.
- Whether to extend or replace the K2-D2 docking controllers.
- The tolerances without RCS.
- The layout of the new tabs, within the existing UI style.
