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

## 13. Rails warp on long burns (replaced by section 18 since Redux 26w46a)
Use a burn of more than 2 minutes, for example on a low-thrust stage. Check:
- `[TargetOrbitBurn] rails burn:` entry at x10 or x50, then the guard result (trajectory found, at least one patch);
- the exit back to x4 before the end, then the closed loop finishing the burn;
- the Δv applied during rails, planned against actual.
If the guard fails, the mod must drop to physics warp at once without losing fuel for more than about 2 frames. A landing, reentry or rendezvous burn near the target never goes on rails.

## 14. After the 2026-10-25 fixes (Duna transfer)
- Leave Node tab → ADVANCED → "Warp during burns" on **Off** for the first flight. Fly Kerbin → Duna on one plan.
- Read these lines in the log:
  - `state after the …` and `state mismatch after the burn`;
  - `physics mode RigidBody -> …`;
  - `late SOI entry into …` and `inside our SOI radius of …`;
  - `resume after a failed correction …` and `options: …`.
- Then try one flight with "Physics (x4)". If the trajectory jumps only with physics warp, the mismatch lines will show it.

## 15. Landing with a chosen stage (Land tab → "Land with stage …")
- Use a booster under the lander, with fuel left. Choose the lander stage and start a landing on an airless body or Duna, in Propulsive mode.
- The log shows `[Landing] stage plan: …` (drop height), and `auto-staging suspended`.
- The drop happens when the booster is dry or at the drop height: `cutting the throttle first`, then `stage drop: … fired / done`, then `the new stage pushes`.
- The lander must never fall onto the dropped booster. No `ALARM` line should appear.
- Also try precision landing on the Mun with two stages (`stage plan: precision: …`).

## 16. Deorbit, Δv refusal and reentry heating (Land tab, Propulsive mode)
Lines to read: `[Landing] deorbit: …` (plan, start check, phases) and `[Landing] heat: …` (model inputs, entries, predictions, guard).
1. **Airless, from orbit** (Mun, 30 km circular, precision off): press Brake.
   - `[Landing] deorbit: start plan ok … deorbit at +… s, Δv ~20 m/s to pe -2.0 km | brake Δv … | total …` then `start check: ok=true`.
   - The tab shows "Deorbit burn", then the usual Pause / Quick Warp / Brake / Touch Down. `phase deorbit end: Δv used … (planned …)` should match within a few m/s.
   - Precision on: unchanged (its own deorbit). Above a 100 km periapsis the tab turns precision off: the plain deorbit then flies.
2. **Δv refusal**: drain the tanks until the landing cannot be paid. The start is refused with "Δv X m/s for ~Y m/s needed (deorbit …, pre-brake …, brake …, +10 %)." and nothing moves.
3. **Staged landing, airless, from orbit** ("Land with stage 2"): it now starts from orbit. Stage 1 pays the deorbit (`stage plan: deorbit: stage 1 pays …`), the drops follow after the deorbit. On Kerbin or Duna from orbit it is still refused ("the staged plan has no drag model").
4. **Kerbin, from a 100 km orbit**, a stage with its engines first (retrograde):
   - `[Landing] heat: model inputs on Kerbin: … | heated parts: n of m read …` lists the parts with their airstream area and Cd at retrograde. A `0.00 m²` everywhere, or "heating not modelled", needs a fix.
   - `[Landing] heat: start plan: entries: pe 42.0 km: … | pe 35.0 km: …` and the chosen entry, `predicted peak … % … before the brake`.
   - Phases: "Deorbit burn", then "Pre-brake before the atmosphere" when planned (`plan pre-brake`), then "Atmospheric coast": warp stops 60 s before the interface, 1x in the air, SAS surface retrograde.
   - `[Landing] heat: prediction: brake start +… s …` every few seconds, then `[Landing] heat: brake at altitude …`: compare the predicted brake Δv with the Δv really burned at touchdown.
   - Compare `[Landing] heat: AeroCoast: … hottest <part> … %` with the predicted peak. The "Hottest Part" row shows it live.
5. **Heat guard**: start from orbit with a fragile engine, or deorbit by hand to a steep, fast entry, then press Brake above the atmosphere.
   - From 75 %: `[Landing] heat: guard on …`, throttle up surface retrograde (only within 15° of retrograde), `guard off` under 70 % or below Mach 1.
   - It must never pass 90 %; otherwise the red "Overheating: …" message and the log say so. "Heat guard is using the landing's Δv reserve" when it eats into the brake's Δv.
   - In Pause / Quick Warp / Waiting on a collision course inside the air, the guard switches the run to the atmospheric coast.
6. **Things to check against the game**: the difficulty "HeatScaling" read in the model inputs line; the `harshness`; that the predicted heating is in the right range (a factor 2 off means the area or the occlusion model is wrong); the frame time while the coast predicts (`… ms` at the end of the prediction lines).

