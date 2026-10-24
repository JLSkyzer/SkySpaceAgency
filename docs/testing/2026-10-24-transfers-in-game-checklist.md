# In-game checklist: transfers, Lift transfer goal (later: rendezvous, docking)

This list covers sub-projects 4.1 (Transfer tab) and 4.2 (Lift "Prepare transfer"). Items for 4.3 to 4.6 will be added as those sub-projects are delivered.

**For every test:** quicksave first, and send `Ksp2-2.log` afterwards. The lines to read start with `[Transfer]` or `[Lift]`.

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
