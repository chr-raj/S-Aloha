# 2026-11-10 — INC-16 : la cascade ne recopie pas le code Doublon (#245)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : résoudre un incident parent avec le code *Doublon* ne résout plus ses enfants
  ouverts : ils restent ouverts, sans code, et la réponse les liste dans
  `unresolvedChildren` avec la raison (« Parent résolu en doublon (INC-08)… »). Les autres
  codes cascadent comme avant.
- **Pourquoi** : décision tacite du 2026-11-09 (palier 6, option B du dossier du
  2026-11-04) : la cascade ne fait jamais ce que l'API refuserait en direct ; INC-08 exige
  qu'un doublon soit relié à l'incident conservé, ce que la cascade ne sait pas faire. Même
  traitement que les enfants majeurs sans problème relié (INC-15, décision du 2026-10-26).
- **Fichiers** : `backend/Modules/IncidentManagement/Controllers/IncidentsController.cs`,
  `tests/SAloha.Api.Tests/IncidentLot3Tests.cs`
- **Documentation** : spécification des incidents (INC-16 et son scénario)
- **Commit** : correction de #245
