# 2026-11-04 — INC-16 : la résolution du parent ne résout que les enfants de son périmètre

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : à la résolution d'un incident parent, seuls ses enfants ouverts du même
  périmètre que lui passent Résolu ; un enfant changé de périmètre (PER-07) garde son lien
  au parent mais reste tel quel.
- **Pourquoi** : le rattachement exige le même périmètre (INC-16) ; la cascade le respecte
  désormais aussi, comme les droits par périmètre (PER-05, PER-06).
- **Fichiers** : `backend/Modules/IncidentManagement/Controllers/IncidentsController.cs`,
  `tests/SAloha.Api.Tests/PerimeterRightsTests.cs`
- **Documentation** : spécification des incidents (INC-16)
- **Commit** : INC-16, périmètre de la cascade
