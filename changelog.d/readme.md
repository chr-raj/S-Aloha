# Notes du journal des versions en attente

Une note par fichier, `<identifiant>.md` (l'issue ou la règle traitée, en minuscules,
chiffres et tirets), écrite dans la PR du changement. La première ligne donne la rubrique
de « Non publié » (`### Ajouté`, `### Modifié`, `### Corrigé`, `### Sécurité`, `### Retiré`
ou `### Mise à jour`), la suite est la liste à puces de la note :

```markdown
### Corrigé

- **Exports CSV** : une cellule à retour chariot est mise entre guillemets (REP-08).
```

`python3 scripts/changelog.py` vérifie les notes ; `--assembler` les verse dans
[`CHANGELOG.md`](../CHANGELOG.md) et supprime leurs fichiers, à la passe de publication du
lundi ([protocole](../docs/protocole-documentation.md#3-journaliser-les-sujets-traités)).
