# 2026-11-10 — REQ-13 : une demande ne passe En cours qu'avec un responsable (#227)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : `PUT /api/requests/{id}` refuse (400 « Désigner le responsable… ») de passer En
  cours une demande approuvée sans responsable. Nouvelle règle REQ-13, comptée dans la
  synthèse des pratiques. Le reliquat documentaire de l'issue est soldé : la ligne
  « Réalisée → … » des transitions de l'amélioration continue passe à ✅ CSI-11 (statut
  final tenu par le graphe de transitions) ; la ligne « En cours → Réalisée » l'était déjà.
- **Pourquoi** : décision tacite du 2026-11-09 (palier 6, option A du dossier du
  2026-11-04) : une demande en cours a quelqu'un pour la satisfaire. Une demande du
  catalogue reçoit déjà le groupe d'exécution de son modèle à l'approbation (REQ-23) : le
  geste de plus ne vaut que pour une demande libre ou un modèle sans groupe. Le chargeur de
  démonstration ne passe aucune demande En cours.
- **Fichiers** : `backend/Modules/ServiceRequestManagement/Controllers/ServiceRequestsController.cs`,
  `tests/SAloha.Api.Tests/ItilModulesTests.cs` ; `RequestTasksTests.cs` et `DashboardTests.cs`
  renvoient désormais le responsable (groupe d'exécution) avec la fiche, qu'un envoi sans
  responsable effaçait avant de passer En cours
- **Documentation** : spécification des demandes (§ 4, REQ-13), de l'amélioration continue
  (§ 4), synthèse des pratiques
- **Commit** : correction de #227
