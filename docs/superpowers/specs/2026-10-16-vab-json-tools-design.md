# Outils VAB : export, import et dump JSON — conception

Date : 2026-10-16 · Statut : à relire par l'utilisateur

## Pourquoi

L'utilisateur a construit une fusée dans le VAB. Une fois posée sur la Mun, il n'avait plus assez
de Δv pour repartir ; il a corrigé à la main. La prochaine fois, il veut pouvoir donner sa fusée à
une IA. L'IA doit pouvoir :

- comprendre la fusée et expliquer ce qui manque, ou
- fournir un fichier corrigé, que l'utilisateur importe dans le VAB.

L'utilisateur veut les deux possibilités. L'IA ne doit proposer que des pièces que le joueur a
réellement débloquées, d'où le dump des pièces disponibles.

## Objectifs

1. **Export** : un fichier JSON de la fusée ouverte dans le VAB, avec un maximum de données. Il
   contient deux parties :
   - un **résumé lisible** pour une IA (voir « Contenu de l'export ») ;
   - le **JSON brut du jeu** pour cet assemblage, tel que le jeu le sérialise lui-même, que l'import
     sait recharger.
2. **Import** : charger un fichier JSON au format brut du jeu, ou un fichier d'export du mod dont
   on reprend la partie brute, dans le VAB. Il sert aussi bien à **remplacer la fusée actuelle** par
   une version corrigée qu'à **construire de zéro**.
3. **Dump** : un fichier JSON de toutes les pièces du jeu. Pour chaque pièce : ses caractéristiques,
   si elle est débloquée dans la campagne en cours, et le nœud de recherche qui la débloque. Il
   donne aussi de quoi écrire une pièce valide au format brut (voir « Contenu du dump »).

## Hors objectifs

- Aucune analyse de mission dans le mod. L'utilisateur ne veut pas de champ « mission visée » : il
  l'expliquera à l'IA lui-même.
- Aucun générateur automatique de fusée.
- Pas d'édition de pièces dans l'interface du mod.
- Pas d'appel à une IA depuis le jeu : on écrit des fichiers, et le joueur les donne à l'IA.

## Anti-objectifs (échec même si ça marche)

- **Un import qui casse silencieusement la fusée du joueur**, c'est-à-dire qui la remplace sans
  qu'il le sache, sans retour possible ou à moitié.
- **Un import qui fait apparaître des pièces non débloquées** en campagne : ce serait de la triche
  involontaire.
- **Un export illisible pour une IA** : par exemple seulement le JSON brut du jeu, sans noms
  lisibles ni Δv par étage.

## Ce que dit le jeu (recherche du 2026-10-16, doc API beta `9b15c3ff` + fichiers sur disque)

Rien de ceci n'est encore vérifié en jeu.

| Rôle | Membre |
|---|---|
| Atelier ouvert | `Game.OAB.Current` (`ObjectAssemblyBuilder`), scène `GameState.VehicleAssemblyBuilder` |
| Fusée principale | `builder.Stats.MainAssembly` (`IObjectAssembly`) |
| Δv par étage | `MainAssembly.VesselDeltaV.StageInfo` (`DeltaVStageInfo` : `StageMass`, `DryMass`, `FuelMass`, `StartMass`, `EndMass`, `IspVac/ASL/Actual`, `TWRVac/ASL/Actual`, `ThrustVac/ASL/Actual`, `DeltaVinVac`, `DeltaVatASL`, `DeltaVActual`, `StageBurnTime`, `EnginesActiveInStage`), `IsReady`, `TotalDeltaVVac` / `Actual` |
| Situation par étage | `MainAssembly.StageDeltaVSituation` (astre, atmosphère, altitude) |
| Pièces | `IObjectAssemblyPart` : `AvailablePart.Name` / `Title`, parent et enfants, nœuds d'attache, symétrie, `InStageIndex`, ressources (`Name`, `Count`, `Capacity`, masses), modules |
| Sérialisation | `builder.UtilFileIO.AssemblyToString(assembly)` / `AssemblyToSerializable` → JSON `SerializedAssembly` (format des sauvegardes de workspace de la campagne) |
| Rechargement | `UtilFileIO.LoadSerializedAssembly`, `AssemblyFromString(json, Stats.AvailableParts)`, `ObjectAssemblyBuilder.LoadFromFile(TextAsset…)` |
| Pièces disponibles | `Stats.AvailableParts` (filtrées par déblocage) / `AllKnownParts` ; le chargement échoue proprement sur une pièce absente (`ResolveAvailableParts`) |
| Toutes les pièces | `Game.Parts.AllParts()` (`PartCore.data` : nom, titre, catégorie, famille, coût, masse, taille, nœuds d'attache, conteneurs de ressources, tags…), `GetRawJson(name)` |
| Déblocage | `Game.SessionManager.IsPartUnlocked(id)`, `GetMyAgencyUnlockedTechNodes()`, `Game.ScienceManager.TechNodeDataStore.PartIDLookup` (pièce → nœud) |

**Inconnues à lever en jeu :**
- l'import par `LoadSerializedAssembly` remplace-t-il ou fusionne-t-il, et passe-t-il par
  l'annulation (`builder.Historian`) ?
- le comportement en bac à sable ;
- la fraîcheur du Δv juste après une modification (`IsReady`).

## Conception

### Interface

- Un bouton dans la barre d'applis **du VAB** (`Appbar.RegisterOABAppButton`) ouvre une petite
  fenêtre « SkySpaceAgency — VAB tools », séparée de la fenêtre de vol. La fenêtre de vol reste
  réservée au vol, comme aujourd'hui.
- **Contenu de la fenêtre :**
  - **Export** : écrit l'export de la fusée ouverte et affiche le chemin du fichier.
  - **Import** : une liste déroulante des fichiers `.json` du dossier `imports`, plus un bouton
    « Import (replaces the current rocket) ».
  - **Dump parts** : écrit le dump et affiche le chemin.
  - **Open folder** : ouvre le dossier dans l'explorateur Windows.
  - Une ligne d'état pour les messages : réussite, erreur, pièces manquantes.
- **Langue** : les textes de l'interface sont en anglais, comme le reste du mod.

### Fichiers *(choix de Claude)*

- Dossier `<jeu>/mods/SkySpaceAgency/vab/`, avec trois sous-dossiers `exports/`, `imports/` et
  `dumps/`. C'est le schéma existant (fichiers à côté de la DLL). L'installation par copie ne
  supprime rien. Le joueur dépose dans `imports/` le fichier que l'IA lui a donné.
- **Noms de fichiers :**
  - `exports/<nom du vaisseau>_<AAAA-MM-JJ_HHMMSS>.json` ;
  - `dumps/parts_<campagne>_<AAAA-MM-JJ_HHMMSS>.json`.
- Encodage UTF-8, JSON indenté.

### Contenu de l'export

```
{
  "format": "SkySpaceAgency.VabExport", "version": 1,
  "exported_at": "...", "game_version": "...", "mod_version": "...",
  "campaign": {"name", "mode", "unlock_all_parts"},
  "instructions_for_ai": "texte court : ce que contient chaque section, que `game_assembly` est le format
     que l'import recharge, qu'il faut garder les GUID et les paires de nœuds cohérents, et n'utiliser
     que des pièces marquées unlocked dans un dump",
  "summary": {
    "vessel_name", "part_count", "mass_wet_t", "mass_dry_t",
    "delta_v_total_vac", "delta_v_total_actual", "delta_v_ready": bool,
    "stages": [ { "stage", "parts": [titres], "engines_active": [titres],
                  "start_mass_t", "end_mass_t", "dry_mass_t", "fuel_mass_t",
                  "isp_vac", "isp_asl", "thrust_vac_kN", "thrust_asl_kN",
                  "twr_vac", "twr_asl", "twr_actual",
                  "delta_v_vac", "delta_v_asl", "delta_v_actual",
                  "burn_time_s", "situation": {"body", "atmosphere", "altitude_m"} } ],
    "parts": [ { "guid", "name", "title", "parent_guid", "attach_node",
                 "stage_index", "symmetry_group", "mass_t", "cost",
                 "resources": [ {"name", "amount", "capacity", "mass_t"} ],
                 "modules": [noms] } ],
    "resources_total": [ {"name", "amount", "capacity", "mass_t"} ]
  },
  "game_assembly": <JSON SerializedAssembly du jeu, tel quel>
}
```

L'ordre des étages suit la numérotation affichée dans le VAB. La conversion d'index étage ↔ liste
est à vérifier, car le jeu stocke certaines données à l'envers.

### Import

- Accepte deux formes : un export du mod, dont on lit `game_assembly`, ou un JSON `SerializedAssembly`
  brut.
- **Avant de toucher à la fusée :**
  1. analyser le JSON ;
  2. vérifier que chaque `partName` existe dans `Stats.AvailableParts`, donc qu'il est débloqué en
     campagne ;
  3. si une pièce manque, **refuser** et lister les pièces manquantes (nom + débloquée ou non) dans
     la ligne d'état. La fusée actuelle reste intacte.
- **Sécurité** : juste avant de remplacer, sauvegarder automatiquement la fusée actuelle dans
  `exports/` (`before_import_<…>.json`). Le joueur peut toujours la réimporter.
- **Remplacement** : charger l'assemblage comme fusée principale par l'API du jeu. Le mécanisme
  exact (remplacer ou vider puis charger) dépend du comportement réel à vérifier en jeu. Le
  résultat voulu : la fusée importée est la fusée principale, et l'ancienne a disparu ou est
  sauvegardée.
- Une erreur de chargement s'affiche avec son message. La fusée actuelle n'est pas modifiée tant
  que le chargement n'a pas réussi, dans la mesure où l'API le permet.

### Contenu du dump

```
{
  "format": "SkySpaceAgency.PartsDump", "version": 1, "exported_at", "game_version",
  "campaign": {"name", "mode", "unlock_all_parts", "unlocked_tech_nodes": [...]},
  "parts": [ { "name", "title", "category", "family", "tech_node", "unlocked": bool,
               "cost", "mass_t", "size", "crew_capacity", "max_temp", "crash_tolerance",
               "attach_nodes": [...], "resources": [ {"name", "capacity"} ],
               "engine": { "thrust_vac_kN", "thrust_asl_kN", "isp_vac", "isp_asl", "propellants" } | null,
               "modules": [noms] } ],
  "part_templates": { "<partName>": <JSON brut de la pièce, Game.Parts.GetRawJson> }
}
```

- `part_templates` donne à l'IA la forme exacte d'une pièce pour écrire un `game_assembly` valide.
  S'il s'avère trop lourd, on le limite aux pièces débloquées. Ce sera mesuré au premier essai.
- Les données moteur sont remplies quand le jeu les expose, `null` sinon.

## Tests

- **Automatiques (EditMode)** : uniquement la logique pure.
  - Construction du résumé à partir de structures simples : agrégation des ressources, mise en
    forme des étages.
  - Validation d'import : liste des pièces manquantes, détection des deux formats, refus d'un JSON
    invalide.
  - Noms de fichiers.
- **En jeu :**
  1. Exporter une fusée à trois étages : le résumé donne le même Δv et le même TWR par étage que le
     VAB.
  2. Réimporter cet export tel quel : même fusée.
  3. Modifier `game_assembly` (supprimer une pièce) puis importer : la fusée change, et
     `before_import_…` existe.
  4. Importer un fichier contenant une pièce non débloquée : refus et liste des pièces.
  5. Dump en campagne : `unlocked` correspond à ce que le VAB propose.

## Choix de Claude après relecture (à corriger si besoin)

1. **Construire de zéro.**
   - Le dump contient aussi ses `instructions_for_ai`, qui expliquent comment assembler un
     `game_assembly` : pièce racine, liens parent/enfant, paires de nœuds, étages, symétrie.
   - Il contient aussi un `example_assembly` : le `game_assembly` de la fusée ouverte au moment du
     dump. Le dump se fait depuis le VAB ; si aucune fusée n'est ouverte, ce champ vaut `null`, et
     les instructions demandent d'exporter d'abord une petite fusée.
2. **Validation de l'import.**
   - Toute pièce absente de `Stats.AvailableParts` provoque un refus.
   - Une ressource dont la quantité dépasse la capacité de la pièce est **ramenée à la capacité**,
     et cette correction est signalée dans la ligne d'état et dans le log.
   - Les données de modules et les variantes ne sont pas modifiées : c'est le chargeur du jeu qui
     les valide.
3. **Autres assemblages du VAB.**
   - L'import ne remplace que la fusée principale et laisse les assemblages détachés tels quels.
   - La sauvegarde `before_import_…` est un instantané de **tout** l'atelier (`GenerateSnapshot`),
     pour que le retour en arrière soit complet.
4. **Fin du travail.** Implémenter, tester dans l'éditeur, installer, puis remettre à l'utilisateur
   la liste des tests en jeu. Chaque étape de l'import écrit une ligne de log
   (`[VabTools] import: …` : forme reconnue, pièces vérifiées, sauvegarde, méthode de chargement,
   résultat). La méthode de chargement est choisie au plan d'après la doc, avec repli sur l'autre
   si la première échoue. On l'ajustera après le premier essai en jeu.

## Laissé au constructeur

- La construction exacte de la fenêtre (UXML ou code).
- La façon de détecter la version du jeu et du mod.
- Le choix entre `LoadSerializedAssembly` et `LoadFromFile` après vérification.
- Les valeurs par défaut d'arrondi.
