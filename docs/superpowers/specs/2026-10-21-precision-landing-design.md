# Atterrissage — sous-projet 3 : cible au clic et précision — conception

Date : 2026-10-21 · Statut : validée en discussion, relue par un constructeur, en attente de validation
S'appuie sur les sous-projets 1 (`2026-10-05-landing-safety-design.md`) et 2
(`2026-10-06-landing-modes-design.md`). Source technique : audit
`docs/audits/2026-10-05-landing-physics-audit.md` (constats F7, F9, F11, F13 à F15, F21), relu
le 2026-10-21 contre le code.

## Pourquoi

L'utilisateur voulait se poser sur la Mun à côté du **signal mystérieux** d'une mission. Il n'a
pas pu utiliser l'atterrissage de précision, pour deux raisons :

- il ne connaissait pas les coordonnées du signal et n'avait aucun moyen de les pointer ;
- à la main, il ne savait pas prévoir où le freinage le ferait arriver.

Le point de mission apparaît déjà dans la liste des waypoints de l'onglet Land, sous la forme
`[Mission] …` (livré le 2026-10-20). Il reste à pouvoir désigner n'importe quel point en
cliquant, à rendre la précision vraiment précise, et à l'offrir autour de Kerbin pour revenir
près du KSC sous parachutes.

## Trois parties, livrées dans cet ordre

1. **Cible au clic**, et précision proposée sur tous les corps.
2. **Précision à moins de 100 m** sur un corps sans atmosphère, comme la Mun ou Minmus.
3. **Retour sous parachutes** près d'une cible sur un corps à atmosphère, depuis l'orbite.

Chaque partie est utilisable seule et testée en jeu avant la suivante.

## Objectifs

- Désigner une cible en cliquant sur le corps dans la **vue carte**, la vue avec les orbites.
  La vue de vol peut aussi marcher, en bonus.
- Mun et corps sans atmosphère : se poser à **moins de 100 m** de la cible, pour au plus
  **20 % de Δv de plus** qu'un atterrissage non précis. Le changement de plan n'entre pas dans
  ces 20 %.
- Kerbin : depuis l'orbite, revenir sous parachutes à **quelques kilomètres** de la cible. Le
  KSC est une cible prête dans la liste.
- Un seul bouton : le joueur choisit la cible, lance Land, et le pilote fait le reste.

## Non-objectifs

- 100 m sous parachutes. C'est irréaliste, car le jeu ouvre les parachutes lui-même et la
  capsule dérive sous voile.
- La rentrée guidée par portance ou angle d'attaque, et le vol plané.
- L'arrivée directe depuis une trajectoire interplanétaire ou un retour lunaire sans mise en
  orbite. Le départ se fait toujours d'une orbite fermée autour du corps visé.
- La précision propulsive sur un corps à atmosphère : avec moteurs sur Kerbin, Duna, etc.
- Le pointeur dans le Tracking Station. L'utilisateur l'a évoqué sans insister, et l'outil de
  Redux n'est connu que pour la carte et le vol.

## Anti-objectifs

Ce qui ferait de ce travail un échec, même s'il « marche » :

- un pilote qui fonce vers la cible et s'écrase, ou qui vide le réservoir et ne peut plus
  freiner, plutôt que de se poser un peu plus loin ;
- un clic sur la carte qui ajoute des waypoints dans la liste du joueur à son insu ;
- une précision affichée comme disponible sur Kerbin alors qu'elle ne marche pas ;
- un mode précision moins sûr que le mode normal : les garde-fous du sous-projet 1 (TWR, Δv,
  « Cannot stop before the ground! ») restent prioritaires sur la cible.

## Contraintes

- **Jeu et outils** :
  - **Version** : KSP2 Redux 0.2.9, snapshot 26w42c. Le code du jeu est dans `Ksp2Redux.dll`.
  - **Loggers** : `SpaceWarp2.API.Logging`.
  - **Activation** : le mod est désactivé tant que la campagne ne l'active pas. Voir le
    `CLAUDE.md` du dépôt parent.
  - **UI** : créée seulement quand le mod est actif.
