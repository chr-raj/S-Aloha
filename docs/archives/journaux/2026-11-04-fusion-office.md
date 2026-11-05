# 2026-11-04 — L'étiquette `fusion-office` n'est plus perdue

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : le workflow `fusion-office.yml` repose l'étiquette `fusion-office` sur une PR
  fusionnée qui ne la porte pas, dès que le compte du mainteneur y commente « Fusion
  d'office ». Il ne lit que l'API et n'exécute rien du dépôt.
- **Pourquoi** : #254 a été fusionnée le 2026-11-04 avec ce commentaire mais sans
  l'étiquette, alors que #252 et #253, fusionnées dans la même passe, la portent. Le suivi des
  fusions d'office s'appuie sur cette étiquette.
- **Fichiers** : `.github/workflows/fusion-office.yml`
- **Documentation** : architecture § Intégration continue
- **Commit** : étiquette `fusion-office` reposée d'office
