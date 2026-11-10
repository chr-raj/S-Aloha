### Sécurité

- **Aucun secret d'exemple dans `docker-compose.yml`** (S1) : le mot de passe PostgreSQL, la
  clé JWT et la clé du coffre se fournissent par l'environnement (`POSTGRES_PASSWORD`,
  `JWT_KEY`, `DISCOVERY_VAULT_KEY`, par exemple `docker compose --env-file …`) ; les valeurs
  d'exemple ne restent que dans `docker-compose.demo.yml`. Une installation lancée avec le
  seul `docker-compose.yml` doit désormais fournir ces trois variables.
