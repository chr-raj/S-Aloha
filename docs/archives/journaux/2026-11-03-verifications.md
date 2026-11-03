# 2026-11-03 — Vérifications automatiques : preuve, garde-fous, risque, filet

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : nouveaux contrôles de pull request — **Preuve** (les tests de la PR échouent
  sur le code de la base), **Garde-fous** (aucun test supprimé, réduit ou désactivé ;
  outillage de vérification modifié seulement avec l'étiquette `outillage`), **Dépendances
  ajoutées** (vulnérabilités hautes, licences incompatibles), **Workflows** (actionlint,
  zizmor) ; classe de risque posée en étiquette ; issue `main-rouge` quand la CI de `main`
  échoue. Quatre constats de zizmor corrigés : deux injections par expression
  (`github.actor` dans `docker login`, `ci.yml` et `publication.yml`), condition de bot
  falsifiable (`dependabot.yml`, remplacée par la lecture des auteurs des commits),
  déclencheur `issue_comment` justifié (`commentaires.yml`).
- **Pourquoi** : la vérification d'une PR reposait sur des verdicts déclarés ; la preuve du
  correctif, l'intégrité des tests et la classe de risque deviennent des faits calculés et
  rejoués à chaque push.
- **Fichiers** : `scripts/preuve.py`, `scripts/garde-fous.py`, `.github/workflows/ci.yml`,
  `.github/workflows/garde-fous.yml`, `.github/workflows/filet.yml`,
  `.github/workflows/dependabot.yml`, `.github/workflows/publication.yml`,
  `.github/workflows/commentaires.yml`
- **Documentation** : architecture § Intégration continue, sécurité § Contrôles du dépôt,
  CONTRIBUTING (Preuve et garde-fous)
- **Commit** : vérifications automatiques
