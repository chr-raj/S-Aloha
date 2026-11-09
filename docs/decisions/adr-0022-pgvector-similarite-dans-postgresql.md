# ADR-0022 — pgvector : la similarité des vecteurs est calculée dans PostgreSQL

<sub>[← Documentation](../readme.md) · [Toutes les décisions](readme.md)</sub>

- **Date** : 2026-11-09
- **Statut** : accepté — remplace le point 3 de l'[ADR-0013](adr-0013-recherche-hybride-service-embeddings-separe.md) (« vecteurs sans pgvector »)
- **Décideur** : Leipa

## Contexte

L'[ADR-0013](adr-0013-recherche-hybride-service-embeddings-separe.md) gardait les vecteurs
des passages en mémoire dans l'API (cosinus par force brute) et écartait pgvector jusqu'à
un seuil de volume. Deux besoins changent la donne avant la première version stable :

- un **second consommateur** de l'index est prévu (serveur MCP, après la v1.0.0) : des
  vecteurs qui n'existent que dans la mémoire d'un processus ne se partagent pas ;
- changer l'image PostgreSQL **avant** la v1.0.0 ne demande aucune migration aux
  installations ; après, ce serait un changement cassant pour chacune.

Décision du mainteneur du 2026-11-09 : adopter pgvector maintenant. Meilisearch reste écarté
(un index externe à synchroniser, y compris pour les périmètres, PER-07 et RAG-13).

## Décision

1. **Image** `pgvector/pgvector:0.8.7-pg16` (même PostgreSQL 16, extension `vector`) ;
   l'extension est déclarée dans le modèle (`HasPostgresExtension("vector")`) et créée par
   la migration `Pgvector`.
2. **Colonne générée** `EmbeddingVector vector(1024)`, calculée par PostgreSQL depuis
   `Embedding` (`real[]`, seule colonne connue du modèle EF) ; un vecteur d'une autre
   dimension y vaut `NULL`. La dimension est celle de `bge-m3` (`SearchPassage.Dimensions`) :
   un modèle d'une autre dimension demande une migration.
3. **Index HNSW** (`vector_cosine_ops`) sur cette colonne ; requête « les 20 plus proches
   d'abord, le plancher de similarité ensuite » (un filtre de distance dans la même clause
   `WHERE` écarte l'index), parcours itératif `hnsw.iterative_scan = relaxed_order` pour
   que le filtre par type ne vide pas la liste ; les candidats sont reclassés par leur
   distance exacte.
4. **Plus de copie en mémoire** : `VectorStore` est retiré ; tout processus qui lit la base
   voit le même index, dès l'écriture, sans rechargement.

Le reste de l'ADR-0013 tient : service d'embeddings séparé, plein texte PostgreSQL, fusion
RRF, index dérivé, recherche lexicale sans le service.

## Mesures (2026-11-09, vecteurs aléatoires de 1024 dimensions, poste du mainteneur)

| Méthode | 10 000 passages | 50 000 passages |
|---|---|---|
| Calcul exact dans PostgreSQL, conversion `real[]` à chaque ligne | ≈ 195 ms | ≈ 1,1 s |
| Calcul exact sur une colonne `vector` stockée | ≈ 100 ms | ≈ 800 ms |
| **Index HNSW** (filtre de type compris) | — | **≈ 4 ms** (≈ 87 ms à froid) |

Le calcul exact dans PostgreSQL est plus lent que la force brute en mémoire (les vecteurs
de 4 Ko sont stockés hors ligne, TOAST) : c'est l'index HNSW qui fait de pgvector un gain.
Construire l'index sur 50 000 passages existants prend environ deux minutes, une fois ;
ensuite il grandit à chaque insertion.

## Conséquences

- **Mémoire de l'API** : elle ne croît plus avec l'index.
- **Image** : Debian (glibc) au lieu d'Alpine (musl). Une base créée par l'ancienne image
  doit être **sauvegardée puis restaurée** (ou ses index reconstruits par `REINDEX
  DATABASE`) : le tri du texte diffère entre les deux bibliothèques C
  ([exploitation § Recherche](../reference/exploitation.md#recherche)).
- **PostgreSQL géré** : l'extension `vector` doit y être disponible (c'est le cas chez la
  plupart des hébergeurs) ; sinon, la migration échoue au démarrage.
- **Recherche approchée** : HNSW peut manquer un voisin ; sur 20 candidats reclassés
  exactement, l'effet est négligeable, à vérifier au calibrage (RAG-10).
- **Suite possible** : le filtre des périmètres (RAG-13, aujourd'hui après le classement)
  peut entrer dans la requête SQL ; ce sera le préalable d'un serveur MCP.

## Alternatives envisagées

- **Statu quo (ADR-0013)** — écarté : vecteurs prisonniers d'un processus, mémoire liée au
  volume, changement d'image repoussé après la v1.0.0.
- **Calcul exact dans PostgreSQL, sans index** — écarté après mesure (tableau ci-dessus).
- **Paquet `Pgvector.EntityFrameworkCore`** (type `vector` dans le modèle EF) — écarté : sa
  version 0.3.0 ne déclare que Npgsql EF 9, S-Aloha est en EF 10 ; la colonne générée évite
  toute nouvelle dépendance .NET.
- **Meilisearch** — écarté : un service de plus et un index séparé à synchroniser (les
  périmètres compris), alors que PostgreSQL porte déjà le plein texte et les vecteurs.
