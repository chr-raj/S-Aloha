### Ajouté

- **HTTPS** (S2) : `docker-compose.https.yml` place un reverse proxy TLS (Caddy) devant
  S-Aloha : autorité interne, Let's Encrypt ou certificat fourni ; seuls les ports 80
  (redirection) et 443 sont publiés ; le cookie de session devient `Secure`
  ([exploitation § HTTPS](docs/reference/exploitation.md#https)).
- **LDAPS avec l'autorité de l'annuaire** (S2) : `Ldap:CaCertificatePath` désigne le fichier PEM
  de l'autorité qui a émis le certificat du contrôleur de domaine, pour la connexion et la
  découverte Active Directory. La validation du certificat et du nom n'est jamais désactivée
  ([exploitation § LDAPS](docs/reference/exploitation.md#ldaps)).