- **API internes** : l'outil de placement de Redux (`Redux.UI.Waypoints.PlayerWaypointManager`)
  est `internal`. On y accède par réflexion, comme `ReduxWaypoints.cs` le fait déjà. Si un
  membre manque, il faut un message clair dans l'onglet et dans le log, jamais une exception.
- **Tests** : le code de calcul pur est testé en EditMode (`Assets/Tests/Landing`) et ne
  référence aucun type du jeu. Aujourd'hui, `LandingTargeting` et le pilotage final ne sont pas
  testables, car ils dépendent de `CelestialBodyComponent`. Cette séparation fait partie du
  travail.
- **Licence et suivi** : licence CC BY-SA 4.0. Chaque changement visible est noté dans
  `CHANGELOG.md`. Le code de déorbitation existant écarte volontairement la recherche par
  résonance « pour des raisons de licence GPL » (`LandingTargeting.cs`, vers la ligne 648) : ne
  pas reprendre de code GPL.
- **Langue** : l'interface et les messages sont en anglais, comme le reste du mod.

## Partie 1 — Cible au clic, précision proposée partout

### Ce que voit le joueur

- **Bouton** : dans l'onglet Land, à côté de la liste des waypoints, un bouton **Pick on map**.
  - Il ouvre la vue carte si elle ne l'est pas déjà, puis attend un clic sur le corps.
  - Le texte du bouton indique l'attente, par exemple « Click the body… (Esc to cancel) ».
- **Clic** : la latitude et la longitude se remplissent, et le point devient la cible.
  - Les champs restent modifiables à la main.
  - La liste affiche alors une entrée « Picked point (lat, lon) ».
- **Annulation** : Échap, ou un second clic sur le bouton.
- **Autre corps** : un clic sur un autre corps que celui de l'orbite actuelle est refusé, avec
  un message.
- **Aucun waypoint créé** : le clic ne crée pas de waypoint dans la liste du joueur. Il faut le
  vérifier en jeu : `Records.Count` doit rester identique après un clic.
- **Liste** :
  - **KSC** : sur Kerbin, la liste contient **KSC** en tête, au-dessus des waypoints du joueur
    et des missions.
  - **Ordre** : KSC, point cliqué, waypoints du joueur, puis `[Mission] …`.
- **Corps à atmosphère** :
  - **Avant la partie 3** : l'option de précision et la cible sont visibles, mais la précision
    reste désactivée, avec la mention « Precision landing on atmospheric bodies: coming with
    parachute targeting ».
  - **Après la partie 3** : la précision est active en mode Parachute. En mode Propulsive sur
    un corps à atmosphère, elle reste indisponible avec un message (voir les non-objectifs).

### Décidé

- **Réutiliser l'outil de placement de Redux** : `BeginPlacement()`, l'événement
  `PlacementCompleted` (corps, latitude, longitude), `CancelPlacement()`, `PlacementFailed` et
  `IsPlacementActive`. Il gère déjà le clic en vue carte et en vue de vol. Ne pas réécrire de
  lancer de rayon tant que cet outil fonctionne.
- **Coordonnées du KSC** : le point visé est **le pas de tir**. Ses coordonnées sont lues dans
  le jeu plutôt que codées en dur. *(Choix de Claude.)* Une valeur codée en dur n'est admise qu'en
  secours, documentée et loguée.
- **Durée de vie du point cliqué** : il est écrit dans les réglages de cible existants
  (latitude et longitude), qui sont déjà sauvegardés. L'entrée « Picked point » de la liste ne
  vit que pendant la session. *(Choix de Claude.)*

### Laissé au constructeur

- Le déroulé exact de l'attente : bascule vers la carte, ce qui se passe si le joueur ferme la
  carte pendant l'attente.
