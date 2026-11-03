# 2026-11-03 — CI de main : chaque commit garde son exécution

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : le groupe de concurrence de `ci.yml` est par commit sur `main` (sans
  annulation) et par branche sur une pull request (avec annulation). Le job *Images edge* a
  son propre groupe, une publication à la fois, et ne publie que si son commit est encore
  la tête de `main`.
- **Pourquoi** : le 2026-11-03, trois fusions automatiques en quelques minutes (#213, #232,
  #235) ont annulé les exécutions des deux premières : seule la dernière a eu Recette et DCO,
  et le filet (`filet.yml`) ne voit pas un commit dont l'exécution est annulée.
- **Fichiers** : `.github/workflows/ci.yml`
- **Documentation** : architecture § Intégration continue
- **Commit** : concurrence de la CI sur main
