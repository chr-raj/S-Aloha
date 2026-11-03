### Ajouté

- **Sondes de santé de l'API** (O1) : `GET /health/live` (le processus répond) et
  `GET /health/ready` (la base aussi, 503 sinon), sans jeton et sans détail, pour Docker et
  Kubernetes ; healthcheck du service `api` dans `docker-compose.yml`.
