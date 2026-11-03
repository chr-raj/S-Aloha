# 2026-11-03 — Garde-fous : la classe de risque n'est plus annulée par une étiquette

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : la concurrence du workflow Garde-fous passe du workflow à chaque job. Une
  étiquette posée juste après l'ouverture d'une PR annulait toute l'exécution née de
  l'ouverture, job « Classe de risque » compris ; ce job ne tournant pas sur `labeled`, la
  PR restait sans étiquette `risque-*`.
- **Pourquoi** : constaté sur #243 ; une PR sans classe de risque échappe au classement
  dont dépendent les règles de fusion.
- **Fichiers** : `.github/workflows/garde-fous.yml`
- **Documentation** : aucune autre (comportement attendu inchangé)
- **Commit** : classe de risque non annulée
