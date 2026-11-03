# 2026-11-03 — #239 : CORS, une origine configurée avec une barre oblique finale est admise

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : les valeurs de `Cors:AllowedOrigins` perdent leurs espaces et leur barre oblique
  finale à la lecture ; `https://frontend.example/` admet désormais l'origine
  `https://frontend.example`.
- **Pourquoi** : l'en-tête `Origin` n'a jamais de barre finale et `WithOrigins` compare la
  valeur telle quelle : l'origine était refusée sans message alors que l'exploitant la croyait
  ouverte.
- **Fichiers** : `backend/Program.cs`, `tests/SAloha.Api.Tests/CorsTests.cs`
- **Documentation** : exploitation § configuration (`Cors:AllowedOrigins`)
- **Commit** : correction de #239
