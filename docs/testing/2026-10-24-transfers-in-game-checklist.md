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

## 22. Transfert + rendez-vous + amarrage vers une cible autour d'un autre corps (4.5)

Préparer une station en orbite circulaire d'environ 200 km autour de la Mun (puis, si possible, autour de Minmus, dont le
repère est tourné), inclinée d'environ 20°, avec un port d'amarrage libre. Le vaisseau part d'une orbite basse de Kerbin avec
un port compatible. Sauvegarde rapide avant chaque essai. Lire `Player.log`, lignes `[Transfer] chain:`, `[Transfer]` puis
`[Rendezvous]` et `[Docking]`.

1. **Plan.** Onglet Rendezvous, choisir la station : le texte dit qu'elle orbite la Mun, seuls les boutons « Transfer +
   rendezvous + dock » et « Transfer + rendezvous » sont actifs. Appuyer sur le premier.
   - Ligne `planning transfer + rendezvous` : Pe, Ap, `inclination` comparée à `game orbit.inclination` (noter l'unité),
     orbite d'attente 190 km.
   - Lignes `arrival asymptote` (source `propagated`), `rendezvous estimate` (12 phases, fourchette de Δv, temps en ms :
     le jeu ne doit pas geler ; environ 10 s dans l'éditeur, barre de progression « Planning ») et `total … = transfer … +
     rendezvous … + docking reserve 10`.
   - Le tableau du plan montre chaque étape du transfert, « Plane match left » si plus de 0,05°, « Rendezvous … (planned at
     the arrival) » avec « up to … m/s », « Docking », puis le total et la marge.
2. **Refus faute de Δv.** Avec un vaisseau trop juste pour le rendez-vous mais assez pour le transfert : refus « Not enough
   Δv … Per stage … », aucun burn.
3. **Corrections.** À chaque correction, une ligne `target plane re-read` et `plane error predicted … °` dans la ligne de
   correction. Comparer à `plane left after the arrival correction`.
4. **Arrivée et relais.** Après la capture : `in orbit … plane error to … °` (proche de la prévision), puis `hand-over
   started`. Le pilote Rendezvous planifie depuis la phase réelle (`[Rendezvous]`, plane match éventuel en premier), puis
   l'amarrage (sections 9 et 10). Comparer le Δv du rendez-vous réel à la fourchette prévue.
5. **Prepare only.** Même essai avec Prepare only : un nœud à la fois pour le transfert, puis pour le rendez-vous ;
   l'amarrage reste piloté. Le bouton « Transfer + rendezvous » s'arrête après le rendez-vous, sans amarrage.
6. **Arrêt et reprise.** Stop pendant la croisière : message « stopped during … Left to do: press Resume … ». Recharger la
   sauvegarde rapide : l'onglet Rendezvous propose de reprendre (« … Then the rendezvous with … »). Resume : le transfert
   reprend (`resume: target found`) et le relais a lieu à l'arrivée.
7. **Cible perdue.** Pendant la croisière, récupérer ou supprimer la station : le transfert continue jusqu'à l'orbite
   d'attente (`not readable … stored plane kept`), puis `hand-over refused` avec la raison, aucun burn, warp à 1×.
8. **Cibles refusées.** Une cible posée, une cible autour du Soleil, une cible autour de notre propre corps (ce sont alors
   les boutons de la section 9) : refus immédiat avec le message.
9. **Repère tourné.** Station autour de Minmus : `plane error to …` à l'arrivée du même ordre que la prévision (pas
   d'écart de dizaines de degrés).
10. **Correctifs 4.4.** Lift « Rendezvous » : une cible refusée sur le pas de tir dit « Nothing was launched » ; quitter le
    vol pendant l'attente donne « the scene changed (flight left) » ; lancer le plan sous warp : ligne `warp set to 1x
    while the passes are planned`.

## 23. Assistance gravitationnelle (4.6)

Onglet Transfer, case « Use gravity assist » cochée. Sauvegarde rapide avant chaque essai. Lire `Player.log`, lignes
`[Transfer] assist:` et `[Transfer]`.

Note : sur le système de test du mod (copie du système de série), aucun survol d'Eve ne fait gagner assez vers Jool
(2 % au mieux) ; le cas de référence est **Kerbin → Moho via Eve** (16 à 18 % gagnés). Commencer par lui.

1. **Moho via Eve.** Depuis une orbite basse de Kerbin, cible Moho, Orbit, Plan.
   - Lignes `direct …`, `grid via …` (une par corps, nombre de Lambert, durée en ms), `built via …`, puis `offer: …` avec
     la durée totale. Le jeu ne doit pas geler pendant la recherche (barre « Searching ») ; noter la durée totale.
   - Si une assistance est proposée : deux lignes dans l'onglet, « Direct: … » et « Via … », la moins chère marquée ●.
     Cliquer l'autre : le tableau et le total changent, ligne `chosen … by the player`.
   - Sinon : une ligne « Direct: … — no gravity assist saves 5 % … » ; noter le meilleur pourcentage du log.
2. **Vol via Eve.** Start sur le plan « Via Eve ».
   - Les corrections avant le survol : ligne `correction n` avec `plane error predicted` ; périapside visé = celui de
     l'étape « Flyby (Eve) ».
   - Entrée dans la SOI d'Eve : `leg … starts in Eve: flyby, periapsis … km predicted (planned …)`, toujours au-dessus de
     l'atmosphère (plancher 100 km).
   - Au périapside : `flyby of Eve: … burn … m/s` (ou `no burn`), poussée en 1x, sans warp.
   - Après : `flyby of Eve done: excess out … m/s, …° from the wanted one` (attendu : moins de 1°), puis les corrections
     vers Moho ; Δv réel des corrections comparé à la réserve.
   - Arrivée : capture à Moho comme en section 1 à 6 ; comparer le Δv total réel au plan.
3. **Jool via Eve.** Même essai, cible Jool : noter si « Via Eve » est proposé (non attendu sur le système de test) et le
   pourcentage de la ligne `offer:`.
4. **Départ via la Mun.** Depuis une orbite basse de Kerbin, cible Duna : si « Via Mun » est proposé, le voler. Lignes
   `departure via Mun`, injection vers la Mun, survol, burn de re-visée dans la SOI de Kerbin, sortie avec l'excès voulu
   (`SOI exit … mismatch`). Sur le système de test, la Mun ne fait gagner qu'environ 3 % : elle n'est pas proposée.
5. **Pas d'assistance utile.** Cible la Mun ou Minmus : aucune ligne « Via … », aucune ligne `grid via`.
6. **Durée max.** Mettre une durée max entre la durée du direct et celle de l'assistance : l'assistance n'est plus proposée
   (« longer than the maximum trip »).
7. **Reprise.** Sauvegarder pendant la croisière vers Eve, recharger, Resume : `resume of an assist via Eve: route
   BeforeFlyby`, puis le survol a lieu. Recommencer dans la SOI d'Eve avant le périapside (`InFlybySoi`) et après le
   survol (`AfterFlyby` : plan sans assistance, message clair).
8. **Prepare only.** Même plan avec Prepare only : un nœud à la fois, dont un nœud au périapside d'Eve si le survol est
   propulsé.
9. **Transfer + rendezvous.** Case cochée, station autour de Moho : le tableau de l'onglet Rendezvous montre l'étape
   « Flyby » si l'assistance a été retenue ; sinon le plan direct.
10. **Option décochée.** Refaire le plan Kerbin → Duna : le plan et les lignes du log sont ceux d'avant 4.6.

## 24. Reprises de K2-D2 1.4.0 / 1.4.1 (2026-11-19)

Plan : `docs/superpowers/plans/2026-11-19-upstream-k2d2-merge.md`. Rien de cette section n'a été essayé en jeu. Lire
`Player.log`.

1. **Mode photo.** Pendant un atterrissage ou un burn, passer en mode photo : le pilote continue, la fenêtre se cache puis
   revient. Quitter vers le menu principal : les pilotes s'arrêtent.
2. **Node sans moteur allumé.** Start refusé avec « No active engine - stage or switch one on. », ligne
   `[NodeExPilot] start refused: no active engine`. Si les moteurs s'éteignent pendant le burn :
   `[BurnManeuver] no active engine: full throttle…`.
3. **Crash avec moteurs.** Au plus une ligne `[BurndV] an engine of … could not be read (destroyed?): skipped` par
   vaisseau, aucune exception.
4. **Auto-staging avec un SRB vide resté attaché.** Une seule ligne
   `[Staging] a lit engine is out of propellant: next stage (n spent engine(s)…)` par vrai largage, jamais une cascade
   (parachutes compris).
5. **Circularize garde les nœuds suivants.** Créer un nœud après l'apoapside, puis Circularize :
   `[Circularize] nodes: removed a up to the end of the burn, kept b after it`, le nœud suivant est toujours là.
6. **Gizmo du nœud.** Un nœud créé en vue de vol a son gizmo quand on ouvre la carte, sans ligne
   `[ManeuverCreator] map gizmo update failed`.
7. **Lift Orbit, « Circularize Once Ascent Complete » décoché.** Fin après l'Adjust :
   `[Lift] ascent complete without auto-circularize (pause on|off)`, pause si le réglage est coché. Avec le goal Prepare
   transfer ou Rendezvous, la case est cachée et la circularisation a lieu.
8. **Coasting.** Warp d'un cran plus bas qu'avant, sans dépasser le haut de l'atmosphère ; avec Coasting Warp décoché,
   la croisière a lieu sans warp et atteint l'apoapside visée. Après un programme de roulis, le maintien prograde ne
   lutte plus contre un SAS verrouillé.
9. **Attitude.** Les valeurs d'élévation et de cap d'avant sont gardées (lues une fois depuis `warp.*`).
10. **Land : Max rotation.** Le virage avant le freinage respecte le curseur (défaut 10° ; avant, 30° toujours).
11. **Land : posé détecté.** `[Landing] standing still with the engines throttled down`, puis
    `touchdown in …: standing still …` à l'arrêt ; jamais pendant un vol stationnaire.
12. **Land : Stop.** `[Landing] stop: SAS target released to the current attitude`.
13. **Land : Precision (vide) depuis une orbite trop haute.** La case reste cochée, le texte dessous dit pourquoi, Start
    refuse avec la même raison.
14. **Land : jambes.** `[Landing] gear: … Gear group has N part(s)`, comparé à notre compte de jambes.
15. **Land : hauteurs.** `[Landing] altitude: in <phase>: approx …, grounded …` sur le pas de tir et juste avant le posé.
16. **Land : waypoint déplacé.** Choisir un de ses waypoints, le déplacer dans la fenêtre Waypoints de Redux :
    `[Landing] target: waypoint '…' moved from … the target follows`. Si la ligne n'apparaît jamais, le champ `Id` des
    enregistrements de Redux n'existe pas en 26w46a.
17. **Prédiction d'impact de Redux.** `[Landing] redux impact: …` (distance entre leur point et le nôtre), et une fois
    par atterrissage `[Landing] spin: <corps>: … -> agrees/DISAGREES/not compared`. Noter les écarts.
18. **Récupération d'étages.** `[StageRecovery] at landing start: …`, puis les changements de phase.
19. **Freinage dans l'air** (profil Atmo, Propulsive, sans précision) : depuis un saut suborbital ou un booster qui
    retombe vers 5–15 km, allumage plus tard que le départ « vide », un seul burn, relais près de 50 m :
    `[Landing] air timing: …`, `[LandingBurn] braking at …`, `[LandingBurn] handover to Touch Down at … m`.
20. **Gravity Turn** (Lift, goal Orbit, profil Gravity Turn) : `[Lift] start: goal Orbit, ascent profile GravityTurn`,
    `[GravityTurn] Planning at …`, `Auto plan …`, une ligne `[GravityTurn] +Ns …` toutes les 5 s, `Ascent done at …`,
    puis Coasting, Adjust, Circularize normaux. Surveiller un vol à plat vers 66 km à gaz minimum (vu sur la fusée de
    test). Avec Prepare transfer ou Rendezvous : `Gravity Turn flies Orbit launches only for now; using Guided`.
21. **Fenêtre.** Elle suit l'échelle d'UI du jeu (fenêtre VAB aussi) ; la poignée de Redux redimensionne en largeur et
    en hauteur ; la taille et la position reviennent après redémarrage (`WindowSizeSaver: window size saved WxH`) ; un
    clic droit tourne la caméra sans bouger la fenêtre ; un simple clic ne l'envoie plus dans le coin au lancement
    suivant ; le titre affiche le nom du mod (sinon chercher `has no Title property`).

## 25. SkySpaceAgency et K2-D2 installés ensemble (2026-11-19)

Relevé des collisions : `docs/superpowers/notes/2026-11-19-coexistence-with-k2d2.md`. Installer K2-D2 1.4.1 (dossier
`mods/K2D2`) **et** SkySpaceAgency, activer les deux dans la même campagne.

1. Les deux fenêtres s'ouvrent, chacune avec son contenu. Aucune ligne
   `[Features] More than one enabled feature provides 'Assets/UI/…'`, ni
   `another AssetBundle with the same files is already loaded`, ni `Failed to load UXML`.
2. **Raccourcis.** `[Plugin] window shortcut: LeftAlt+Y` au démarrage. Alt+Y ouvre seulement SkySpaceAgency, Alt+O
   seulement K2-D2 ; Alt+Y ne trime pas, ne warpe pas, ne déclenche pas le RCS. Le raccourci apparaît dans les réglages
   de mods de Redux et se change ; `SkySpaceAgency-config.json` apparaît dans le dossier du mod.
3. **FlightPlan.** Le bouton Execute de FlightPlanRedux pilote K2-D2 (lignes de K2-D2), pas nous (aucune ligne
   `[NodeEx]` de SkySpaceAgency).
4. **Un seul activé.** Chaque mod activé seul dans une campagne, l'autre installé mais désactivé : celui qui est
   désactivé n'a pas de bouton et ne fait rien ; l'autre marche.
5. **Réglages séparés.** `mods/SkySpaceAgency/skyspaceagency_*.json` et les `k2d2_*.json` de K2-D2 ne changent que
   quand leur propre mod change un réglage.
6. **Deux pilotes à la fois** sur le même vaisseau : ils se battent (attendu, non corrigé). Le chien de garde de warp de
   SkySpaceAgency ne coupe pas un burn de K2-D2 (aucune ligne
   `[Warp] the game started a burn under rails warp that we did not ask for` pendant que K2-D2 vole).
7. Refaire 1 à 5 avec K2-D2 1.5.0 (branche de la PR) quand elle existera.

## 26. Nouvelle interface, étape 1 : les fondations (2026-11-19, aperçu)

Plan `docs/superpowers/plans/2026-11-19-new-ui-foundations.md`. Captures de référence (éditeur) :
`docs/design/ui/foundations/`. Rien ne vole depuis la nouvelle fenêtre : seul l'affichage est à vérifier.

1. **Rien ne change sans clic.** La fenêtre classique, ses onglets et Alt+Y marchent comme avant. Seul nouveau : le
   bouton « New UI (preview) » en bas de la fenêtre classique, entre « Auto-staging » et « i », qui ne déborde pas.
2. **Ouverture.** Le bouton ouvre la nouvelle fenêtre (un second clic la ferme), en vol et en vue carte seulement.
   Log : `AppWindow: built, scale 100 % on the game's …`. Sinon chercher `New window not created yet` ou
   `Failed to load UXML from Addressables at 'Assets/UI/SkySpaceAgency_UI/App/SkySpaceAgency_App.uxml'`.
3. **Aspect.** Comparer à `docs/design/ui/lift.png` et à `docs/design/ui/foundations/lift_mockup_size.png` : fond bleu
   nuit en dégradé, police Noto Sans (pas la police à chasse fixe de la fenêtre classique), onglet LIFT doré souligné,
   badge PREVIEW, frise des phases, bouton « Running… » doré. Le relief de vol (courbe cyan pointillée, trajectoire
   dorée, fusée) est dessiné. Noter tout carré blanc, texte manquant ou police différente (la police vient du paquet
   du SDK : à vérifier dans le jeu).
4. **Déplacement.** Glisser par l'en-tête déplace la fenêtre ; glisser dans le corps (curseurs, champs) ne la déplace
   pas. Un clic droit ne la déplace pas.
5. **Redimensionnement.** La poignée en bas à droite agrandit et réduit en largeur et en hauteur, jamais sous 760 x 480 ;
   la vue centrale suit la taille ; sous 1100 px de large, la vue passe sous les deux colonnes et les onglets se
   resserrent. Log `WindowSizeSaver: window size saved WxH`.
6. **Mémoire.** Fermer, rouvrir, puis relancer le jeu : position, taille et onglet reviennent.
7. **Échelle.** Onglet SETTINGS, « Interface scale » : 75 à 150 % par pas de 5 %, effet immédiat sur la nouvelle
   fenêtre seulement (log `AppWindow: panel scale X (game Y x ours Z %)`). Changer ensuite l'échelle d'UI du jeu, puis
   la résolution : la nouvelle fenêtre suit (produit des deux) et reste à l'écran ; la fenêtre classique ne suit que
   l'échelle du jeu.
8. **Réduire.** Le bouton « – » ne garde que l'en-tête ; un second clic rend la hauteur d'avant, et la taille réduite
   n'est pas enregistrée.
9. **Scènes.** Quitter le vol (VAB, KSC, menu) ferme la nouvelle fenêtre ; elle ne se rouvre pas toute seule. F2 la
   masque avec le reste de l'interface.
10. **Champs.** Taper dans un champ de l'onglet LIFT (« Target altitude ») ne déclenche aucune commande du jeu.
11. Onglet « Orbit » (ou un autre onglet de vol) : le message « moves to this window in step 2 », et « Classic window »
    ouvre la fenêtre classique.

## 27. Nouvelle interface, étape 2 : tous les onglets (2026-11-19)

Plan `docs/superpowers/plans/2026-11-19-new-ui-tab-migration.md`, inventaire `docs/superpowers/notes/new-ui-inventory/`
(chaque fichier finit par une liste « Not verified in game » à reprendre ici onglet par onglet). Captures de référence :
`docs/design/ui/migration/`. La section 26 (fenêtre, déplacement, taille, échelle) reste à faire aussi.

1. **Bascule.** Alt+Y et le bouton d'AppBar ouvrent la nouvelle fenêtre ; le bouton d'AppBar reste allumé tant qu'elle
   est ouverte. SETTINGS > Interface > « Classic UI » : Alt+Y ouvre l'ancienne fenêtre, jamais les deux à la fois ; son
   bouton « New UI » revient à la nouvelle. Une erreur d'onglet n'apparaît qu'une fois dans le log
   (`AppWindow: tab '…' update failed`) : n'en voir aucune.
2. **En-tête.** Puces : nom du vaisseau (point vert pendant un vol de pilote), « corps · situation », Δv, TWR.
3. **Onglet allumé.** L'onglet du pilote qui vole s'affiche en vert.
4. **NODES.** Liste des nœuds réels, Circularize AP/PE, Start/Running…, Abort, pause en fin, sections Node info et
   Advanced (Reset), navball (sens du repère à vérifier), carte « Hold attitude » (élévation, cap, Hold/Release).
5. **ORBIT.** Vue de dessus : orbite actuelle cyan, orbite visée dorée, burns bleus ; Create nodes puis Start/Stop ;
   capture depuis un survol ; frise des étapes.
6. **LIFT.** Tous les buts (Orbit, Prepare transfer, Rendezvous), profils Guided et Gravity Turn ; courbe prévue et
   trajectoire volée ; tuiles et barres d'étages ; statuts du pilote dans la pastille.
7. **LAND.** Profil atmosphère / vide qui suit le corps, modes, cible (liste, carte, coordonnées, « Here »), étage
   choisi, précision, Apply du coefficient de traînée, profil de descente, chaleur, Brake/Land.
8. **TRANSFER.** Tuiles de destination, arrivée, Plan / Re-plan / Cancel search, choix Direct / Via, Start, Resume,
   Cancel saved plan, carte (sens de l'arc, angle de phase), budget Δv.
9. **RENDEZVOUS.** Liste des cibles avec distances, mode Rendezvous / + Dock / Dock only, ports et « Change », Prepare
   only, chaîne vers un autre corps, vue d'approche, CHECKS, tuile RCS LEFT (lecture du MonoPropellant à vérifier).
10. **SETTINGS.** Échelle (section 26), Classic UI, barre compacte, auto-staging, warp des burns, debug, Reset All (deux
    clics), crédits complets, version.
11. **RETURN.** Page « planned », contrôles grisés, badge PLANNED dans la barre d'onglets.
12. **Barre compacte.** Fenêtre fermée pendant un vol : la barre apparaît en haut (pilote, phase, valeurs, Stop, ouvrir
    la fenêtre) ; corail quand la garde de chauffe agit ; disparaît à la fin du vol ou si on la coupe dans SETTINGS.
13. **Réglages partagés.** Changer un réglage dans la nouvelle fenêtre puis passer en Classic UI : l'ancienne montre la
    même valeur (et l'inverse). Relancer le jeu : les valeurs restent.
14. **Fenêtre basse.** À 760 x 480, la frise défile avec les colonnes ; la barre d'actions reste visible.

## 28. Fenêtre réduite : Scale / Reflow (2026-11-20)

Réglage SETTINGS > Interface > « Small window » (Scale par défaut, Reflow = comportement d'avant). Captures de référence :
`docs/design/ui/migration/small_*`.

1. **Scale.** Réduire la fenêtre avec la poignée (jusqu'à 760 x 480) : les trois colonnes, la frise et la barre d'actions
   restent comme à la grande taille, tout rétrécit sans défilement ; jamais en dessous de la moitié ; texte encore lisible
   à la taille minimale. La poignée suit bien la souris (la fenêtre garde sa vraie taille) ; le déplacement par l'en-tête
   marche à toute échelle.
2. **Commandes réduites.** Curseurs, champs, interrupteurs, boutons, liste déroulante et Start/Stop restent utilisables
   quand le contenu est réduit (clic au bon endroit). Les listes déroulantes s'ouvrent à taille normale (attendu).
3. **Reflow.** Passer à Reflow : sous 1100 px les colonnes se réorganisent et la page défile, comme avant.
4. **Bascule en direct.** Changer le choix fenêtre ouverte : la mise en page change tout de suite, sans fermer.
5. **Combinaison.** Avec l'échelle d'interface (75 à 150 %) et l'échelle d'UI du jeu : la fenêtre reste à l'écran et le
   contenu la remplit.
6. **Réduire (« – »).** L'en-tête seul, à taille normale ; la restauration rend l'échelle d'avant. La taille enregistrée
   reste celle de la fenêtre (relancer le jeu : même taille, même échelle).
7. **Défaut.** Un joueur sans réglage enregistré a Scale (fichier de réglages sans `app_small_window`).
