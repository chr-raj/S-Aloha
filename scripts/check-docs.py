#!/usr/bin/env python3
"""Santé de la base documentaire.

Trois contrôles, là où la documentation peut mentir sans qu'on le voie :

  1. liens markdown relatifs   `](chemin.md)`      → la cible existe-t-elle ?
  2. ancres                    `](fichier.md#x)`   → le titre existe-t-il ?
  3. chemins du dépôt cités    dans le texte ET dans les blocs de code

Le troisième est le plus utile : un chemin cité dans un bloc de code reste invisible
pour un contrôleur de liens markdown, et survit donc à toutes les réorganisations.

    python3 scripts/check-docs.py                       # toute la doc
    python3 scripts/check-docs.py docs/reference/frontend.md

Sortie non nulle si au moins un problème est trouvé.
"""
import os
import pathlib
import posixpath
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[1]

# Racines qui désignent un chemin du dépôt.
ROOTS = ('backend/', 'frontend/', 'docs/', 'scripts/', '.github/', 'changelog.d/')
BARE = ('docker-compose.yml', 'README.md')

# Jetons génériques : gabarits, globs, exemples.
PLACEHOLDER = re.compile(r'[<>*{}$]|\.\.\.|YYYY|000X|:\w+$')

PATH_TOKEN = re.compile(r'(?<![\w./-])((?:\.?[\w.-]+/)+[\w.-]+)')
MD_LINK = re.compile(r'\]\(([^)]+)\)')

# Une entrée de journal par sujet (protocole § 3).
JOURNAL = re.compile(r'^\d{4}-\d{2}-\d{2}-[a-z0-9][a-z0-9-]*\.md$')

# Références délibérées à ce qui N'EXISTE PLUS — la phrase dit justement que le
# chemin a disparu (ADR et constats racontent l'arborescence d'avant).
INTENTIONNEL = {
    'docs/rules.md',            # devenu docs/reference/regles-metier.md (2026-10-06)
    'docs/architecture.md',     # devenu docs/reference/architecture.md (2026-10-06)
}


def slug(title):
    """Identifiant d'ancre GitHub : minuscules, ponctuation retirée, espaces → tirets."""
    s = re.sub(r'[^\w\s-]', '', title.strip().lower(), flags=re.UNICODE)
    return s.replace(' ', '-')


def headings(text):
    out, fence = set(), False
    for line in text.split('\n'):
        if line.lstrip().startswith('```'):
            fence = not fence
            continue
        if fence:
            continue
        m = re.match(r'^#{1,6}\s+(.*)', line)
        if m:
            out.add(slug(m.group(1)))
    return out


def main(argv):
    os.chdir(REPO)
    files = argv or sorted(str(p) for p in pathlib.Path('docs').rglob('*.md')) + ['README.md']
    texts = {f: pathlib.Path(f).read_text(encoding='utf-8') for f in files if pathlib.Path(f).is_file()}

    problems = []
    n_links = n_anchors = n_paths = 0

    for f, text in texts.items():
        base = posixpath.dirname(f)

        for m in MD_LINK.finditer(text):
            target = m.group(1).split(' ')[0]
            if re.match(r'^(https?:|mailto:|tel:|#)', target):
                continue
            path, _, frag = target.partition('#')
            path = path.replace('%20', ' ')
            if path:
                n_links += 1
                resolved = posixpath.normpath(posixpath.join(base, path))
                if not os.path.exists(resolved):
                    problems.append(f'{f}  lien cassé          → {target}')
                    continue
            else:
                resolved = f
            if frag:
                n_anchors += 1
                if resolved in texts and slug(frag) not in headings(texts[resolved]):
                    problems.append(f'{f}  ancre inexistante   → {target}')

        if '/archives/journaux/' in f and not JOURNAL.match(posixpath.basename(f)):
            problems.append(f'{f}  nom attendu         → AAAA-MM-JJ-identifiant.md')

        # Les archives sont des relevés datés : elles citent forcément des fichiers
        # déplacés ou supprimés depuis. Leurs liens markdown restent vérifiés.
        if '/archives/' in f:
            continue

        seen = set()
        for i, line in enumerate(text.split('\n'), 1):
            for m in PATH_TOKEN.finditer(re.sub(r'https?://\S+', ' ', line)):
                tok = m.group(1).rstrip('.,;:)').lstrip('./')
                if tok in seen or PLACEHOLDER.search(tok):
                    continue
                if not (tok.startswith(ROOTS) or tok in BARE):
                    continue
                seen.add(tok)
                n_paths += 1
                if not os.path.exists(tok) and tok not in INTENTIONNEL:
                    problems.append(f'{f}:{i}  chemin introuvable  → {tok}')

    for p in problems:
        print(p)
    print(f'\n{len(texts)} fichiers · {n_links} liens · {n_anchors} ancres · {n_paths} chemins cités')
    print('✔ aucun problème' if not problems else f'✖ {len(problems)} problème(s)')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
