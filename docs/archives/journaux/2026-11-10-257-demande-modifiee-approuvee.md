# 2026-11-10 — REQ-04 : une demande modifiée après l'approbation repart à l'approbation (#257)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : modifier l'objet demandé, le service ou le bénéficiaire d'une demande Approuvée
  ou En cours la ramène à Soumise : l'approbation est effacée et les approbateurs sont
  notifiés. Sur une demande Satisfaite ou Close, ces champs ne se modifient plus (400). Le
  bénéficiaire ne change que par le demandeur ou un gestionnaire (400 sinon). Le titre et la
  description restent libres. Nouveau point d'extension `StatusAfterValidation` du
  contrôleur commun : il décide du statut après la recopie et la validation, sur les valeurs
  normalisées (objet repris du modèle, service résolu), et il ne change rien pour les autres
  pratiques.
- **Pourquoi** : décision tacite du 2026-11-09 (palier 6, option D du dossier du
  2026-11-04). Une approbation doit couvrir ce qui a été approuvé, et un tiers ne doit pas
  pouvoir se faire bénéficiaire pour contester (REQ-10) ou noter (REQ-32). Le changement
  suit déjà la même règle (#209, #214).
- **Fichiers** : `backend/Core/Records/RecordController.cs`,
  `backend/Modules/ServiceRequestManagement/Controllers/ServiceRequestsController.cs`,
  `tests/SAloha.Api.Tests/RequestReworkTests.cs`
- **Documentation** : spécification des demandes (§ 2, § 4, REQ-04, REQ-07)
- **Commit** : correction de #257
