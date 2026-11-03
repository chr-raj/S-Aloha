# 2026-11-03 — Un fichier par sujet pour le journal et le journal des versions

<sub>[← Archives](../readme.md)</sub>

- **Quoi** : chaque PR écrit son entrée de journal dans
  `docs/archives/journaux/AAAA-MM-JJ-identifiant.md` et sa note du journal des versions
  dans `changelog.d/<identifiant>.md` ; plus d'index des journaux à compléter.
  `scripts/changelog.py` vérifie les notes en CI et les verse dans « Non publié » le lundi ;
  `scripts/check-docs.py` contrôle le nom des entrées.
- **Pourquoi** : les 16 PR ouvertes le 2026-11-03 modifiaient toutes
  `docs/archives/readme.md`, 12 d'entre elles `CHANGELOG.md` et chaque lot le même journal
  du jour ; chaque fusion mettait les suivantes en conflit (#211, #212 et #213 l'étaient).
- **Fichiers** : `scripts/changelog.py`, `scripts/check-docs.py`, `.github/workflows/ci.yml`,
  `changelog.d/`
- **Documentation** : protocole documentaire (§ 2 et § 3), `docs/readme.md` (où va quoi),
  index des archives, en-tête de `CHANGELOG.md`
- **Commit** : journal et journal des versions par sujet
