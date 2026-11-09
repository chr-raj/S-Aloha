using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;

namespace SAloha.Api.Core.Search;

/// <summary>Met à jour les passages d'un enregistrement (ou de tous) et leurs vecteurs.</summary>
public class SearchIndexer(AppDbContext db, IEmbeddingClient embeddings, ILogger<SearchIndexer> logger)
{
    /// <summary>
    /// Réindexe un enregistrement : ses anciens passages sont retirés, puis
    /// recréés s'il est (encore) éligible. Sans service d'embeddings, les
    /// passages sont créés sans vecteur : ils restent trouvables par la
    /// recherche lexicale (RAG-02).
    /// </summary>
    public async Task ReindexAsync(string type, int id, CancellationToken ct = default)
    {
        var old = await db.SearchPassages.Where(p => p.SourceType == type && p.SourceId == id).ToListAsync(ct);
        db.SearchPassages.RemoveRange(old);

        var doc = await SearchSources.IndexableAsync(db, type, id);
        var passages = new List<SearchPassage>();
        if (doc is not null)
        {
            var chunks = Chunker.Split(doc.Text);
            if (chunks.Count == 0) chunks.Add(doc.Title);
            // Le titre accompagne chaque passage dans le vecteur : un passage
            // isolé (« Relancer le service ») n'a de sens qu'avec son sujet.
            var vectors = await embeddings.EmbedAsync(chunks.Select(c => doc.Title + "\n" + c).ToList(), TimeSpan.FromSeconds(120), ct);
            passages = chunks.Select((c, i) => new SearchPassage
            {
                SourceType = type, SourceId = id, Reference = doc.Reference, Title = Truncate(doc.Title, 200),
                Status = doc.Status, Ordinal = i, Text = c,
                Embedding = vectors?[i], EmbeddingModel = vectors is null ? null : embeddings.Model
            }).ToList();
            db.SearchPassages.AddRange(passages);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reconstruit tout l'index (démarrage sur un index vide, demande d'un administrateur).</summary>
    public async Task ReindexAllAsync(CancellationToken ct = default)
    {
        await db.SearchPassages.ExecuteDeleteAsync(ct);
        var count = 0;
        foreach (var type in SearchSources.Types)
            foreach (var id in await SearchSources.IdsAsync(db, type))
            {
                await ReindexAsync(type, id, ct);
                count++;
            }
        logger.LogInformation("Index de recherche reconstruit : {Count} enregistrement(s) examiné(s).", count);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
