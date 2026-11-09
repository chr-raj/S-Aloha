using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;
using SAloha.Api.Core.Links;
using SAloha.Api.Core.Perimeters;

namespace SAloha.Api.Core.Search;

/// <summary>
/// Recherche hybride dans les contenus validés (RAG-xx,
/// docs/reference/processus/recherche.md ; ADR-0013).
/// </summary>
[ApiController]
[Route("api/search")]
[Authorize(Policy = "User")]
public class SearchController(AppDbContext db, HybridSearch search, IEmbeddingClient embeddings, IndexQueue queue,
    PerimeterAccess access) : ControllerBase
{
    /// <summary>
    /// PER-09 : seuls les résultats visibles de l'utilisateur (ses périmètres, articles publiés).
    /// Le filtre vient après le classement : on demande trois fois plus de candidats.
    /// </summary>
    private async Task<List<HybridSearch.Hit>> VisibleAsync(List<HybridSearch.Hit> hits, int limit)
    {
        var scope = await access.GetAsync();
        if (scope.Global) return hits.Take(limit).ToList();
        var visible = new HashSet<(string, int)>();
        foreach (var group in hits.GroupBy(h => h.Type))
        {
            var kind = ItemLinks.ByType(group.Key);
            if (kind is null) continue;
            var ids = group.Select(h => h.Id).ToList();
            foreach (var t in await kind.Query(db).Where(t => ids.Contains(t.Id)).ToListAsync())
                if (scope.Sees(t)) visible.Add((group.Key, t.Id));
        }
        return hits.Where(h => visible.Contains((h.Type, h.Id))).Take(limit).ToList();
    }

    private static IReadOnlyCollection<string> TypesOf(string? types) =>
        string.IsNullOrWhiteSpace(types)
            ? SearchSources.Types
            : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(SearchSources.Types.Contains).ToList();

    /// <summary>Recherche libre (RAG-05) : articles publiés, erreurs connues et problèmes résolus, incidents résolus.</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] string? types, [FromQuery] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<object>());
        var max = Math.Clamp(limit, 1, 50);
        return Ok(await VisibleAsync(await search.SearchAsync(q.Trim(), TypesOf(types), null, max * 3), max));
    }

    /// <summary>
    /// Cas similaires à un enregistrement (RAG-06) : son titre et sa
    /// description servent de requête ; il est exclu des résultats.
    /// </summary>
    [HttpGet("similar")]
    public async Task<IActionResult> Similar([FromQuery] string type, [FromQuery] int id, [FromQuery] string? types, [FromQuery] int limit = 5)
    {
        var doc = await SearchSources.LoadAsync(db, type, id);
        var kind = ItemLinks.ByType(type);
        var source = kind is null ? null : await kind.Query(db).FirstOrDefaultAsync(t => t.Id == id);
        if (doc is null || source is null || !(await access.GetAsync()).Sees(source)) return NotFound();
        var query = doc.Title + "\n" + (doc.Text.Length > 2000 ? doc.Text[..2000] : doc.Text);
        var max = Math.Clamp(limit, 1, 20);
        return Ok(await VisibleAsync(await search.SearchAsync(query, TypesOf(types), (type, id), max * 3, lexicalQuery: doc.Title), max));
    }

    /// <summary>État de l'index et du service d'embeddings (RAG-08, Admin).</summary>
    [HttpGet("status")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Status()
    {
        var byType = await db.SearchPassages.AsNoTracking().GroupBy(p => p.SourceType)
            .Select(g => new { Type = g.Key, Records = g.Select(p => p.SourceId).Distinct().Count(), Passages = g.Count(),
                WithVector = g.Count(p => p.Embedding != null) }).ToListAsync();
        var reachable = embeddings.Enabled && await embeddings.EmbedAsync(["test"], TimeSpan.FromSeconds(5)) is not null;
        return Ok(new
        {
            Embeddings = new { embeddings.Enabled, embeddings.Model, Reachable = reachable },
            Vectors = await db.SearchPassages.CountAsync(p => p.Embedding != null),
            Sources = byType,
            LastIndexedAt = await db.SearchPassages.MaxAsync(p => (DateTime?)p.IndexedAt)
        });
    }

    /// <summary>Reconstruction complète, en tâche de fond (RAG-08, Admin).</summary>
    [HttpPost("reindex")]
    [Authorize(Policy = "Admin")]
    public IActionResult Reindex()
    {
        queue.EnqueueAll();
        return Accepted(new { message = "Reconstruction de l'index lancée en tâche de fond." });
    }
}
