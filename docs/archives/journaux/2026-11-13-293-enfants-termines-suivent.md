# 2026-11-13 — INC-16 : les enfants résolus ou clos suivent le parent déplacé (#293)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : un parent ne change plus de périmètre tant qu'il a des enfants **ouverts** dans
  un autre périmètre que celui visé (400, message corrigé) ; ses enfants résolus ou clos le
  suivent dans le même enregistrement, toujours rattachés, avec leurs déclarations
  réglementaires et leurs notifications. Le journal d'audit trace le changement de périmètre
  de chaque enfant.
- **Pourquoi** : un enfant résolu ne se détache plus (INC-16) et ne se déplace pas seul
  (PER-07) : un parent ouvert dans le mauvais périmètre ne pouvait plus être déplacé, pas même
  par un administrateur. Décision du mainteneur du 2026-11-10 (option A).
- **Vérifié** : `PerimeterRightsTests.PER_07_INC_16_un_enfant_termine_suit_le_parent_deplace_un_enfant_ouvert_le_retient`
  (enfant résolu qui suit avec sa déclaration, ses notifications et sa ligne d'historique ;
  enfant ouvert qui retient, puis reste dans son périmètre une fois détaché) et
  `PER_07_INC_16_un_enfant_termine_d_un_troisieme_perimetre_ne_suit_pas`, rouges sans la
  correction ; `PER_07_INC_16_un_parent_ne_change_pas_de_perimetre_tant_qu_il_a_des_enfants_rattaches`
  reste le témoin de l'enfant ouvert.
- **Reste** : un enfant terminé d'un troisième périmètre (lien hérité d'avant PER-07) ne suit
  pas : celui qui déplace n'est gestionnaire que du périmètre de départ et de celui d'arrivée.
  La décision ne dit rien de ce cas ; il est gardé tel qu'INC-16 décrit déjà le lien hérité.
- **Fichiers** : `backend/Modules/IncidentManagement/Controllers/IncidentsController.cs`,
  `tests/SAloha.Api.Tests/PerimeterRightsTests.cs`
- **Documentation** : spécification de la gestion des incidents (INC-16, scénario), périmètres
  (PER-07)
- **Commit** : correction de #293
