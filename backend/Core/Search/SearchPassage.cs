using System.ComponentModel.DataAnnotations;
using NpgsqlTypes;

namespace SAloha.Api.Core.Search;

/// <summary>
/// Passage indexé d'un enregistrement (article publié, problème, incident
/// résolu) pour la recherche hybride (ADR-0013). Index dérivé : il se
/// reconstruit entièrement depuis les enregistrements, rien n'y est saisi.
/// </summary>
public class SearchPassage
{
    public int Id { get; set; }
    [MaxLength(20)] public string SourceType { get; set; } = "";
    public int SourceId { get; set; }
    [MaxLength(20)] public string Reference { get; set; } = "";
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(30)] public string Status { get; set; } = "";
    public int Ordinal { get; set; }
    public string Text { get; set; } = "";
    /// <summary>
    /// Dimension des vecteurs indexés par pgvector (bge-m3, ADR-0022). La colonne générée
    /// <c>EmbeddingVector vector(1024)</c> et son index HNSW, créés par la migration Pgvector
    /// et inconnus du modèle EF, ne retiennent que les vecteurs de cette dimension.
    /// Changer de modèle pour une autre dimension demande une migration.
    /// </summary>
    public const int Dimensions = 1024;
    /// <summary>Vecteur normalisé (bge-m3 : 1024 dimensions) ; null si le service d'embeddings était absent.</summary>
    public float[]? Embedding { get; set; }
    [MaxLength(50)] public string? EmbeddingModel { get; set; }
    /// <summary>Colonne générée par PostgreSQL : to_tsvector('french', titre + texte), index GIN.</summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
}
