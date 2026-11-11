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
    /// RAG-13, PER-09 : périmètres visibles de l'utilisateur (null : tous, Admin), filtrés dans les
    /// requêtes de la recherche, avant le classement.
    /// </summary>
    private async Task<IReadOnlyCollection<int>?> PerimetersAsync()
    {
        var scope = await access.GetAsync();
        return scope.Global ? null : scope.Ids;
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
        return Ok(await search.SearchAsync(q.Trim(), TypesOf(types), await PerimetersAsync(), null, max));
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
        return Ok(await search.SearchAsync(query, TypesOf(types), await PerimetersAsync(), (type, id), max, lexicalQuery: doc.Title));
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
