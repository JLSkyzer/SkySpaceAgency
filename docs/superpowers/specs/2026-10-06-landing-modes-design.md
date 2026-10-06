# Atterrissage — sous-projet 2 : pieds et parachutes — conception

Date : 2026-10-06 · Statut : validée en discussion, en attente de relecture
S'appuie sur le sous-projet 1 (`2026-10-05-landing-safety-design.md`, livré, test en jeu en attente).
Source : audit `docs/audits/2026-10-05-landing-physics-audit.md`, F18 (pas de détection du
contact), F19 (fin de descente en rétrograde pur, sans annulation de la dérive) et F20 (pieds
jamais déployés).

## Objectif

Deux façons d'atterrir, choisies par le joueur, lancées par un seul bouton :

- **Propulsive (legs)** : les moteurs freinent jusqu'à un posé doux. Les pieds se déploient seuls,
  le vaisseau finit droit sans dérive, et l'autopilote s'arrête au contact.
- **Parachute** : sans aucun moteur. Le vaisseau tient le rétrograde pendant la rentrée, les
  parachutes sont armés et le jeu les ouvre quand c'est sûr, puis les pieds se déploient s'il y en
  a. Le vaisseau descend sous voile.

## Hors périmètre

- Précision et choix de la cible au clic : sous-projet 3.
- Parachute combiné avec une poussée finale : écarté à la demande de l'utilisateur.
- Rentrée guidée (portance, angle d'attaque) ; traînée dans la simulation du freinage.
- Rentrer les pieds, gérer les roues de rover, couper les parachutes (le jeu les coupe lui-même
  après l'atterrissage, `Module_Parachute.UpdateCut`).
- Désorbitation automatique en mode parachute : le joueur désorbite lui-même, le mode refuse
  sinon.

## Choix du mode

- Réglage `land.mode` (`Propulsive` | `Parachute`), présent **seulement dans le profil
  atmosphère** (`LandingSettings` atmo). Sur un astre sans atmosphère, le mode est toujours
  Propulsive.
- L'onglet Land affiche un sélecteur `InlineEnum` dans le panneau atmosphère. Le bouton de
  lancement s'appelle « Land » en mode parachute et reste « Brake » en mode propulsif.
- Réglage `land.deploy_legs_early` (booléen, faux par défaut), présent dans les deux profils :
  avec ce réglage, les pieds sortent dès l'entrée en Brake au lieu de l'entrée en Touch Down.

## Mode Propulsive (legs)

La séquence reste celle du sous-projet 1 (QuickWarp → RotationWarp → Waiting → Brake → TouchDown).
Ce sous-projet y ajoute trois choses.

### Pieds

- Les pieds se déploient en entrant en `Mode.TouchDown`. Avec `deploy_legs_early`, ils se
  déploient dès `Mode.Brake`.
- Le déploiement passe par `VesselComponent.SetActionGroup(KSPActionGroup.Gear, true)` si
  `GetActionGroupState(Gear)` n'indique pas déjà un déploiement.
- **Vérification** : 3 s après la commande, l'état réel de chaque pied est relu
  (`Data_Deployable.CurrentDeployState`). S'il reste un pied `Retracted`, il est déployé
  directement par son module (`Module_Deployable.Extend()`). Le log note `[Landing] gear: …`
  avec l'état avant, l'état après et la voie utilisée.
- Un vaisseau sans pied atterrit quand même : l'onglet affiche « No legs », et rien n'est refusé.

### Fin de descente (h_final = 50 m au-dessus du sol)

Sous `h_final`, en mode TouchDown uniquement :

- **Orientation** : la cible est la verticale locale (`HorizonUp`), inclinée contre la vitesse
  horizontale. L'angle est `θ = atan(k · v_h / g)` avec `k = 1,0`, plafonné à **15°**.
  L'orientation est appliquée comme le fait déjà le steering, via SAS StabilityAssist et
  `SetTargetOrientation` dans le repère `HorizonUp`, et elle remplace le rétrograde pur.
- **Poussée** : au lieu d'être coupée par le test d'alignement (`touch_down_max_angle`), la
  poussée calculée est multipliée par `max(0, cos(écart d'orientation))`. Sous h_final, la poussée
  n'est donc jamais coupée net parce que le SAS est en retard.
- **Vitesse de contact** : sous h_final, la vitesse visée ne descend jamais sous **0,5 m/s**, même
  si « Touch-Down speed » vaut 0. Sinon le vaisseau resterait en vol stationnaire.

### Contact

- L'atterrissage est terminé dès `VesselComponent.LandedOrSplashed`. Le test actuel
  (`altitude < 5 && vitesse de chute < 1`) reste comme filet.
- À la fin : poussée 0, SAS en StabilityAssist, autopilote coupé. Les moteurs ne se rallument
  pas après le premier contact, même en cas de rebond ou de glissade.

## Mode Parachute

### Refus au départ (message persistant, comme au sous-projet 1)

| Condition | Message |
|---|---|
| astre sans atmosphère | `No atmosphere here: parachute landing needs one.` |
| aucun parachute à bord | `No parachute on this vessel.` |
| en orbite ou en évasion, périapside au-dessus de l'atmosphère | `Trajectory stays above the atmosphere: deorbit first.` |

Le refus se fait seulement si le vaisseau n'est pas déjà dans l'atmosphère (`IsInAtmosphere`).
Les contrôles moteur, TWR et Δv du sous-projet 1 ne s'appliquent pas à ce mode.

### Séquence (`Mode.Parachute`, une seule phase, contrôleur `ParachuteDescent`)

1. **Rentrée** : SAS en Retrograde, sans autre consigne. Le mode ne pilote pas l'accélération du
   temps, que le joueur garde en main.
2. **Armement** : tous les parachutes `STOWED` sont armés une seule fois (`Module_Parachute
   .ArmChute()`), dès le démarrage. Les parachutes déjà armés ou ouverts sont laissés tels quels.
   C'est le jeu qui les ouvre, selon les réglages de chaque parachute (`DeploymentMode`,
   `deployAltitude`, `minAirPressureToOpen`, ordre entre parachutes de freinage et principaux).
3. **Après l'ouverture** : dès qu'au moins un parachute est `DEPLOYED`, le SAS est relâché
   (StabilityAssist) et les pieds se déploient s'il y en a, avec la même vérification que plus haut.
4. **Contact** : `LandedOrSplashed` termine le mode. L'autopilote est coupé, le SAS est laissé en
   StabilityAssist.

### Alertes (affichées, sans action possible puisque sans moteur)

- `Too fast under canopy` : au moins un parachute est `DEPLOYED`, la hauteur est sous 500 m et la
  vitesse de descente dépasse **10 m/s**.
- `No parachute left` : il ne reste aucun parachute `STOWED`, `ARMED`, `SEMIDEPLOYED` ou
  `DEPLOYED` (tous `CUT` ou détruits) alors que le vaisseau vole encore.

## Architecture

| Fichier | Rôle |
|---|---|
| `Pilots/Landing/Braking/FinalDescent.cs` | Pur : direction de la fin de descente (verticale + inclinaison contre la dérive, plafond 15°), facteur de poussée `cos(écart)`, plancher de vitesse 0,5 m/s. |
| `Pilots/Landing/Braking/ParachuteFeasibility.cs` | Pur : refus de départ du mode parachute et messages ; seuil « Too fast under canopy ». |
| `KSPService/LandingGear.cs` | Présence et état des pieds (action group + `Data_Deployable` pied par pied), déploiement, vérification à 3 s, repli par module. |
| `KSPService/Parachutes.cs` | Parcours des pièces (`VesselBehavior.parts`, comme `VesselAeroLookup`) : nombre de parachutes par état, armement des `STOWED`. |
| `Pilots/Landing/Controlers/ParachuteDescent.cs` | Contrôleur de la phase Parachute : rétrograde, armement, relâche du SAS et pieds à l'ouverture, alertes. |
| `Pilots/Landing/LandingPilot.cs` | `Mode.Parachute`, démarrage selon le mode, refus du mode parachute, déploiement des pieds à l'entrée en TouchDown/Brake, fin d'atterrissage commune (`LandedOrSplashed`). |
| `Pilots/Landing/Controlers/TouchDown.cs` | Fin de descente sous h_final (orientation, poussée modulée, plancher 0,5 m/s). |
| `Pilots/Landing/LandingSettings.cs` | `land.mode` (profil atmo), `land.deploy_legs_early` (deux profils). |
| `Pilots/Landing/LandingUI.cs`, `UI/K2D2_UI/Landing.uxml` | Sélecteur de mode, option « Deploy legs early », lignes d'info (pieds, parachutes), libellé du bouton, alertes. |
| `Assets/Tests/Landing/` | Tests EditMode des deux unités pures. |

`Mode.Parachute` s'ajoute **en fin** de l'énumération `LandingPilot.Mode`. `nextMode()` n'est
jamais appelé depuis cette phase : le contrôleur ne passe jamais à `finished` et la fin vient de
la détection du contact.

## Tests

### Automatiques (EditMode)

1. `FinalDescent` : sans vitesse horizontale, la cible est la verticale exacte. Avec 1 m/s
   horizontal sur la Mun (g = 1,63), l'inclinaison vaut `atan(1/1,63)`, soit 31,5°, plafonnée à
   15°, dirigée contre la vitesse. Avec 0,1 m/s, l'inclinaison vaut `atan(0,1/1,63)`, soit 3,5°.
2. `FinalDescent` : facteur de poussée 1 à 0°, `cos 30°` à 30°, 0 au-delà de 90°. Plancher de
   vitesse : `max(v_td, 0,5)`.
3. `ParachuteFeasibility` : chaque refus avec son message, le cas OK, le cas « déjà dans
   l'atmosphère » qui accepte même avec une périapside haute, et le seuil d'alerte (10 m/s sous
   500 m, parachute ouvert).

### En jeu (par le joueur)

1. Mun, mode propulsif, avec pieds : les pieds sortent en Touch Down, la fin se fait droite et
   sans dérive, l'arrêt se fait au contact, et le vaisseau ne rebondit pas.
2. Même chose avec « Deploy legs early » : les pieds sortent dès Brake.
3. Atterrisseur sans pied : il se pose, l'onglet affiche « No legs ».
4. Kerbin, capsule avec parachute, trajectoire suborbitale : rétrograde, parachutes armés puis
   ouverts par le jeu, pieds s'il y en a, arrêt au contact.
5. Mode parachute sans parachute : refus. En orbite stable : refus « deorbit first ».
6. Log : lignes `[Landing] gear: …`. Elles disent si `Gear` déploie bien les pieds de série.

## Risques connus

- Que `KSPActionGroup.Gear` déploie les pieds de série et le sens de `GetActionGroupState(Gear)`
  ne sont pas vérifiés en jeu. D'où la relecture à 3 s et le repli par module.
- `ArmChute()` n'est pas documenté comme idempotent : il n'est appelé qu'une fois par parachute
  `STOWED`.
- Le moment où appeler les actions de groupe (Update ou FixedUpdate) n'est pas documenté. Elles
  sont appelées depuis `Update`, comme `SetThrottle`.
- La hauteur utilisée est celle du code actuel (`GetApproxAltitude`). La correspondance avec
  `AltitudeFromTerrain` reste à vérifier (audit, ligne 162).
