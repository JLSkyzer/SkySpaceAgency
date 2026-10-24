# In-game checklist: transfers, Lift transfer goal (later: rendezvous, docking)

This list covers sub-projects 4.1 (Transfer tab) and 4.2 (Lift "Prepare transfer"). Items for 4.3 (Rendezvous tab) are in sections 8–11; 4.4 to 4.6 will be added as they are delivered.

**For every test:** quicksave first, and send `Player.log` afterwards: `C:\Users\<you>\AppData\LocalLow\Intercept Games\Kerbal Space Program 2\Player.log` (`Ksp2-2.log` does not hold the mod lines). The lines to read start with `[Transfer]`, `[Lift]`, `[TargetOrbitBurn]`, `[Rendezvous]` or `[Docking]`.

## 0. First start
- `[Transfer] game trajectory model: …` should show patched conics while coasting.
- `[Transfer] frame check …`: the position computed two ways should agree to within a few metres.
- The terrain height is logged for each airless body. A 0 or "unknown" on the Mun or Tylo needs a fix.

## 1. Kerbin → Mun, capture (4.1)
1. From a 100 km circular orbit, open the Transfer tab and select Mun, Orbit, low circular. Click Plan.
   - The injection should be about 840 m/s.
   - The steps and their explanations should read correctly.
2. Click Start and let it fly: warp, injection, SOI change, corrections, capture. Check:
   - planned against flown Δv for each burn;
   - which corrections were skipped, and why;
   - our SOI change time against the game's;
   - the final orbit, which should be about the atmosphere-free low orbit + 10 km.

## 2. Kerbin → Duna with Prepare only (4.1)
1. Plan, tick Prepare only, then Start.
2. Fly each node yourself. Try both deleting a node afterwards and leaving it. In both cases the pilot should move on to the next step, but only after a full burn.
3. Cut the throttle in the middle of a node. The pilot must **not** count that node as flown.

## 3. Save and reload during the coast (4.1)
1. Start Kerbin → Duna, quicksave during the cruise, then quickload.
2. The tab should offer "Resume plan to Duna (next: …)". Click Resume.
3. Check:
   - no saved burn is replayed: every burn still ahead is planned again;
   - the `frame check` line before and after the reload.

## 4. A stage that runs dry during a burn (4.1)
Use a rocket whose transfer stage runs out of fuel mid-injection. Check:
- `[Transfer] staging {n} drops: …` lists the expected parts, followed by `staged during the burn: stage {n}`;
- `stages in firing order` matches the staging stack;
- a stage holding a parachute or a docking port stops the burn with "Stage manually";
- the pilot continues on the same vessel (the log shows the ids before and after).

## 5. Moon to planet (4.1)
From a low Mun orbit, transfer to Duna.
- After the Mun escape, the log should show a `leg … starts …: AimExcess` line.
- No full search should run mid-trip.

## 6. Lift "Prepare transfer to Duna" (4.2)
1. On the KSC pad, open Lift, select the goal Prepare transfer and the target Duna, then Start.
   - Read the launch countdown, the azimuth, and the plane inclination, which should be about 4°.
2. The mod warps, then fires the first stage itself. Test this only on a rocket with nothing staged.
   - Check that the ascent does not head the wrong way: the `[Lift] plane error …` line should decrease.
3. After circularization and the trim:
   - the plane error should be under 0.1°;
   - the message "Ready for the Duna transfer: open the Transfer tab" should appear.
4. In the Transfer tab, Plan to Duna. The plan should start from this orbit without a large plane change.

## 7. Lift "Prepare transfer to Mun" (4.2)
Same as 6. The plane should be the Mun's orbit plane, or the closest one reachable from the KSC.

## 8. Transfer again after the 1x fix (4.1)
Resume or re-plan the Duna transfer. The injection should last about 70 s at 1x, with the remaining Δv going down to the end (`[TargetOrbitBurn]` lines), and no "Burn diverging".

## 9. Rendezvous with a station in low Kerbin orbit (4.3)
1. Put a station in a ~120 km orbit. Launch a vessel to about 80 km, 30° behind it.
2. In the Rendezvous tab, select the station and press "Rendezvous only". Check:
   - the plan: plane match, intercept, Δv;
   - after the intercept: miss distance and relative speed (`[Rendezvous]` lines);
   - the brake ends about 200 m away at about 0 m/s.

## 10. Docking with RCS (4.3)
From within 2 km, press "Dock only" (or use "Rendezvous + dock" from further away).
- `[Docking] rcs mapping …`: the three axes are found, with no dead axis on a symmetric RCS.
- Ports chosen (the tab shows the pair). Change the pair by hand once.
- Holds at 50 m and 10 m, then the final approach at 0.2–0.5 m/s. `[Docking] trace:` lines show the lateral error and closing speed.
- "Docked". After docking, RCS is left as it was on the merged vessel and SAS is in Stability Assist.
- Also try a port on the far side of the station: the vessel must go around it, never through it.

## 11. Docking without RCS, and safety (4.3)
- On a vessel without RCS: `[Docking] main-engine deadband …`, and the docking either succeeds with wider holds or is refused with its reason.
- During the approach, switch to the station and back. The pilot must stop, without firing anything on the station, and with our own throttle and RCS cut.
- Approach too fast, by giving a push by hand: the pilot must abort, back off to 20 m and retry once.

## 12. Physics warp during burns
During a long burn (the Duna injection, for example), the mod should go to x2 and then x4. Look for `[TargetOrbitBurn] physics warp: …` lines: the vessel stays RigidBody and steers normally. The warp level the game reports should be x4. If you press rails warp by hand during the burn, the mod must bring it back to physics or to 1x. After the burn, your usual warp mode is back.

## 13. Rails warp on long burns
Use a burn of more than 2 minutes, for example on a low-thrust stage. Check:
- `[TargetOrbitBurn] rails burn:` entry at x10 or x50, then the guard result (trajectory found, at least one patch);
- the exit back to x4 before the end, then the closed loop finishing the burn;
- the Δv applied during rails, planned against actual.
If the guard fails, the mod must drop to physics warp at once without losing fuel for more than about 2 frames. A landing, reentry or rendezvous burn near the target never goes on rails.
