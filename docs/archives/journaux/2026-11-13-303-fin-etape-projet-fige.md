# 2026-11-13 — PRJ-07 : la fin d'étape ne se valide plus sur un projet clôturé ou abandonné (#303)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : `POST /api/projects/{id}/phases/{étape}/approve` passe par la garde commune des
  registres du projet (`ProjectPartController.Load`, en écriture) : 400 « … il ne se modifie
  plus » sur un projet Clôturé ou Abandonné, 400 « Composant non activé » sur un projet sans
  phases ni étapes, puis le droit du chef de projet comme avant. L'écran ne propose plus
  « Valider la fin d'étape » sur un projet figé.
- **Pourquoi** : la route d'action chargeait le projet sans cette garde ; un chef de projet
  terminait une étape et ouvrait la suivante sur un projet terminé (revue du 2026-11-12).
- **Vérifié** : `ProjectManagementTests.PRJ_07_la_fin_d_etape_ne_se_valide_plus_sur_un_projet_fige`
  (Clôturé, Abandonné ; chef de projet et gestionnaire ; étapes inchangées),
  `PRJ_03_la_fin_d_etape_ne_se_valide_pas_sans_le_composant`, et côté interface
  `Project.test.jsx` (« Étapes de gestion ») ; tous rouges sans la correction.
  `Prince2_business_case_etapes_et_registres` reste le témoin du cas permis.
- **Reste** : les flèches de rang du backlog restent affichées sur un projet figé (l'API les
  refuse déjà) ; le bouton « Burndown », en lecture, doit rester.
- **Fichiers** : `backend/Modules/ProjectManagement/Controllers/ProjectPartController.cs`,
  `ProjectPartsControllers.cs`, `frontend/src/modules/project-management/components/Registers.jsx`,
  `Project.test.jsx`, `tests/SAloha.Api.Tests/ProjectManagementTests.cs`
- **Documentation** : spécification de la gestion de projet (PRJ-07, § 9)
- **Commit** : correction de #303
