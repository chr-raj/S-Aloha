using System.Text.RegularExpressions;
using SAloha.Api.Core.Search;

namespace SAloha.Api.Tests;

/// <summary>
/// Embeddings déterministes pour les tests : chaque groupe de synonymes occupe
/// une dimension, les autres mots sont hachés. « courriel » et « messagerie »
/// sont donc proches, sans partager de racine — ce que seule la recherche
/// vectorielle peut rapprocher. <see cref="Available"/> simule une panne.
/// </summary>
public partial class FakeEmbeddingClient : IEmbeddingClient
{
    public static volatile bool Available = true;
    public bool Enabled => true;
    public string Model => "fake-test";

    private static readonly string[][] Synonyms =
    [
        ["messagerie", "courriel", "courriels", "mail", "mails", "exchange"],
        ["lent", "lente", "lenteur", "lenteurs", "ralentissement", "latence"],
        ["vpn", "télétravail", "distant", "nomade"],
        ["imprimante", "impression", "imprimer"],
    ];
    // Même dimension que bge-m3 : les tests passent par la colonne vector(1024) et son index HNSW (ADR-0022).
    private const int Dims = SearchPassage.Dimensions;

    public Task<float[][]?> EmbedAsync(IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult(Available ? texts.Select(Embed).ToArray() : null);

    private static float[] Embed(string text)
    {
        var v = new float[Dims];
        foreach (Match m in Words().Matches(text.ToLowerInvariant()))
        {
            var group = Array.FindIndex(Synonyms, g => g.Contains(m.Value));
            if (group >= 0) v[group] += 3;
            else if (m.Value.Length > 3) v[Synonyms.Length + (int)((uint)m.Value.GetHashCode() % (Dims - Synonyms.Length))] += 1;
        }
        return Vectors.Normalize(v);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
