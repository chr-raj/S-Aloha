# 2026-11-09 — KB-03, KB-16 : le seul rédacteur ne republie pas seul sa retouche

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : `KnowledgeController` surcharge `StatusAfterManagerEditAsync`. La retouche du
  titre, du résumé, du contenu ou du type d'un article publié par un gestionnaire non Admin
  qui en serait le seul rédacteur (KB-16, d'après le journal d'audit) le ramène en Brouillon,
  sans nouvelle version. Les autres gestionnaires gardent l'exception de KB-03 (la retouche
  vaut publication).
- **Pourquoi** : KB-16 n'était vérifié qu'à l'entrée dans Publié ; le seul rédacteur
  réécrivait l'article publié et le republiait sans relecture (#215). Décision tacite du
  2026-11-09 (option A), comme pour les changements (#210, #214).
- **Fichiers** : `backend/Modules/KnowledgeManagement/Controllers/KnowledgeController.cs`,
  `tests/SAloha.Api.Tests/KnowledgeLot3Tests.cs`
- **Documentation** : spécification des connaissances (KB-03, KB-16, transitions, scénarios)
- **Commit** : correction de #215
