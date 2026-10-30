# SkySpaceAgency — état et reste à faire

Lire avec `tasks/lessons.md` et la section SkySpaceAgency de `D:\KSPReduxModding\CLAUDE.md` : c'est cette
section qui donne l'état détaillé (ce qui est vérifié en jeu, ce qui ne l'est pas, les lignes de log à
lire). Ce fichier ne garde que le fil des tâches. Mis à jour le 2026-10-30.

## Installé dans le jeu, pas encore testé en jeu

Le zip installé le 2026-10-30 contient tout ce qui suit (DLL vérifiée par `cmp`, réglages json, `vab/` et
`plans/` gardés). La sauvegarde de l'installation précédente est dans le scratchpad de la session, donc
temporaire.

- [ ] **Liste de tests en jeu**, sections 0 à 16 : `docs/testing/2026-10-24-transfers-in-game-checklist.md`.
  - 4.1 Transfer ;
  - 4.2 Lift « Prepare transfer » ;
  - 4.3 Rendezvous et amarrage ;
  - warp physique et warp sur rails pendant les burns ;
  - atterrissage avec un étage choisi.
- [ ] **Onglet Orbit depuis un survol** : capture d'abord (commit `1c85b34`). Lignes `[OrbitUI] capture:`.
- [ ] **Correction près du périapside**, où la gravité était comptée comme du Δv manquant (commit `7a2d1a8`).
- [ ] **Section 16 de la liste** : désorbitation automatique, refus faute de Δv, chauffe de rentrée planifiée
  et garde à 75 % (commits `77297d2`..`0949fdf`). Lignes `[Landing] deorbit:` et `[Landing] heat:`.

## Questions ouvertes à l'utilisateur

- [ ] **Chauffe de rentrée** : le plan vise 70 % de la température max, pas les 90 % convenus. L'agent a
  fait ce choix pour que la garde de 75 % ne se déclenche pas à chaque entrée. L'utilisateur n'a pas encore
  dit s'il le garde.

## Ensuite, dans l'ordre

1. [ ] Corriger ce que les tests en jeu révèlent.
2. [ ] Sous-projet 4.4 : Lift vers un rendez-vous.
3. [ ] Sous-projet 4.5 : un seul bouton pour transfert, rendez-vous et amarrage.
4. [ ] Sous-projet 4.6 : assistance gravitationnelle.
   - Spec des trois : `docs/superpowers/specs/2026-10-22-transfers-rendezvous-docking-design.md`.
5. [ ] **Feuille de route** `docs/superpowers/roadmap-booster-return-video-scripting.md`, à commencer par la
   phase 0 obligatoire (scanner le jeu et Redux). Elle couvre :
   - R1 : point de retour ;
   - R1b : nos pièces recovery computer et ordinateur de script ;
   - R2 : onglet Return ;
   - R3 : Lift qui prévoit le retour ;
   - R4 / R4b : pause, bascule, rejeu et booster gardé ;
   - R5 : vidéo ;
   - R6 : langage de script ;
   - R7 : overlay.
6. [ ] **Pièces** : écrire notre script Blender de cuisson et d'export des textures Redux, sans plugin (voir
   `D:\KSPReduxModding\docs\redux-part-modeling-with-blender.md`).

## Connaissances à reverser dans `D:\BDC`

La section « À écrire » de `D:\BDC\Projets\SkySpaceAgency.md` liste les notes à créer :
- le warp sur rails précalcule un burn à direction fixe ;
- le modèle thermique de Redux ;
- les gaz de l'ancien vaisseau ne sont pas coupés ;
- les règles du warp physique.
