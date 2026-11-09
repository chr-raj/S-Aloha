### Modifié

- **Recherche** : la similarité des vecteurs est calculée dans PostgreSQL par pgvector
  (index HNSW) au lieu de la mémoire de l'API ; la mémoire de l'API ne croît plus avec
  l'index, et tout processus qui lit la base voit le même index (ADR-0022, RAG-05).
- **Exigence** : pgvector 0.8.0 ou plus (parcours itératif de l'index) ; avec une version
  plus ancienne, la requête vectorielle est journalisée et la recherche reste lexicale.
