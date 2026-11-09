### Mise à jour

- **Image PostgreSQL** : `pgvector/pgvector:0.8.7-pg16` remplace `postgres:16-alpine`. Une
  base créée par l'ancienne image se sauvegarde puis se restaure (ou ses index se
  reconstruisent par `REINDEX DATABASE`) : le tri du texte diffère entre Alpine et Debian
  ([exploitation § Recherche](docs/reference/exploitation.md#recherche)). Un PostgreSQL
  géré doit proposer l'extension `vector`.
