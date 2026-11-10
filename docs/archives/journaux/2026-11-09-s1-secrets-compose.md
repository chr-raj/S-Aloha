# 2026-11-09 — S1 : aucun secret d'exemple dans docker-compose.yml

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : `docker-compose.yml` lit le mot de passe PostgreSQL (`POSTGRES_PASSWORD`, aussi
  dans la chaîne de connexion de l'API), la clé JWT (`JWT_KEY`) et la clé du coffre
  (`DISCOVERY_VAULT_KEY`) dans l'environnement, vides à défaut ; `docker-compose.demo.yml`
  porte les valeurs d'exemple, qui remplacent les valeurs vides à la fusion des deux fichiers.
- **Pourquoi** : premier geste de la part *code* de S1 (plan d'action § 5), décidé le
  2026-11-09 : `${VAR:?}` aurait fait échouer la fusion avec le fichier de démonstration
  (Compose interpole chaque fichier avant de les fusionner), donc le job « Recette » et la
  démonstration en une commande.
- **Vérifié** : `docker compose config` sur le fichier seul (valeurs vides), avec
  `docker-compose.demo.yml` (valeurs d'exemple), avec les variables fournies (leurs valeurs) ;
  l'API, lancée hors développement avec ces valeurs vides, refuse de démarrer faute de clé JWT
  (S7) ; avec une vraie clé JWT et un coffre vide, elle démarre et le coffre se déclare non
  configuré (il refuse d'enregistrer). La valeur vide l'emporte sur `appsettings.json`.
- **Fichiers** : `docker-compose.yml`, `docker-compose.demo.yml`,
  `backend/Core/Auth/SampleSecrets.cs` (commentaire), `tests/SAloha.Api.Tests/ComposeSecretsTests.cs`
- **Documentation** : exploitation (§ Secrets, démarrage rapide, configuration), sécurité
  (points de durcissement), plan d'action (S1), README
- **Commit** : S1, secrets hors de docker-compose.yml
