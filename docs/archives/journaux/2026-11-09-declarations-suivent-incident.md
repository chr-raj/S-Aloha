# 2026-11-09 — DRG-01 : les déclarations réglementaires suivent leur incident déplacé

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : au changement de périmètre d'un incident (`PUT /api/incidents/{id}`, PER-07),
  ses déclarations réglementaires prennent le nouveau périmètre dans le même enregistrement ;
  `PUT /api/regulatory-notifications/{id}` refuse (400) de déplacer une déclaration hors du
  périmètre de son incident (la validation de DRG-01 lisait le périmètre d'avant le
  déplacement).
- **Pourquoi** : la lecture des déclarations filtre sur le périmètre copié dans chacune ; il
  restait celui d'avant le déplacement de l'incident, comme pour les notifications avant
  SOC-23 (PER-05).
- **Fichiers** : `backend/Modules/IncidentManagement/Controllers/IncidentsController.cs`,
  `backend/Modules/RegulatoryNotifications/Controllers/RegulatoryNotificationsController.cs`,
  `tests/SAloha.Api.Tests/PerimeterRightsTests.cs`
- **Documentation** : spécification des déclarations réglementaires (DRG-01)
- **Commit** : DRG-01, déclarations d'un incident déplacé