- La façon de résoudre l'ordre des arguments de `PlacementCompleted`. Il n'est déduit que des
  noms. Il faut le vérifier par un test en jeu et le loguer au premier clic.
- Le repli si `PlayerWaypointManager` est introuvable : bouton désactivé avec une infobulle,
  et saisie manuelle toujours possible.

## Partie 2 — Précision à moins de 100 m sans atmosphère

### Ce que voit le joueur

- Il choisit la cible (liste, clic ou saisie) et lance Land une fois.
- **Avant de démarrer**, l'onglet affiche :
  - le Δv prévu pour toute la manœuvre (changement de plan, désorbitation, freinage, marge) ;
  - l'écart attendu ;
  - un refus clair si le Δv ne suffit pas.
- **Pendant la descente**, l'onglet affiche la distance prévue à la cible et la phase en cours.
- **À l'arrivée**, l'onglet affiche la distance réelle à la cible.

### Comportement attendu

- **Départ** : depuis toute orbite fermée. Si elle n'est pas assez basse ou pas assez ronde, le
  pilote la circularise d'abord, comme le fait déjà l'étape `Circularize`. Ce coût est affiché
  à part, comme celui du changement de plan, et n'entre pas dans les 20 %. *(Choix de Claude.)*
- **Changement de plan** : si l'orbite ne passe pas au-dessus de la cible, le pilote change
  lui-même de plan **tout de suite**. Il n'attend pas que la rotation du corps amène la cible
  sous l'orbite : c'est le choix de l'utilisateur. Ce coût est affiché, mais il n'entre pas dans
  les 20 %. Il réutilise le
  guidage en boucle fermée des onglets Orbit et Node (`OrbitGuidance`, `TargetOrbitBurn`), qui
  vise une orbite cible plutôt que le modèle de nœud du jeu.
- **Désorbitation** : le moment est choisi pour que le **point d'arrêt réel** tombe sur la
  cible.
  - **Point d'arrêt** : c'est la fin du freinage simulé, pas l'impact balistique.
  - **Rotation du corps** : elle est calculée sans la constante empirique ×1,307 actuelle.
  - **Recherche du moment** : elle remplace le balayage de 36 échantillons par une recherche
    qui converge.
- **Correction en route** : une correction à mi-parcours, sur le même modèle.
- **Fin de descente** : un guidage qui ramène la vitesse horizontale à zéro au-dessus de la
  cible.
- **Garde-fous** :
  - **Priorité** : ceux du sous-projet 1 restent prioritaires.
  - **Si la cible devient inatteignable** (Δv, TWR, terrain), le pilote le dit et se pose au
    plus près, en sécurité.
  - **Dépassement des 20 %** : si la précision coûte plus de 20 % de Δv au-delà d'un
    atterrissage normal, le pilote prévient et se pose au plus près.
- **Référence des 20 %** : c'est le Δv que demande au même vaisseau, depuis la même orbite
  circularisée et dans le même plan, un atterrissage sans cible : désorbitation au point naturel
  puis freinage, prévu par la même simulation. La comparaison se fait sur la prévision au départ,
  et le Δv réellement consommé est logué pour le test en jeu. *(Choix de Claude.)*
