# 2026-11-05 — CFG-55 : une relation proposée ne relie que des CI du périmètre de la proposition

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : `GET /api/discovery/relations` et les réponses d'acceptation et de rejet ne
  renvoient le CI rapproché d'un actif que s'il est visible de l'appelant ; une proposition
  n'est prête, et ne s'accepte, que si ses deux CI sont au périmètre de la proposition.
- **Pourquoi** : le rapprochement d'un actif n'est remis à jour qu'au scan suivant ; entre
  un déplacement de CI (PER-07) et ce scan, la proposition doit tenir les règles de
  périmètre (PER-05, PER-06) comme le sas des actifs.
- **Fichiers** :
  `backend/Modules/ServiceConfigurationManagement/Controllers/RelationProposalsController.cs`,
  `tests/SAloha.Api.Tests/DiscoveryTests.cs`
- **Documentation** : spécification de la gestion de la configuration (CFG-55)
- **Commit** : CFG-55, périmètre des relations proposées
