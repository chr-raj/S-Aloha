# 2026-11-13 — Réaffectation d'un enregistrement engagé : le responsable en place ou un gestionnaire (#269)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : `PUT /api/changes/{id}`, `/api/requests/{id}` et `/api/improvements/{id}` refusent
  (400) qu'un tiers change le responsable d'un changement Autorisé, Planifié ou Mis en œuvre,
  d'une demande En cours, ou le porteur d'une amélioration Validée ou En cours. Le responsable
  d'avant l'envoi, un membre du groupe responsable et un gestionnaire du périmètre réaffectent.
  Un enregistrement sans responsable reste à prendre, et le reste de la fiche se modifie comme
  avant.
- **Pourquoi** : décision du mainteneur du 2026-11-09 (option A du dossier de #269). Les
  transitions réservées au responsable (CHG-14, REQ-25, CSI-11) regardaient le responsable
  d'avant l'envoi, mais un tiers se désignait d'un premier envoi puis transitait au second.
  Les § 2 des trois pratiques disaient encore ces gestes ouverts à tout User, et CHG-07 que le
  responsable « se modifie librement ».
- **Vérifié** : `OwnerReassignmentTests`, un test par pratique (tiers refusé, y compris vers un
  groupe ou en retirant le responsable ; responsable, membre du groupe et gestionnaire
  acceptés ; avant l'engagement, le responsable se modifie librement), rouges sans la garde.
- **À relire** : le cas « sans responsable » n'était pas dans le dossier ; il reste ouvert à
  tous (se désigner n'y prend le travail de personne).
- **Fichiers** : `backend/Core/Records/RecordController.cs` (`ReassignedByThirdParty`),
  `ChangesController.cs`, `ServiceRequestsController.cs`, `ImprovementsController.cs`,
  `tests/SAloha.Api.Tests/OwnerReassignmentTests.cs`
- **Documentation** : spécifications des changements (§ 2, CHG-07), des demandes (§ 2, REQ-25)
  et de l'amélioration continue (§ 2, CSI-11)
- **Commit** : correction de #269