## 17. After the 2026-11-04 fixes
1. **Heating judged on the engines only** (Kerbin, 100 km orbit or higher, Propulsive, the vessel that was refused because of `storage_0v_battery_electricity_radial`): press Brake.
   - No refusal because of a battery or any other part that is not an engine.
   - `[Landing] heat: model inputs on Kerbin: … heated parts: n engine(s) judged and m other part(s) logged …`: the engines are listed under "engines", the battery under "not judged". "No engine among the heated parts" here needs a fix.
   - `[Landing] heat: start plan: hottest other part (not judged, only engines are): … %`: informational.
   - In the air, the row is "Hottest Engine". The guard starts only on an engine (`guard on in …: engine …`). Watch whether the battery really burns: the model expects it far hotter than the game will make it (no shock-cone shading in the model).
2. **Lift "Prepare transfer"** on the pad, target Duna: after `[Lift] plan: …`, `[Lift] pad wait: throttle 1 cut to 0 …` (when the throttle was up), then the clock warps (no `[Warp] waiting for the engines …` line every 5 s), drops to 1x about 30 s before the launch, and the first stage fires at T-0 at full throttle.
   - The target list starts with Moho: Kerbol is no longer offered (Lift and Transfer tabs).

## 18. Native rails warp during burns (Redux 26w46a, default "Rails (native)")
Node tab → ADVANCED → "Warp during burns" should read **Rails (native)** (also after an old settings file that said "Physics + rails"). Each burn logs `burn warp mode: rails (native warp for the middle of the burn, 1x at both ends)`.
1. **A transfer injection** (Kerbin → Duna, or a long Mun injection) with a stage that can pay it:
   - ignition at 1x, then after about 1 s aligned: `[TargetOrbitBurn] rails burn: entering index … (x10, x50 or x100) …` with the burn left and the planned Δv on rails;
   - `rails burn: guard ok at frame …: under-thrust active, PatchedOrbit PatchedNBodyOrbit; the game's burn ends in … s`;
   - near the end `rails burn: step down to index …`, then `rails burn: leaving (… s of burn left …)` with `Δv applied … m/s, planned … m/s` (they should agree within a few %), and the closed loop finishes at 1x on the target orbit;
   - no `[Transfer] state mismatch after the burn` and no `about … m/s nobody commanded`; the SOI change happens when predicted.
2. **Fuel**: compare the Δv used with the same burn at 1x (or the planned Δv): no large loss as on 2026-10-24.
3. **Fallback**: if a `rails burn: the game does not take the burn …` line appears, the warp must be back at 1x at once and the burn must finish normally. Note which reason it gives (not flying the burn, or does not push).
4. **A short burn** (under about 115 s) stays at 1x for its whole length: a `rails burn` line must not appear.
5. **Never on rails**: Land tab (deorbit, Brake, precision, reentry), a Rendezvous burn within 2 km of the target, docking, a Lift circularization while still in the atmosphere. None may log `rails burn: entering`.
6. **Stop mid-stretch**: press Stop (or switch vessel) while on rails: the warp must come back to 1x at once (`physics warp: the burn was not updated this frame …`), with no burn going on by itself.
7. **Native node by hand** (no pilot): create a node, start the burn by hand, rails warp: the mod must not drop the warp (no `[Warp] the game started a burn under rails warp that we did not ask for`). After cutting the throttle, a coast warp still waits about 1 s (`[Warp] waiting for the engines to stop …`).
8. **The other modes**: "Off (1x)" never warps while the engines push; "Physics (x4)" goes to x2 then x4 as in section 12 and never on rails.

