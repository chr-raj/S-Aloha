using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;

namespace SAloha.Api.Core.Search;

/// <summary>
/// Recherche hybride (ADR-0013, ADR-0022) : plein texte PostgreSQL (configuration
/// « french ») et similarité des vecteurs bge-m3 calculée par pgvector dans
/// PostgreSQL, fusionnés par rang réciproque (RRF, k = 60). Le lexical trouve les termes exacts (codes
/// d'erreur, noms de serveurs) ; le vectoriel, les reformulations et les
/// autres langues.
/// </summary>
public partial class HybridSearch(AppDbContext db, IEmbeddingClient embeddings, IConfiguration config)
{
    public const int RrfK = 60;
    private const int Candidates = 20;

    public record Hit(string Type, int Id, string Reference, string Title, string Status, string Snippet,
        double Score, bool Lexical, bool Semantic);

    /// <summary>
    /// Plancher de similarité cosinus (RAG-07) : sans lui, une recherche sans
    /// rapport ramène quand même les passages « les moins éloignés », présentés
    /// comme des réponses. 0,47 : valeur calibrée le 2026-11-09 (RAG-10), avec une marge sur
    /// les requêtes sans réponse, qui sortent autour de 0,42 à 0,45 avec bge-m3.
    /// </summary>
    private float MinSimilarity => float.TryParse(config["Search:MinSimilarity"], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.47f;

    /// <param name="query">Requête du vectoriel, et du lexical si <paramref name="lexicalQuery"/> est absent.</param>
    /// <param name="lexicalQuery">
    /// Requête lexicale distincte : pour les cas similaires, le titre seul. Les
    /// termes étant reliés par OU, une description entière ramène des passages
    /// qui ne partagent que des mots secondaires (« mettre », « minutes »).
    /// </param>
    public async Task<List<Hit>> SearchAsync(string query, IReadOnlyCollection<string> types, (string Type, int Id)? exclude, int limit,
        string? lexicalQuery = null)
    {
        var lexical = await LexicalAsync(lexicalQuery ?? query, types);
        var semantic = await SemanticAsync(query, types);

        // Rang d'un enregistrement dans chaque liste = rang de son meilleur passage.
        var fused = new Dictionary<(string, int), (double Score, int Passage, bool Lex, bool Sem)>();
        void Add(IEnumerable<(string Type, int Id, int Passage)> ranked, bool isLexical)
        {
            var seen = new HashSet<(string, int)>();
            var rank = 0;
            foreach (var (type, id, passage) in ranked)
            {
                if (!seen.Add((type, id))) continue;
                var key = (type, id);
                fused.TryGetValue(key, out var f);
                fused[key] = (f.Score + 1.0 / (RrfK + rank + 1), f.Passage == 0 ? passage : f.Passage,
                    f.Lex || isLexical, f.Sem || !isLexical);
                rank++;
            }
        }
        Add(lexical, true);
        Add(semantic, false);

        var top = fused.Where(f => exclude is null || f.Key != exclude.Value)
            .OrderByDescending(f => f.Value.Score).Take(limit).ToList();
        var passageIds = top.Select(t => t.Value.Passage).ToList();
        var passages = await db.SearchPassages.AsNoTracking().Where(p => passageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return top.Where(t => passages.ContainsKey(t.Value.Passage)).Select(t =>
        {
            var p = passages[t.Value.Passage];
            return new Hit(p.SourceType, p.SourceId, p.Reference, p.Title, p.Status, Snippet(p.Text),
                Math.Round(t.Value.Score, 4), t.Value.Lex, t.Value.Sem);
        }).ToList();
    }

    /// <summary>
    /// Termes reliés par OU : une question longue (« cas similaires » à partir
    /// d'une description) ne doit pas exiger que tous ses mots soient présents ;
    /// le classement ts_rank_cd favorise les passages qui en contiennent le plus.
    /// </summary>
    private async Task<List<(string Type, int Id, int Passage)>> LexicalAsync(string query, IReadOnlyCollection<string> types)
    {
        var terms = Words().Matches(query.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 2).Distinct().Take(40).ToList();
        if (terms.Count == 0) return [];
        var tsquery = string.Join(" | ", terms);
        return (await db.SearchPassages.AsNoTracking()
            .Where(p => types.Contains(p.SourceType) && p.SearchVector.Matches(EF.Functions.ToTsQuery("french", tsquery)))
            .OrderByDescending(p => p.SearchVector.RankCoverDensity(EF.Functions.ToTsQuery("french", tsquery)))
            .Take(Candidates).Select(p => new { p.SourceType, p.SourceId, p.Id }).ToListAsync())
            .Select(p => (p.SourceType, p.SourceId, p.Id)).ToList();
    }

    /// <summary>
    /// Plus proches voisins par distance cosinus (opérateur <c>&lt;=&gt;</c> de pgvector),
    /// calculés dans PostgreSQL sur l'index HNSW de <c>EmbeddingVector</c> (ADR-0022) : les
    /// vecteurs ne vivent plus dans la mémoire de l'API, et tout processus qui lit la base
    /// voit le même index, sans rechargement. Le parcours itératif de l'index
    /// (<c>hnsw.iterative_scan</c>) évite qu'un filtre (type, plancher) ne vide la liste.
    /// Une requête d'une autre dimension que l'index (modèle changé) n'a pas de voisins.
    /// </summary>
    private async Task<List<(string Type, int Id, int Passage)>> SemanticAsync(string query, IReadOnlyCollection<string> types)
    {
        if (!embeddings.Enabled || !await db.SearchPassages.AnyAsync(p => p.Embedding != null)) return [];
        // Délai court : une recherche ne doit pas attendre un service lent ; le lexical répond seul.
        var vectors = await embeddings.EmbedAsync([query], TimeSpan.FromSeconds(5));
        if (vectors is null || vectors[0].Length != SearchPassage.Dimensions) return [];
        var q = vectors[0];
        var kinds = types.ToArray();
        var maxDistance = 1.0 - MinSimilarity;
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL hnsw.iterative_scan = relaxed_order");
        // Les voisins d'abord (ORDER BY … LIMIT, que sert l'index), le plancher ensuite :
        // un filtre de distance dans la même clause WHERE écarte l'index (pgvector).
        var rows = await db.Database.SqlQuery<Neighbour>($"""
            WITH proches AS MATERIALIZED (
                SELECT "Id" AS "PassageId", "SourceType", "SourceId",
                       "EmbeddingVector" <=> {q}::real[]::vector(1024) AS "Distance"
                FROM "SearchPassages"
                WHERE "SourceType" = ANY({kinds})
                ORDER BY "EmbeddingVector" <=> {q}::real[]::vector(1024)
                LIMIT {Candidates})
            SELECT "PassageId", "SourceType", "SourceId" FROM proches
            WHERE "Distance" <= {maxDistance}
            ORDER BY "Distance"
            """).ToListAsync();
        await tx.CommitAsync();
        // Les 20 candidats sont reclassés par leur distance exacte : relaxed_order ne change
        // que l'ordre dans lequel l'index les trouve.
        return rows.Select(r => (r.SourceType, r.SourceId, r.PassageId)).ToList();
    }

    private sealed class Neighbour
    {
        public int PassageId { get; set; }
        public string SourceType { get; set; } = "";
        public int SourceId { get; set; }
    }

    private static string Snippet(string text) => text.Length <= 280 ? text : text[..280].TrimEnd() + "…";

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