- **Terrain de la cible** : pas d'évitement d'obstacles. Le pilote vise le point demandé, et les
  garde-fous existants (contact, vitesse, pente) restent ceux du sous-projet 2. Si la cible est
  sur une forte pente, il le signale sans la déplacer. *(Choix de Claude : l'évitement
  d'obstacles est un sous-système à part, hors de ce sous-projet.)*

### Décidé

- La prédiction tient compte du freinage, par la simulation du sous-projet 1 (`BrakeSimulator`),
  et de la rotation du corps dans un repère lié au sol.
- Les calculs nouveaux ou repris passent dans des classes pures testées en EditMode :
  prédiction du point d'arrêt, recherche du moment de désorbitation, plan qui survole la cible,
  guidage final.

### Laissé au constructeur

- La loi de guidage finale (ZEM/ZEV ou équivalent), les altitudes de passage d'une phase à
  l'autre, le périapse de la trajectoire de descente, le choix entre nœud ascendant et
  descendant pour le changement de plan.
- La façon de démontrer les 100 m en EditMode, avec une simulation de bout en bout sur la Mun,
  en impulsions puis en poussée finie, et les tolérances retenues.

## Partie 3 — Retour sous parachutes près d'une cible

### Ce que voit le joueur

- **Lancement** : sur Kerbin, en orbite, il choisit **KSC** (ou une autre cible), le mode
  **Parachute** et la précision, puis lance Land.
- **Déroulé** :
  1. Le pilote calcule le moment du burn de désorbitation et l'effectue avec les moteurs du
     vaisseau.
  2. Il **largue lui-même** le module de service : tout ce qui est sous l'étage qui porte les
     parachutes et le bouclier. C'est le choix de l'utilisateur. La prédiction tient compte de
     ce largage, avec la masse et la traînée de la capsule seule après le largage.
  3. Il tient le rétrograde pendant la rentrée.
  4. Il laisse le jeu ouvrir les parachutes, comme au sous-projet 2.
- **Écart prévu** : il est affiché avant le départ et pendant la rentrée, avec une estimation
  honnête, par exemple « about 3 km ».
- **Refus** : sans moteur pour désorbiter, le pilote refuse avec un message. Le mode Parachute
  sans précision reste disponible.

### Comportement attendu

- Objectif : quelques kilomètres. **Sous 5 km** est la cible de réussite. *(Choix de Claude.)*
- Le prédicteur tient compte :
  - de la traînée de la rentrée et des parachutes, phases fermée, déployée puis ouverte ;
  - de la rotation de l'atmosphère avec la planète.
- Si la cible est hors d'atteinte depuis l'orbite actuelle (plan, carburant), même conduite qu'à
  la partie 2 : changement de plan automatique avec coût affiché, ou refus.

### Décidé

- **Prédicteur** : partir de `AtmosphericPredictor` et `VesselAeroLookup`, écrits par
  IanMealworm et jamais branchés, en les complétant (parachutes, rotation de l'atmosphère)
  plutôt que d'écrire un nouveau prédicteur. Les commentaires périmés de ces fichiers sont
  corrigés.
- **Contrôle** : la seule commande est le moment, voire l'ampleur, du burn de désorbitation.
  Pas de pilotage sous voile.

### Laissé au constructeur

- La façon de repérer l'étage à larguer (celui qui sépare les parachutes du reste), et le
  refus clair quand il n'est pas identifiable sans ambiguïté.
- Le modèle de traînée des parachutes, à partir des données des pièces, et son calage.
- Le pas d'intégration et le coût de calcul.
- La part testable en EditMode, et la part qui sera seulement vérifiée en jeu.

## Vérification

- **EditMode** : chaque partie ajoute ses tests, et tous les tests existants restent verts
  (192 au 2026-10-20).
- **Tests en jeu**, à faire par l'utilisateur après chaque partie :
  1. **Partie 1** :
     - cliquer sur la Mun en vue carte : les coordonnées se remplissent et aucun waypoint
       n'apparaît dans la fenêtre Waypoints de Redux ;
     - sur Kerbin : « KSC » figure en tête de liste.
  2. **Partie 2** :
     - depuis une orbite basse de la Mun, cible le signal mystérieux : posé à moins de 100 m,
       Δv utilisé au plus 20 % au-dessus d'un atterrissage normal ;
     - même test avec une cible qui demande un changement de plan.
  3. **Partie 3** : depuis une orbite basse de Kerbin, cible KSC en mode Parachute : posé à moins
     de 5 km du KSC.
- **Log** : chaque phase logue ce qu'il faut pour diagnostiquer un écart :
  - la prédiction et l'écart prévu ;
  - le Δv par phase ;
  - la distance finale ;
  - pour le pointeur, les valeurs brutes reçues de Redux.