## 19. Retour d'une lune vers sa planète (correctifs du 2026-11-18, Minmus → Kerbin)
Même essai que le 2026-11-18 : orbite de Minmus (n'importe quelle inclinaison), onglet Transfer, cible Kerbin, orbite de 100 km. Sauvegarde rapide avant l'injection.
1. **Repères des corps** : au plan, lire les lignes `[Transfer] body Minmus: … frame turned …° from the root's` (attendu : nettement plus que 0°, de l'ordre de 100°) et `[Transfer] body Kerbin: … frame turned 0.00°` (ou presque).
   - `[Transfer] frame check at UT …` : `tilted chain vs world` doit valoir quelques mètres, alors que `chain vs world (Zup swap)` (l'ancien calcul) donnait 101 266 m autour de Minmus. Si `tilted chain vs world` reste grand, la rotation des repères est fausse : envoyer le log.
2. **Injection** : `[TargetOrbitBurn] accepted: asymptote v∞ … (frame turned …°)`, puis `injection burn done: planned …, actual …`.
   - Juste après, `next SOI change (after the injection burn): ours Minmus -> Kerbin …` ; à la sortie, `state at SOI change Minmus -> Kerbin` doit montrer une orbite **fermée** autour de Kerbin (e < 1), et la correction suivante un `without the correction` proche de 100 km (quelques dizaines de km au plus), pas 37 831 km.
   - Le `frame check can abort: normal component …` peut encore apparaître (orbite de départ inclinée) : sans conséquence si le burn finit `finish (done)`.
3. **Changement de SOI** : plus de `state mismatch after the burn: about … m/s nobody commanded` à la bascule Minmus → Kerbin ; à la place `energy since the injection burn ended: the central body changed (Minmus -> Kerbin), accounting restarted around Kerbin …`.
4. **Sortie de SOI** : plus de `late SOI entry into Minmus` ni de `failure in Escape`. Si notre rayon et celui du jeu diffèrent de quelques mètres : `inside our SOI radius of Minmus but moving away from it: an exit the game has made, no entry awaited`. Puis `phase Escape -> Coast`.
5. **Corrections et capture** : `correction 1: … arrival periapsis predicted 100.xx km (wanted 100.00 …) … arrival UT …` **après** l'heure courante, `converged True`. Puis `capture: periapsis 100… km …` et `done: … final orbit Kerbin: Pe ~100 km, Ap ~100 km, inclination …°` (inclinaison dans le repère de Kerbin, comme le jeu l'affiche).
6. **Si la trajectoire repart quand même de Kerbin** (par exemple en recréant l'essai raté : charger une sauvegarde après une mauvaise sortie, sur une hyperbole qui s'éloigne de Kerbin) :
   - au moment de la correction : `correction n: in the Kerbin SOI past its periapsis, on a path that leaves it: the rest is planned as an orbit at Kerbin (capture first) …`, puis `plan: In the target's SOI, …` et `orbit at Kerbin planned from the current path: 2 step(s) left, … m/s`. Le pilote vole la capture (`Capture in 1 min …`, environ 384 m/s le 2026-11-18 avec l'onglet Orbit) puis l'abaissement à 100 km ;
   - après un **Resume** sur un plan en échec : `resume: already in the Kerbin SOI with no periapsis ahead: CorrectExisting -> Replan (the orbit at Kerbin)`, puis le même plan. Plus jamais `refused: The target is the body you are orbiting.` en boucle.
7. **Cible = corps actuel** : en orbite de Kerbin, choisir Kerbin comme cible avec une autre altitude : le plan s'appelle `In the target's SOI` (comme l'onglet Orbit). Déjà sur l'orbite demandée : « Already in the requested orbit around Kerbin. ».
8. **Non-régression** : un transfert Kerbin → Mun ou Kerbin → Duna doit donner les mêmes lignes qu'avant (`frame turned 0.00°` pour Kerbin et la Mun, `tilted chain vs world` de quelques mètres).
9. **Lift « Prepare transfer » depuis une lune inclinée** (facultatif) : la ligne `[Lift] plan: plane inclination …°` est maintenant dans le repère de la lune. Le choix de l'inclinaison minimale atteignable depuis le site reste calculé dans le repère commun : à surveiller seulement.

## 20. Réglages de chaleur du jeu (onglet Land, mode Propulsive, 2026-11-18)
Les options de difficulté de la campagne (Edit Pack / paramètres de la partie) : « Heat » (HeatEnabled), « Heat part destruction » (HeatPartDestruction), « Heat scaling » (HeatScaling). Même essai que la section 16 : vaisseau en orbite de Kerbin à 100 km, Land → Brake. Sauvegarde rapide avant le départ.
1. **Chaleur désactivée** (HeatEnabled off) :
   - au départ, `[Landing] heat: game heat disabled, difficulty HeatEnabled off: no heat limit on the plan (Δv only), heat guard off` ;
   - `[Landing] heat: model inputs …` finit par `heating not modelled: game heat disabled, …`, et la ligne `start plan` ne contient **aucun** `pre-brake` ;
   - aucun refus « Reentry too hot … », même avec un vaisseau qui était refusé avant (moteurs fragiles, entrée raide) ;
   - pendant la rentrée : aucune ligne `guard on`, la ligne « Hottest Engine » de l'onglet affiche `game: heat disabled, not judged` ;
   - les températures des pièces ne bougent pas (fenêtre thermique de DebugTools si besoin).
2. **Même chose avec le cheat « Disable Thermodynamics »** (chaleur de la difficulté laissée active) : `game heat disabled, cheat Disable Thermodynamics on: …`.
3. **Destruction des pièces désactivée** (HeatPartDestruction off, chaleur active) :
   - `[Landing] heat: game part destruction by heat off, difficulty HeatPartDestruction off: …` ;
   - la ligne `entries:` montre `heating not judged (game difficulty), engine peak … % (…) unbraked` pour chaque entrée, et aucun pré-freinage n'est planifié ;
   - si un moteur dépasserait 100 % : avertissement au départ « Predicted heating … % on the engine …: above its max temperature, but this campaign's difficulty does not destroy parts by heat. », pas de refus ;
   - en rentrée, aucune ligne `guard on` ; le moteur peut dépasser sa température max sans exploser, et le freinage se fait normalement.
4. **Échelle de chaleur** (par exemple 0,5 puis 2, chaleur et destruction actives) : la ligne `model inputs` donne `heat scale 0.5` (ou 2) et `game heat on, part destruction on, heat scaling 0.5`. Le pic prévu (`predicted engine peak …`) doit baisser avec 0,5 et monter avec 2, et rester proche du pic lu en vol (`hottest engine … (peak …)`).
5. **Changement en cours de vol** (si le jeu permet de modifier l'option en vol) : la ligne `[Landing] heat: …` réapparaît avec le nouvel état dans la seconde, et la garde s'arrête (`guard off in …: the game's heat no longer destroys parts`) si elle tournait.
6. **Non-régression** (tout actif, échelle 1) : mêmes lignes qu'à la section 16, limite du plan à **70 %** (gardée par l'utilisateur le 2026-11-18), garde à 75 %, alarme à 90 %.

## 21. Lift « Rendezvous » depuis le pas de tir (4.4)

Préparer une station en orbite circulaire vers 120 km, inclinée d'environ 6° (une inclinaison de 0° ou de 0,03° teste le
cas 4). Lire `Player.log`, lignes `[Lift] rendezvous:` puis `[Rendezvous]`.

1. **Plan sur le pas de tir.** Onglet Lift, objectif Rendezvous, cible la station, « Dock after the rendezvous » coché, Start.
   - Une ligne `planning with …` : Pe et Ap de la cible, `inclination` comparée à `game orbit.inclination` (même valeur, en
     degrés ou en radians : noter l'unité), orbite d'attente 110 km (`10 km under the target's periapsis`), Ap du joueur ignoré.
   - Une ligne par passage (`pass k/n`), puis `chosen pass` : le Δv choisi est le plus bas à 5 m/s près. Noter le temps
     `planned in … ms` (le jeu ne doit pas geler ; dans l'éditeur, environ 0,9 s par passage, 7 s pour 8 passages).
   - Aucun passage dans la première minute après Start.
   - Le tableau affiche le passage, l'azimut, le plan de la cible, le changement de plan restant, l'orbite d'attente et le Δv
     du rendez-vous. Le réglage Ap du profil n'a pas changé après la course.
2. **Attente et lancement.** Warp jusqu'à T-30 s, gaz à 0 pendant l'attente, premier étage tiré à T-0 (comme la section 6).
   La ligne `plane error` décroît pendant la montée.
3. **Passage manqué.** Sur une autre tentative, warper soi-même au-delà de l'heure de lancement : la ligne
   `re-planning every pass` apparaît, le warp revient à 1×, et un nouveau passage est choisi, jamais un lancement en retard.
   Si le premier étage avait déjà été tiré, il ne l'est pas une seconde fois.
4. **Plan hors d'atteinte.** Station à 0° (ou 0,03°) d'inclinaison depuis le KSC : `plane change` d'environ 0,1° (quelques m/s)
   affiché avant le lancement, des passages espacés d'environ un douzième de l'orbite de la station (pas un par jour), puis
   un `PlaneMatch` au début du plan `[Rendezvous]`.
5. **Cible haute (plafond de 150 km).** Station vers 400 km : orbite d'attente à 230 km
   (`capped at the low orbit + 150 km`), le phasage du pilote Rendezvous monte le reste. Le Δv affiché le comprend.
6. **Passage de relais.** Après la circularisation, sans trim Lift : ligne `in orbit … handing over`, puis
   `hand-over started`. Comparer la phase réelle à la phase prédite (`phase … (predicted …)`). L'onglet Rendezvous montre le
   plan (plane match éventuel, phasage, interception) puis l'amarrage (sections 9 et 10).
7. **Arrêt dans la chaîne.** Arrêter Lift pendant l'attente : message « stopped during the pad wait … Left to do: … ».
   Supprimer ou récupérer la cible pendant la montée : au relais, `hand-over refused: the target vessel no longer exists`,
   aucun burn lancé, warp à 1×, conseil « choose a target in the Rendezvous tab ».
8. **Cible refusée.** Une cible posée au sol, ou en orbite autour de la Mun : refus immédiat avec le message, rien n'est lancé.
9. **Les autres objectifs.** Un Lift « Orbit » et un « Prepare transfer » se déroulent comme avant (sections 6 et 7).
