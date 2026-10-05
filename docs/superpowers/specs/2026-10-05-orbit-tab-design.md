# Onglet « Orbit » — conception

Date : 2026-10-05 · Statut : validée en discussion, en attente de relecture

## Objectif

Permettre au joueur de viser une orbite (apoapside, périapside, inclinaison) depuis un nouvel
onglet **Orbit**, puis de créer d'un clic **tous** les nœuds de manœuvre nécessaires. L'exécution
reste celle de l'onglet **Node**, nœud par nœud, sans changement.

## Hors périmètre

- **Lift** : aucun changement. Il met déjà en orbite circulaire à « Ap Altitude »
  (`destination_Ap_km`) et circularise toujours en fin de montée.
- Pas d'enchaînement automatique création → exécution. Node se relance pour chaque nœud.
- Pas de visée du plan complet (longitude du nœud ascendant), pas de rendez-vous, pas de
  transfert vers un autre corps.
- Pas de trajectoire d'évasion en entrée ni en sortie : orbites fermées uniquement.

## Interface

Onglet `Orbit`, placé juste après `Node` dans la barre d'onglets.

| Cible | Case | Champ | Unité | Bornes |
|---|---|---|---|---|
| Apoapside | « Ap » | altitude | km | > 0 |
| Périapside | « Pe » | altitude | km | > 0 |
| Inclinaison | « Inclination » | angle | ° | 0 à 180 |

- **Case décochée** : la cible garde la valeur actuelle de l'orbite. Le champ affiche en continu
  la valeur actuelle et n'est pas modifiable.
- **Case cochée** : le champ devient modifiable. Au moment où on coche, il est initialisé avec
  la valeur actuelle. Ensuite, la valeur saisie est conservée et sauvegardée.
- Bouton **« Create maneuvers »**.
- Sous le bouton, un résumé :
  - chaque nœud prévu, avec son instant (T+hh:mm:ss), son point (« périapside »,
    « apoapside », « nœud ascendant »…) et son Δv ;
  - le Δv total ;
  - l'orbite finale calculée par le planificateur ;
  - si l'API le permet, l'orbite finale **prévue par le jeu** après les nœuds créés ;
  - ou un message d'erreur en clair.

## Comportement de « Create maneuvers »

1. Lit l'état réel du vaisseau : position `r0` et vitesse `v0` relatives au corps
   (`GetRelativePositionAtUTZup` / `GetOrbitalVelocityAtUTZup`), μ, rayon du corps, profondeur
   d'atmosphère, instant `UT0`.
2. **Garde-fou de repère** : recalcule l'inclinaison actuelle à partir de `r0`, `v0` et la
   compare à `orbit.inclination` du jeu. Au-delà de 0,5° d'écart, refus : « repère inattendu,
   aucun nœud créé ».
3. Calcule le plan (section suivante). En cas de refus, affiche le message et ne touche à rien.
4. Supprime tous les nœuds existants du vaisseau, puis crée les nœuds dans l'ordre
   chronologique, **un par frame physique**. Le code existant documente qu'un nœud créé dans la
   même frame qu'une suppression est cassé.
5. Affiche le résumé.

## Algorithme de planification (`OrbitPlanner`)

Notations : `R` rayon du corps, `ra`/`rp` rayons d'apoapside et de périapside (altitude + `R`),
`i` inclinaison. Les cibles décochées prennent la valeur actuelle.

### Validations (refus avec message)

- Orbite actuelle non fermée (`sma ≤ 0` ou `ra` infini) : « trajectoire d'évasion ».
- Périapside actuelle sous la surface, ou dans l'atmosphère : « orbite actuelle instable,
  circularise d'abord ».
- `rp` visée < `R` + profondeur d'atmosphère (ou < `R` + 1 km sans atmosphère) :
  « Pe visée sous la surface » ou « Pe visée dans l'atmosphère ».
- `rp` visée > `ra` visée : « la Pe ne peut pas dépasser l'Ap ».

### Tolérance

Une apside est considérée comme déjà bonne si l'écart est inférieur à max(100 m, 0,1 %).
L'inclinaison, si l'écart est inférieur à 0,05°.

### Étape 1 — forme de l'orbite (0, 1 ou 2 poussées, toutes progrades ou rétrogrades)

- **Seule l'Ap change** : une poussée à la prochaine périapside, qui fixe l'apside opposée à
  `ra` visée.
- **Seule la Pe change** : une poussée à la prochaine apoapside, qui fixe l'apside opposée à
  `rp` visée.
- **Les deux changent** :
  1. poussée 1 à la prochaine périapside (rayon `r1`) : l'orbite intermédiaire a pour apsides
     `r1` et `ra` visée ;
  2. poussée 2 à l'apside de rayon `ra` visée : l'apside opposée devient `rp` visée.
- **Orbite actuelle quasi circulaire** (`ra − rp` < 2 km) : la périapside n'est pas définie de
  façon fiable, donc la première poussée a lieu à `UT0 + 180 s`.
- Si l'apside choisie arrive dans moins de 60 s, on prend la suivante (+ une période). Cela
  laisse le temps à Node de tourner le vaisseau.
- Une apside a une vitesse purement tangentielle. Vitesse après poussée par vis-viva :
  `v' = sqrt(μ (2/r − 2/(r + r_opposée)))`. Δv prograde = `v' − v`.

### Étape 2 — inclinaison (0 ou 1 poussée), sur l'orbite issue de l'étape 1

- Ligne des nœuds `n = ẑ × h`, avec `h = r × v` et `ẑ` l'axe nord du repère. Nœud ascendant
  à l'anomalie vraie `ν = −ω`, nœud descendant à `ν = π − ω`.
