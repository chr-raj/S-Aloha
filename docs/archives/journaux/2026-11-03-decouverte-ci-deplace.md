# 2026-11-03 — Découverte : un actif rapproché ne suit pas son CI hors de son périmètre (PER-07)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : au scan suivant, un actif « Rapproché » ne met à jour son CI que si ce CI est
  toujours au périmètre de l'actif ; déplacé ailleurs (PER-07), l'actif repasse par le
  rapprochement, limité à son périmètre. Le sas (liste et fiche d'un actif) ne renvoie le CI
  rapproché que s'il est visible de l'utilisateur (PER-05). Un test d'API, qui échoue sans la
  correction.
- **Pourquoi** : la découverte d'un périmètre ne modifie ni ne décrit la CMDB d'un autre
  (PER-05, PER-06), comme le rapprochement manuel le refusait déjà.
- **Fichiers** : `backend/Modules/ServiceConfigurationManagement/Discovery/MatchingEngine.cs`,
  `backend/Modules/ServiceConfigurationManagement/Controllers/StagingController.cs`,
  `tests/SAloha.Api.Tests/DiscoveryTests.cs`
- **Documentation** : gestion de la configuration (transitions du sas), CHANGELOG (§ Sécurité)
- **Commit** : PER-07 (découverte)
