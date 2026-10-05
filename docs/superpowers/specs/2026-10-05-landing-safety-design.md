# Atterrissage — sous-projet 1 : sécurité du freinage — conception

Date : 2026-10-05 · Statut : validée en discussion, en attente de relecture
Source : audits `docs/audits/2026-10-05-landing-physics-audit.md` (F1, F2, F5, F22, F23, F24, F25)
et `docs/audits/2026-10-05-landing-tab-audit.md`.

## Objectif

Qu'un vaisseau ne puisse plus s'écraser sans prévenir : l'autopilote refuse un atterrissage
impossible, allume au bon moment même sur une approche rasante, et ne descend jamais plus vite
que ce que ses moteurs peuvent arrêter.

## Découpage du chantier atterrissage

1. **Sécurité du freinage** — ce document.
2. Fin de descente : trains d'atterrissage, annulation de la dérive latérale, détection du
   contact et coupure (audit F18–F20).
3. Précision : viser le vrai point d'arrêt, rotation du corps sans facteur empirique 1,307
   (audit F11–F14).

## Hors périmètre

- Trains d'atterrissage, dérive, coupure au contact, SAS après l'atterrissage (sous-projet 2).
- Point visé par l'atterrissage de précision et facteur 1,307 (sous-projet 3).
- Traînée atmosphérique, parachutes, poussée variable avec la pression. La simulation ignore
  la traînée : c'est prudent, la traînée aide au freinage.
- Auto-staging pendant le freinage.
- Aucun nouveau réglage joueur.

## Comportement attendu

### Vérification de faisabilité au démarrage

Au clic sur Start (Brake ou Touch Down), les contrôles passent dans l'ordre du tableau. Au premier
échec, l'autopilote **ne démarre pas** et le bouton revient à l'arrêt. Le message reste affiché
dans l'onglet jusqu'au prochain démarrage réussi.

| Contrôle | Condition d'échec | Message |
|---|---|---|
| Moteur actif | poussée active nulle | `No active engine: stage or activate your engines first.` |
| Poussée locale | `η·a ≤ 1,05·g_surface`, avec `a = F_active / m`, `g_surface = μ / R²`, `η = 0,85` | `Local TWR {a/g_surface:0.00}: too low to stop safely.` |
| Freinage possible | même un allumage immédiat de la simulation s'écrase | `Cannot stop before the ground with the current thrust.` |
| Δv | `1,10 · Δv_nécessaire > Δv_restant` (`VesselDeltaVComponent.TotalDeltaVActual`) | `Δv {restant:0} m/s for ~{nécessaire:0} m/s needed.` |

Si `TotalDeltaVActual` n'est pas disponible (≤ 0 ou NaN alors qu'un moteur est actif), le
contrôle Δv est sauté. L'onglet affiche alors l'avertissement non bloquant
`Δv unknown: check your fuel.`

Le seuil de poussée (1,05 / 0,85, soit un TWR local d'environ 1,24) garde une marge fixe, comme
l'utilisateur l'a choisi.

### Pendant la descente

- Si `|v_sol|² > 2·(a − g)·h`, le vaisseau ne peut plus s'arrêter, même à pleine poussée.
  L'autopilote continue de freiner au maximum et affiche `Cannot stop before the ground!`.
- Si la poussée active est nulle (`a ≈ 0`), la manette passe à 0 (jamais NaN) et la même alerte
  s'affiche.

### Une phase qui échoue arrête l'autopilote

Circularize (« orbite trop haute ») et DeorbitBurn (« pas de fenêtre », « pas de vaisseau »)
signalent un **échec avec sa raison** au lieu d'un simple `finished`. LandingPilot arrête
alors l'autopilote et affiche la raison de façon persistante. Une MidCourseCorrection
« négligeable / ignorée » n'est pas un échec.

## Moteurs actifs (`BurndV`)

- **Moteurs comptés** : seulement ceux qui vérifient `Engine.EngineIgnited && Engine.IsOperational
  && !Engine.IsPropellantStarved`.
- **Poussée max** : calculée comme aujourd'hui (`MaxThrustOutputAtm` / `MaxThrustOutputVac`, 0 si
  `RequiresAir` dans le vide).
- **Isp combinée** : `Isp = ΣF / Σ(F / IspActual)` sur les moteurs actifs, 0 si elle est inconnue.
- **Unités** : poussée en kN, masse en t (`VesselComponent.totalMass`), donc `a = F/m` en m/s²,
  comme le code actuel.
- **Rien d'actif** : `full_dv`, qui est une accélération malgré son nom, vaut 0, jamais NaN ni
  infini.

## Rotation du corps

`ω = s · (2π / rotationPeriod) · ẑ` dans le repère Zup relatif au corps, avec `s = ±1`.

