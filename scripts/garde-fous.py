#!/usr/bin/env python3
"""Garde-fous d'une pull request : ce qu'elle ne doit pas affaiblir.

Trois contrôles sur le diff BASE...HEAD :

  1. tests      un fichier de test supprimé, moins de tests ou d'assertions dans un
                fichier modifié, un test désactivé (Skip, .skip, .only, xit) ;
  2. outillage  un fichier qui vérifie les autres (workflows, scripts de contrôle,
                configuration des tests) modifié sans l'étiquette `outillage` ;
  3. risque     la classe de la PR d'après ses chemins : `risque-doc`, `risque-code`
                ou `risque-sensible` (affichée, et posée en étiquette par le workflow).

    python3 scripts/garde-fous.py BASE HEAD [--etiquettes a,b] [--auteur LOGIN]
    python3 scripts/garde-fous.py BASE HEAD --risque     # n'affiche que la classe

Sortie non nulle si un garde-fou est franchi.
"""
import argparse
import fnmatch
import re
import subprocess
import sys

TESTS = ('tests/*.cs', 'tests/**/*.cs', 'frontend/src/**/*.test.js', 'frontend/src/**/*.test.jsx',
         'frontend/src/*.test.js', 'frontend/src/*.test.jsx')

# Ce qui vérifie le reste : le modifier demande l'accord du mainteneur (`outillage`).
OUTILLAGE = ('.github/*', '.github/**', 'scripts/check-*.py', 'scripts/recette.py',
             'scripts/garde-fous.py', 'scripts/preuve.py', 'scripts/changelog.py',
             'tests/SAloha.Api.Tests/ApiFixture.cs', 'tests/SAloha.Api.Tests/*.csproj',
             'frontend/vite.config.js', 'frontend/package.json')

# Chemins dont une erreur coûte cher : authentification, données, coffre, exposition,
# dépendances, chaîne de construction. Une PR qui en touche un reste au mainteneur.
SENSIBLE = ('backend/Core/Auth/**', 'backend/Program.cs', 'backend/Core/Data/Migrations/**',
            'backend/Core/Data/AppDbContext.cs', 'backend/Core/Perimeters/**',
            'backend/Modules/ServiceConfigurationManagement/Discovery/CredentialVault.cs',
            'backend/Core/Attachments/**', 'backend/appsettings.json', 'backend/*.csproj',
            'backend/Dockerfile', 'frontend/Dockerfile', 'frontend/nginx.conf',
            'frontend/package.json', 'frontend/package-lock.json', 'docker-compose*.yml',
            '.github/**', 'scripts/publier-images.sh', 'garage/**', 'demo/annuaire/**')

DOC = ('docs/**', 'changelog.d/*', '*.md', 'docs/*')

DOTNET_TESTS = re.compile(r'\[(?:Fact|Theory)\b')
DOTNET_ASSERT = re.compile(r'\bAssert\.\w+\(')
DOTNET_SKIP = re.compile(r'\[(?:Fact|Theory)\s*\([^)]*\bSkip\s*=')
JS_TESTS = re.compile(r'(?<![\w.])(?:it|test)(?:\.each\([^)]*\))?\s*\(')
JS_ASSERT = re.compile(r'(?<![\w.])expect\s*\(')
JS_SKIP = re.compile(r'(?<![\w])(?:(?:it|test|describe)\.(?:skip|only|todo)|xit|xdescribe|fit|fdescribe)\s*\(')


def git(*args):
    return subprocess.run(['git', *args], capture_output=True, text=True, check=True).stdout


def match(path, motifs):
    return any(fnmatch.fnmatchcase(path, m) for m in motifs)


def contenu(rev, path):
    r = subprocess.run(['git', 'show', f'{rev}:{path}'], capture_output=True, text=True)
    return r.stdout if r.returncode == 0 else ''


def compter(texte, cs):
    if cs:
        return len(DOTNET_TESTS.findall(texte)), len(DOTNET_ASSERT.findall(texte)), len(DOTNET_SKIP.findall(texte))
    return len(JS_TESTS.findall(texte)), len(JS_ASSERT.findall(texte)), len(JS_SKIP.findall(texte))


def changements(base, head):
    out = []
    for ligne in git('diff', '--name-status', '-M', f'{base}...{head}').splitlines():
        parts = ligne.split('\t')
        statut = parts[0][0]
        if statut == 'R':
            out.append(('D', parts[1]))
            out.append(('A', parts[2]))
        else:
            out.append((statut, parts[1]))
    return out


def risque(chemins):
    if any(match(p, SENSIBLE) for p in chemins):
        return 'risque-sensible'
    if chemins and all(match(p, DOC) for p in chemins):
        return 'risque-doc'
    return 'risque-code'


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('base')
    ap.add_argument('head')
    ap.add_argument('--etiquettes', default='')
    ap.add_argument('--auteur', default='')
    ap.add_argument('--risque', action='store_true')
    a = ap.parse_args(argv)

    base = git('merge-base', a.base, a.head).strip()
    ch = changements(base, a.head)
    chemins = sorted({p for _, p in ch})
    classe = risque(chemins)
    if a.risque:
        print(classe)
        return 0

    etiquettes = {e.strip() for e in a.etiquettes.split(',') if e.strip()}
    problemes = []

    # 1. Tests
    for statut, p in ch:
        if not match(p, TESTS):
            continue
        cs = p.endswith('.cs')
        if statut == 'D':
            avant = compter(contenu(base, p), cs)
            if avant[0]:
                problemes.append(f'{p}  fichier de test supprimé ({avant[0]} test(s))')
            continue
        avant = compter(contenu(base, p), cs) if statut == 'M' else (0, 0, 0)
        apres = compter(contenu(a.head, p), cs)
        if apres[0] < avant[0]:
            problemes.append(f'{p}  tests : {avant[0]} → {apres[0]}')
        if apres[1] < avant[1]:
            problemes.append(f'{p}  assertions : {avant[1]} → {apres[1]}')
        if apres[2] > avant[2]:
            problemes.append(f'{p}  test désactivé ou isolé (Skip, .skip, .only, xit…)')

    # 2. Outillage (les mises à jour Dependabot ne changent que des versions épinglées)
    if 'outillage' not in etiquettes and a.auteur != 'dependabot[bot]':
        for p in chemins:
            if match(p, OUTILLAGE):
                problemes.append(f'{p}  outillage de vérification modifié sans l\'étiquette `outillage`')

    for pb in problemes:
        print(f'::error::{pb}')
    print(f'{len(chemins)} fichier(s) · classe : {classe}')
    print('✔ aucun garde-fou franchi' if not problemes else f'✖ {len(problemes)} garde-fou(s) franchi(s)')
    return 1 if problemes else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
