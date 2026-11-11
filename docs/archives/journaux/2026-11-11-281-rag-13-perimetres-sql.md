# 2026-11-11 — RAG-13 : les périmètres filtrés dans la requête de recherche (#281)

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : la recherche et les cas similaires filtrent les périmètres de l'utilisateur dans
  les requêtes lexicale (EF) et vectorielle (SQL pgvector), par sous-requête sur
  l'enregistrement vivant (article publié ou de ses périmètres, problème ou incident de ses
  périmètres). `SearchController.VisibleAsync` et les « trois fois plus de candidats »
  disparaissent ; l'Admin n'a pas de filtre.
- **Pourquoi** : filtré après le classement, un utilisateur aux périmètres étroits recevait une
  liste courte, voire vide, alors que des résultats visibles existaient plus loin ; accord du
  mainteneur du 2026-11-09 (#281), possible depuis l'ADR-0022. Préalable d'un serveur MCP.
- **Vérifié** : `RAG_13_le_filtre_des_perimetres_precede_le_classement` (quatre incidents du
  Siège en tête, un de l'Usine après ; l'utilisateur de l'Usine, limite 1, le trouve ; un
  incident déplacé se trouve depuis le nouveau périmètre aussitôt) échoue sur `main` et passe ;
  `SearchTests` et `PER_09` verts. Plan d'exécution (essai jetable, 3 000 passages) : `Index
  Scan using "IX_SearchPassages_EmbeddingVector"` avec le filtre, les sous-requêtes en
  ensembles hachés calculés une fois.
- **Fichiers** : `backend/Core/Search/HybridSearch.cs`, `backend/Core/Search/SearchController.cs`,
  `tests/SAloha.Api.Tests/PerimeterViewsTests.cs`
- **Documentation** : recherche (RAG-13), ADR-0022 (suite faite)
- **Commit** : #281, RAG-13
