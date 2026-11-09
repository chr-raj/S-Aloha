using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SAloha.Api.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Pgvector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            // ADR-0022 : colonne générée par PostgreSQL depuis "Embedding" (real[], seule
            // connue du modèle EF), de dimension fixe pour l'index HNSW. Un vecteur d'une
            // autre dimension (autre modèle) y vaut NULL : ignoré jusqu'à la reconstruction.
            migrationBuilder.Sql("""
                ALTER TABLE "SearchPassages" ADD COLUMN "EmbeddingVector" vector(1024)
                    GENERATED ALWAYS AS (
                        CASE WHEN cardinality("Embedding") = 1024 THEN "Embedding"::vector(1024) END
                    ) STORED;
                CREATE INDEX "IX_SearchPassages_EmbeddingVector" ON "SearchPassages"
                    USING hnsw ("EmbeddingVector" vector_cosine_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_SearchPassages_EmbeddingVector";
                ALTER TABLE "SearchPassages" DROP COLUMN IF EXISTS "EmbeddingVector";
                """);
            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
