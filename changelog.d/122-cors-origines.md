### Sécurité

- **CORS limité aux origines configurées** (S3) : l'API n'accepte plus les requêtes de
  n'importe quelle origine. `Cors:AllowedOrigins` liste les origines externes admises ; vide
  par défaut, ce qui suffit au frontend fourni (même origine, par nginx).
