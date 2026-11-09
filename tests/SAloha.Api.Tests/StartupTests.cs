using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SAloha.Api.Core.Data;
using Testcontainers.PostgreSql;

namespace SAloha.Api.Tests;

/// <summary>
/// O1 : préparation de la base au démarrage ou par une étape de déploiement (« --migrate »),
/// <c>Database:MigrateOnStartup</c>, rattrapage des échéances SLA en tâche de fond. Base
/// propre à ces tests : ils partent d'une base vide.
/// </summary>
public class StartupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _server = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg18").Build();

    public Task InitializeAsync() => _server.StartAsync();
    public Task DisposeAsync() => _server.DisposeAsync().AsTask();

    /// <summary>Base vide, propre au test, sur le serveur partagé de la classe.</summary>
    private async Task<string> EmptyDatabase(string name)
    {
        await using (var conn = new NpgsqlConnection(_server.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_server.GetConnectionString()) { Database = name }.ConnectionString;
    }

    private static AppDbContext Db(string cs) => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    private static IConfiguration Config(bool migrateOnStartup) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:MigrateOnStartup"] = migrateOnStartup.ToString() }).Build();

    [Fact]
    public async Task O1_sans_migration_au_demarrage_une_base_en_retard_est_refusee()
    {
        await using var db = Db(await EmptyDatabase("retard"));
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseSchema.Prepare(db, Config(false), NullLogger.Instance, migrateOnly: false));
        Assert.Contains("--migrate", error.Message);
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());   // rien n'a été appliqué
    }

    [Fact]
    public async Task O1_l_etape_migrate_prepare_la_base_meme_sans_migration_au_demarrage()
    {
        var cs = await EmptyDatabase("etape");
        await using (var db = Db(cs))
            DatabaseSchema.Prepare(db, Config(false), NullLogger.Instance, migrateOnly: true);
        await using var check = Db(cs);
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        Assert.True(await check.SecurityObjectives.AnyAsync());   // référentiel NIS 2 aligné (ADR-0012)
        // Base à jour : l'API démarre sans migrer elle-même.
        DatabaseSchema.Prepare(check, Config(false), NullLogger.Instance, migrateOnly: false);
    }

    [Fact]
    public async Task O1_echeances_SLA_rattrapees_en_tache_de_fond_et_visibles_dans_la_sante()
    {
        var cs = await EmptyDatabase("rattrapage");
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", cs)
            .UseSetting("Jwt:Key", ApiFixture.JwtKey)
            .UseSetting("Jwt:Issuer", "S-Aloha")
            .UseSetting("Discovery:IntervalMinutes", "0"));
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFixture.Token("admin.o1", "Admin"));
        JsonElement component = default;
        for (var i = 0; i < 50; i++)
        {
            var health = JsonDocument.Parse(await (await client.GetAsync("/api/admin/health")).Content.ReadAsStringAsync()).RootElement;
            component = health.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Rattrapage des échéances SLA");
            if (component.GetProperty("message").GetString()!.StartsWith("Fait le")) break;
            await Task.Delay(100);
        }
        Assert.Equal("OK", component.GetProperty("state").GetString());
        Assert.StartsWith("Fait le", component.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/incidents")).StatusCode);
    }
}