- On choisit **celui des deux dont le rayon est le plus grand** : vitesse plus faible, donc
  changement de plan moins coûteux. Instant obtenu par l'équation de Kepler (anomalies
  excentrique et moyenne) depuis l'état propagé.
- **Orbite actuelle quasi équatoriale** (`|h_xy|/|h|` < 1e-4) : ligne des nœuds indéfinie. La
  poussée a lieu à la prochaine apoapside (ou à `UT0 + 180 s` si l'orbite est quasi circulaire),
  et ce point devient le nœud.
- Poussée : rotation de la vitesse autour de `r̂` d'un angle `±Δi`. On essaie les deux signes
  et on garde celui dont l'inclinaison résultante est la plus proche de la cible.
  `Δv = v' − v`. Contrôle de cohérence : `|Δv| ≈ 2·v·sin(Δi/2)`.

### Simulation

Chaque poussée est calculée sur l'état **propagé** (`KeplerPropagator.Propagate`) jusqu'à son
instant, puis appliquée de façon impulsive. Les poussées suivantes partent de l'état résultant.
L'orbite finale sert au résumé et aux tests.

### Repère de la poussée

Le jeu attend un `BurnVector` (x = radial, y = normal, z = prograde) : voir les helpers
`ProgradeBurnVector`, `NormalBurnVector` et `RadialOutBurnVector`. À l'instant de la poussée :
`prograde = v̂`, `normal = ĥ = (r × v)/|r × v|`, `radial = v̂ × ĥ`, et chaque composante est la
projection de `Δv` sur l'axe correspondant.

**Point ouvert** : le repère `Zup` est peut-être gaucher (Unity). Dans ce cas, le « normal » du
jeu serait `−ĥ`. À vérifier pendant l'implémentation, en lisant comment le code existant
utilise `normalDeltaV`, puis à confirmer en jeu (scénario 4).

## Architecture

| Fichier | Rôle | Dépendances |
|---|---|---|
| `Code/Pilots/Orbit/OrbitPlanner.cs` | Maths pures : état + cibles → `OrbitPlan` (liste de `PlannedBurn {UT, radial, normal, prograde, label}`, orbite finale prévue) ou erreur. Aucun accès au jeu ni à l'UI. | `KeplerPropagator`, `VisVivaEquation` |
| `Code/Pilots/Orbit/OrbitSettings.cs` | Clés `orbit.ap_enabled`, `orbit.ap_km`, `orbit.pe_enabled`, `orbit.pe_km`, `orbit.inc_enabled`, `orbit.inc_deg`. | `Setting<>`, `ClampSetting<>` |
| `Code/Pilots/Orbit/OrbitUI.cs` | `K2Page` (code `"orbit"`) sans pilote, enregistrée dans `K2D2Window` comme `AboutUI`. Lie les champs, lit l'état, appelle le planificateur, garde-fou de repère, création des nœuds, résumé. | ci-dessus, `ManeuverCreator` |
| `Code/KSPService/ManeuverCreator/ManeuverCreator.cs` | Ajout de `CreateNodes(IReadOnlyList<PlannedBurn> burns, Action<List<ManeuverNodeData>> onDone)` : supprime tout, attend une FixedUpdate, crée un nœud par FixedUpdate. Ajout d'un paramètre radial au chemin de création existant, sans changer ses appelants. | API `AddNodeToVessel` |
| `UI/K2D2_UI/Orbit.uxml` | Interface, référencée par GUID dans `K2D2_Window.uxml` (`<ui:Template>` + `<ui:Instance>` après Node). | contrôles K2UI |
| `Tests/Editor/OrbitPlannerTests.cs` + `.asmdef` | Tests EditMode du planificateur. | `SkySpaceAgency`, NUnit |

## Tests

### Automatiques (EditMode, `Unity.exe -batchmode -runTests -testPlatform EditMode`)

Corps de test : μ et rayon de Kerbin, atmosphère de 70 km. Chaque cas applique le plan avec le
propagateur et vérifie l'orbite obtenue : Ap et Pe à 0,1 % près, inclinaison à 0,05° près.

1. Circulaire 100 km → Ap 500 : 1 poussée, Δv = première impulsion de Hohmann (à 1 % près).
2. Elliptique 100 × 500 → Pe 300 : 1 poussée à l'apoapside.
3. Circulaire 100 km → Ap 500 et Pe 300 : 2 poussées.
4. Circulaire 100 km, i = 0° → i = 30° : 1 poussée, `|Δv| ≈ 2·v·sin(15°)`.
5. Elliptique inclinée → Ap, Pe et i combinées : 3 poussées dans l'ordre chronologique.
6. Refus : Pe visée sous la surface, dans l'atmosphère, Pe > Ap, orbite d'évasion, orbite
   actuelle instable.
7. Rien à faire (cibles = orbite actuelle) : plan vide, message « déjà sur l'orbite visée ».

### En jeu (par le joueur, depuis ~100 km circulaire autour de Kerbin)

1. Valeurs actuelles affichées = celles de la carte (valide le garde-fou de repère).
2. Ap 500 seule : 1 nœud, la carte montre Ap ≈ 500 km.
3. Ap 500 + Pe 300 : 2 nœuds.
4. Inclinaison 30° : 1 nœud à un nœud orbital, la carte montre ≈ 30° (valide le sens du
   « normal »).
5. Exécution de chaque nœud par l'onglet Node, puis relecture du log du jeu.

## Risques connus

- Sens de l'axe normal dans le repère `Zup` (voir « Point ouvert »).
- Comportement du jeu quand plusieurs nœuds sont créés d'affilée, et ordre dans lequel Node
  les prend (`GetNodesForVessel(...)[0]`).
- Lecture de l'orbite prévue par le jeu après les nœuds : à confirmer dans l'API. Sinon, cet
  affichage est omis.
