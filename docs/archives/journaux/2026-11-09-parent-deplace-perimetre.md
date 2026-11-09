# 2026-11-09 — PER-07 : un incident parent ne quitte pas le périmètre de ses enfants

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : `PUT /api/incidents/{id}` refuse (400) de changer le périmètre d'un incident qui
  a des enfants rattachés restant dans un autre périmètre que la destination ; le message
  donne leur nombre (pas leurs références, qui ne sont pas forcément visibles) et invite à
  les détacher d'abord. Même point d'extension `CheckMoveAsync` que pour l'enfant
  (2026-11-05).
- **Pourquoi** : décision du mainteneur du 2026-11-09, symétrique de celle du 2026-11-04 :
  aucun lien parent / enfant entre deux périmètres ne se crée par l'API.
- **Fichiers** : `backend/Modules/IncidentManagement/Controllers/IncidentsController.cs`,
  `tests/SAloha.Api.Tests/PerimeterRightsTests.cs`
- **Documentation** : référence des périmètres (PER-07), spécification des incidents (INC-16)
- **Commit** : PER-07, déplacement d'un incident parent
