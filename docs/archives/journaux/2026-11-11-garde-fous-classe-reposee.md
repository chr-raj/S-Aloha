# 2026-11-11 — Garde-fous : la classe de risque survit à une étiquette posée par-dessus

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : le job « Classe de risque » du workflow Garde-fous tourne aussi sur `labeled` et
  `unlabeled`, et devient idempotent (il lit les étiquettes présentes, ne retire que les
  classes fausses, ne repose la bonne que si elle manque). Une écriture qui remplace la
  liste des étiquettes d'une PR efface la classe ; le recalcul la remet.
- **Pourquoi** : le 2026-11-11 à 03:21 UTC, la pose d'`a-verifier` sur #298 et #299 a retiré
  `risque-sensible` et `risque-code` à la seconde où le workflow les avait posées ; sans
  classe, la vérification ne peut pas conclure à la fusion. Le dépôt étant public, ces
  exécutions supplémentaires ne coûtent rien ; reposer une étiquette déjà présente n'émet
  pas d'événement, donc pas de boucle.
- **Fichiers** : `.github/workflows/garde-fous.yml`
- **Documentation** : aucune autre (comportement attendu inchangé : chaque PR porte sa classe)
- **Commit** : classe de risque reposée
