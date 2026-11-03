#!/usr/bin/env python3
"""Notes du journal des versions, une par fichier.

Chaque PR dépose sa note dans `changelog.d/<identifiant>.md` au lieu d'écrire dans
`CHANGELOG.md` : deux PR ouvertes en même temps ne se gênent plus. Le fichier commence
par la rubrique, suivie des puces de la note :

    ### Corrigé

    - **Exports CSV** : une cellule à retour chariot est mise entre guillemets (REP-08).

    python3 scripts/changelog.py              # vérifie les notes en attente
    python3 scripts/changelog.py --assembler  # les verse dans « Non publié », puis les supprime

Sortie non nulle si une note est mal formée.
"""
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[1]
NOTES = REPO / 'changelog.d'
CHANGELOG = REPO / 'CHANGELOG.md'

# Rubriques de « Non publié », dans l'ordre du journal des versions.
RUBRIQUES = ('Ajouté', 'Modifié', 'Corrigé', 'Sécurité', 'Retiré', 'Mise à jour')

NOM = re.compile(r'^[a-z0-9][a-z0-9-]*\.md$')


def notes():
    return sorted(p for p in NOTES.glob('*.md') if p.name != 'readme.md')


def lire(p):
    """(rubrique, corps) d'une note, ou (None, message d'erreur)."""
    if not NOM.match(p.name):
        return None, 'nom attendu : identifiant en minuscules, chiffres et tirets, puis .md'
    lignes = p.read_text(encoding='utf-8').strip().split('\n')
    m = re.match(r'^### (.+)$', lignes[0])
    if not m or m.group(1).strip() not in RUBRIQUES:
        return None, f'première ligne attendue : « ### <rubrique> », parmi {", ".join(RUBRIQUES)}'
    corps = '\n'.join(lignes[1:]).strip()
    if not corps.startswith('- '):
        return None, 'la note est une liste à puces sous la rubrique'
    return m.group(1).strip(), corps


def assembler(lues):
    texte = CHANGELOG.read_text(encoding='utf-8')
    debut = texte.index('\n## Non publié\n')
    fin = texte.index('\n## ', debut + 1)
    section = texte[debut:fin]
    for rubrique in reversed(RUBRIQUES):
        corps = [c for _, r, c in lues if r == rubrique]
        if not corps:
            continue
        titre = f'\n### {rubrique}\n'
        if titre not in section:
            # Rubrique absente : on l'ajoute à sa place dans l'ordre de RUBRIQUES.
            suivantes = [s for s in RUBRIQUES[RUBRIQUES.index(rubrique) + 1:] if f'\n### {s}\n' in section]
            pos = section.index(f'\n### {suivantes[0]}\n') if suivantes else len(section)
            section = section[:pos] + titre + section[pos:]
        pos = section.index(titre) + len(titre)
        section = section[:pos] + '\n' + '\n\n'.join(corps) + '\n' + section[pos:]
    CHANGELOG.write_text(texte[:debut] + section + texte[fin:], encoding='utf-8')
    for p, _, _ in lues:
        p.unlink()


def main(argv):
    lues, problemes = [], []
    for p in notes():
        rubrique, corps = lire(p)
        if rubrique is None:
            problemes.append(f'changelog.d/{p.name}  {corps}')
        else:
            lues.append((p, rubrique, corps))
    for pb in problemes:
        print(pb)
    if problemes:
        print(f'✖ {len(problemes)} note(s) mal formée(s)')
        return 1
    if '--assembler' in argv:
        assembler(lues)
        print(f'✔ {len(lues)} note(s) versée(s) dans « Non publié » de CHANGELOG.md')
    else:
        print(f'✔ {len(lues)} note(s) en attente, bien formées')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
