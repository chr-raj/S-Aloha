# 2026-11-03 — Hygiène bloquante

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : le job **Hygiène** passe en mode `--strict` : un trailer autre que
  `Signed-off-by` ou un terme réservé fait échouer la pull request, au lieu d'un simple
  avertissement. Le contrôle rejoint les contrôles exigés sur `main`.
- **Pourquoi** : un avertissement se lit trop tard ; une attribution ou un nom interne
  publié par erreur ne doit plus pouvoir être fusionné.
- **Fichiers** : `.github/workflows/ci.yml`
- **Documentation** : architecture § Intégration continue, CONTRIBUTING
- **Commit** : hygiène bloquante