- Pour chaque signe, on prédit la vitesse sol `|v − ω×r|`. On garde le signe dont la prédiction
  est la plus proche de la vitesse sol mesurée par le jeu (`SurfaceSpeed`).
- Si les deux prédictions diffèrent de moins de 1 m/s (rotation lente, orbite polaire), on garde
  `s = +1`.
- Le signe retenu est logué une fois par corps.

## Simulation du freinage (`BrakeSimulator`)

Ce sont des maths pures, sans accès au jeu, testées dans l'éditeur comme `OrbitPlanner`.

**Entrées** :
- `r0` et `v0` à `ut0` (repère Zup relatif au corps, inertiel) ;
- `μ`, `R` et `ω` ;
- poussée `F` (kN), masse `m0` (t), `Isp` (s ; 0 = masse constante) ;
- `η = 0,85` ;
- altitude de sécurité `h_safe = 50 m` et vitesse d'arrêt `v_stop = 1 m/s` ;
- une fonction `TerrainHeight(direction fixe au corps)`, qui renvoie la hauteur du relief
  au-dessus de R (m) ;
- `t_impact`, l'instant de l'impact sans poussée, fourni par l'appelant.

**Trajectoire pour un allumage à `t_s`** :
1. Croisière de `ut0` à `t_s` par `KeplerPropagator`.
2. Freinage intégré en RK4, pas de 0,5 s :
   `accel = −μ·r/|r|³ + (η·F/m)·(−v_sol/|v_sol|)`, `v_sol = v − ω×r`,
   `ṁ = η·F / (Isp·g0)` avec `g0 = 9,80665` (si `Isp > 0`).
3. Altitude au-dessus du relief : `|r| − R − TerrainHeight(d)`. La direction `d` est la position
   ramenée dans l'orientation du corps à `ut0`, par une rotation de `−s·|ω|·(t − ut0)` autour
   de ẑ. Le relief est lu toutes les 2 s simulées, et à chaque pas quand on est à moins de 5 km
   au-dessus de `R`.
4. Fin de la simulation :
   - `|v_sol| ≤ v_stop` → **arrêt**, et l'altitude d'arrêt est l'altitude au-dessus du relief ;
   - altitude ≤ 0 → **crash** ;
   - plus de 3 600 s simulées → échec.

**Recherche de l'allumage** sur `[ut0, t_impact]` :
- si l'allumage immédiat s'écrase : **impossible** ;
- s'il s'arrête sous `h_safe` : **trop tard**, il faut allumer maintenant ;
- sinon, dichotomie sur `altitude_d'arrêt(t_s) − h_safe`, que l'on suppose décroissante en `t_s`.
  Précision 0,25 s, 30 itérations au plus.
  - Un point intermédiaire qui s'écrase compte comme « trop tard » : la recherche se resserre
    vers `ut0`.
  - On garde toujours l'allumage le plus tardif **vérifié** sûr.

**Sorties** :
- `Status` : Ok, TooLate ou Impossible ;
- `StartUT` et `BurnDuration` ;
- `DeltaVNeeded = ∫ η·F/m dt` ;
- `StopAltitude` et `StopPosition`.

**Cadence** : la simulation est recalculée toutes les 0,5 s, avec son résultat en cache, pendant
les phases qui utilisent l'heure d'allumage. Le calcul actuel, lui, tourne à chaque frame.

## Utilisation par LandingPilot

- `startBurn_UT`, `burn_duration` et l'affichage « Start Burn In » viennent de la simulation.
- `startSafeWarp_UT` reste `startBurn_UT − rotation_warp_duration`.
- Le réglage joueur « burn before » (`settings.burn_before`, 0 à 10 s, 0 par défaut) reste une
  marge en plus : `startBurn_UT = StartUT simulé − burn_before`. En mode précision, les marges
  de correction que le code ajoute à `burn_before` restent dans le « calcul actuel » ci-dessous.
- **Mode précision** : `startBurn_UT = min(simulation, calcul actuel)`. On prend le plus tôt des
  deux, pour ne rien dégrader avant le sous-projet 3.
- La recherche de collision actuelle (`compute_real_collision`) reste la source de `t_impact`,
  de la latitude et de la longitude prévues, et de `collision_detected`.
- La fonction de relief passée au simulateur réutilise l'échantillonnage existant
  (`body.GetAltitudeFromTerrain`, avec la même conversion de repère que
  `LandingPilot.cs:422-428`).

## Limite de vitesse (`DescentEnvelope`)

`v_max(h) = min( √(2·max(η·a − g, 0)·max(h, 0)) + v_td ,  h·ratio/10 + v_td )`

