# SkySpaceAgency — leçons

Format : `[date] | ce qui a mal tourné | cause | règle pour l'éviter`. À relire au début de chaque session,
avant de toucher au code. Les faits généraux (comportement du jeu, d'un outil) vont aussi dans `D:\BDC`.

- [2026-10-30] | Aucun `tasks/todo.md` ni `tasks/lessons.md` tenu pendant des semaines de travail : une
  session perdue aurait laissé l'état dispersé entre `CLAUDE.md`, les commits et la mémoire. | Le suivi
  vivait dans le contexte de la session et dans le `CLAUDE.md` parent. | Tenir ces deux fichiers à jour à
  chaque étape terminée, dans le sous-module (jamais à la racine partagée avec la session KSP1PartMod).
- [2026-10-24] | Trois injections vers Duna ont brûlé environ 500 m/s chacune sans changer l'orbite. | En
  warp sur rails, le jeu précalcule un burn à direction fixe jusqu'à l'épuisement de l'étage, et ignore le
  SAS et la manette. | Jamais de warp sur rails pendant une poussée : warp physique au plus, ou garde
  `RailsBurn` avec sortie avant la fin.
- [2026-10-25] | La trajectoire sautait après un burn (SOI manquée, sortie de Kerbin à 54 Mm au lieu de 84). |
  Le warp sur rails partait juste après le burn, alors que le moteur poussait encore : +8,5 m/s fantômes. |
  Avant tout warp, gaz à 0 et aucune force moteur pendant 1 s et 10 images (garde de warp).
- [2026-10-25] | Les pilotes ne volaient plus du tout après une refonte. | Le test `if (job != null) return`
  restait vrai pour un job terminé. | Tester `job.Running`, pas l'existence du job. Après une refonte d'un
  pilote, vérifier qu'il démarre (log de la première phase).
- [2026-10-25] | `Lambert.Solve` annonçait de fausses réussites, jusqu'à 698 km d'erreur. | Perte de précision
  dans y(z) près des asymptotes à plusieurs tours. | Valider toute solution d'un solveur en propageant
  l'orbite obtenue, et tester les cas limites.
- [2026-10-30] | Correction près du périapside avortée en « diverging » après 1 s de poussée. | La gravité
  pendant le burn était comptée comme du Δv manquant ((v0 + Δv) − v). | Mesurer le Δv appliqué par rapport à
  la trajectoire sans poussée depuis l'allumage (propagation de Kepler).
- [2026-10-30] | Brake depuis une orbite ne faisait rien (« no collision detected »). | Le contrôle de départ
  acceptait, puis `TouchDown.checkDirection` attendait sans fin une vitesse vers le bas, qui reste
  horizontale en orbite. | Tester chaque pilote depuis chaque état de départ possible (orbite, survol, déjà
  en chute), et refuser avec un message plutôt qu'attendre sans fin.
- [2026-10-2x] | Interface : listes déroulantes géantes, case à cocher écrasée. | Le thème du jeu
  `KerbalUI.uss` donne `flex-grow: 1` aux listes déroulantes. | Fixer les tailles dans notre USS, et vérifier
  avec le harnais `Assets/Tests/UI` avant de livrer.
- [2026-10-2x] | Lignes du mod introuvables dans le log. | Elles sont dans `Player.log`
  (`%LOCALAPPDATA%Low/Intercept Games/Kerbal Space Program 2/`), pas dans `Ksp2-2.log`. | Toujours lire
  `Player.log`.
- [2026-10-30] | La copie de la DLL dans le jeu a échoué (« Permission denied »). | Le jeu tournait et
  verrouillait la DLL. | Avant d'installer : `tasklist | grep -i ksp2`. Si le jeu tourne, demander de le
  fermer.
- [2026-10-2x] | Un chemin Windows dans la liste de tests a perdu ses antislashs. | `sed` les interprète. |
  Pour du texte avec des antislashs, utiliser l'outil Edit ou une chaîne brute Python, jamais `sed`.
- [2026-10-30] | Risque de commiter le travail de l'autre session (KSP1PartMod) dans le dépôt parent. | Les
  deux sessions écrivent dans le même arbre de travail (`CLAUDE.md`, mémoire). | Vérifier `git diff` avant
  chaque `git add`, ne stager que ses propres chemins, et relire un fichier modifié sur le disque avant de
  l'éditer.