| Symbole | Sens |
|---|---|
| `a` | accélération active courante |
| `g` | gravité locale courante |
| `h` | altitude au-dessus du sol, celle que le code utilise déjà |
| `v_td` | réglage « Touch-Down speed » |
| `ratio` | réglage « Altitude/speed ratio » |

- Cette formule remplace `LandingSettings.compute_limit_speed` partout où il sert (Brake en
  précision, TouchDown).
- Comme aujourd'hui, elle s'applique à la vitesse sol totale.
- Le profil du joueur reste un plafond : les réglages ne peuvent plus que ralentir la descente.

## Architecture

| Fichier | Rôle |
|---|---|
| `Code/Pilots/Landing/Braking/BrakeSimulator.cs` | Simulation et recherche de l'allumage (pur). |
| `Code/Pilots/Landing/Braking/DescentEnvelope.cs` | Limite de vitesse (pur). |
| `Code/Pilots/Landing/Braking/LandingFeasibility.cs` | Contrôles de départ et messages (pur). |
| `Code/Pilots/Landing/Braking/BodyRotation.cs` | Choix du signe de rotation (pur). |
| `Code/KSPService/BurndV.cs` | Filtre des moteurs actifs, Isp combinée, pas de NaN. |
| `Code/Pilots/Landing/LandingPilot.cs` | Simulation à 2 Hz, faisabilité dans le setter `isRunning`, arrêt sur échec de phase, erreur persistante. |
| `Code/Pilots/Landing/Controlers/TouchDown.cs` | Limite de vitesse, garde poussée nulle, alerte « Cannot stop ». |
| `Circularize.cs`, `DeorbitBurn.cs`, contrôleur de base | État « échoué » avec raison. |
| `Code/Pilots/Landing/LandingUI.cs` | Affichage persistant de l'erreur (non effacé par `status_bar.Reset()`), bouton remis à l'arrêt sur refus. |
| `Assets/Tests/Landing/` (+ `.asmdef`) | Tests EditMode des quatre unités pures. |

## Tests

### Automatiques (EditMode)

Les tests utilisent deux corps : Mun (`R = 200 km`, `μ = 6,5138e10`) et Kerbin (`R = 600 km`,
`μ = 3,5316e12`). Le relief est défini par une fonction : sphère lisse ou plateau.

1. **Chute verticale**, sans rotation, Isp 0 :
   - la distance d'arrêt simulée vaut `v²/(2(η·a − g))` à 2 % près ;
   - l'allumage trouvé s'arrête à `50 ± 5 m`.
2. **Approche rasante** (Mun, orbite 15 km, périapside à −2 km, TWR 2) : arrêt à 50 m ou plus
   au-dessus du relief, avec `|v_sol| ≤ v_stop`.
3. **Poussée insuffisante** (`η·a ≤ g`) : `Impossible`.
4. **Perte de masse** (Isp 300 s) :
   - l'allumage est plus tardif qu'à masse constante ;
   - `DeltaVNeeded` égale `Isp·g0·ln(m0/m_fin)` à 1 % près.
5. **Plateau de 3 km** sur la trajectoire : arrêt à 50 m ou plus au-dessus du plateau.
6. **Rotation** :
   - une orbite rétrograde donne un freinage plus long qu'une orbite prograde ;
   - `|v_sol|` final ≤ `v_stop`.
7. **`DescentEnvelope`** :
   - valeurs connues ;
   - jamais au-dessus du profil joueur ;
   - `η·a ≤ g` → `v_td`.
8. **`LandingFeasibility`** :
   - chaque refus et le cas OK, messages compris ;
   - Δv inconnu → avertissement sans refus.
9. **`BodyRotation`** :
   - retrouve le signe à partir d'une vitesse sol mesurée simulée ;
   - écart < 1 m/s → `+1`.

### En jeu (par le joueur)

1. Départ sans moteur activé : refus, message persistant.
2. Limiteur de poussée à ~40 % : refus « Local TWR … too low ».
3. Mun sans précision, orbite basse de 15 km : « Start Burn In » plausible, atterrissage doux.
4. Atterrisseur à deux étages : la poussée comptée est celle de l'étage actif seulement.
5. Précision activée : aussi bien qu'avant.
6. Log : signe de rotation retenu et résultats de simulation (allumage, Δv, altitude d'arrêt).

## Risques connus

- Sens réel de `EngineIgnited`, `IsOperational` et `IsPropellantStarved` sur des moteurs pas
  encore activés. À confirmer en jeu (scénario 4).
- Unités de la poussée et de la masse : kN et t, d'après le code existant.
- Hauteur du relief renvoyée par le jeu depuis 26w40a.
- La dichotomie suppose que l'altitude d'arrêt diminue quand l'allumage est plus tardif. Un
  relief très accidenté peut casser cette hypothèse. La règle « on garde le plus tardif vérifié
  sûr » reste prudente dans ce cas.
