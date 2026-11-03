using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace SAloha.Api.Tests;

/// <summary>
/// S3 (#122) : CORS limité aux origines de `Cors:AllowedOrigins`. L'API de la collection
/// n'en admet aucune ; celle-ci, sur la même base, en admet une.
/// </summary>
[Collection("api")]
public sealed class CorsTests(ApiFixture api) : IDisposable
{
    private const string Allowed = "https://frontend.example";

    private readonly WebApplicationFactory<Program> _factory = Factory(api, Allowed);

    private static WebApplicationFactory<Program> Factory(ApiFixture api, string origin) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", api.Database().Database.GetConnectionString())
            .UseSetting("Jwt:Key", ApiFixture.JwtKey)
            .UseSetting("Jwt:Issuer", "S-Aloha")
            .UseSetting("Discovery:IntervalMinutes", "0")
            .UseSetting("Discovery:VaultKey", "cle-du-coffre-de-test-uniquement-0123456789")
            .UseSetting("Cors:AllowedOrigins:0", origin));

    public void Dispose() => _factory.Dispose();

    private Task<HttpResponseMessage> Preflight(HttpClient client, string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/api/incidents");
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", "POST");
        return client.SendAsync(req);
    }

    [Fact]
    public async Task S3_une_origine_listee_recoit_l_autorisation()
    {
        var res = await Preflight(_factory.CreateClient(), Allowed);
        Assert.Equal(Allowed, Assert.Single(res.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task S3_une_origine_configuree_avec_une_barre_finale_est_admise()
    {
        // #239 : l'en-tête Origin n'a jamais de barre finale ; la valeur configurée la perd.
        using var factory = Factory(api, Allowed + "/");
        var res = await Preflight(factory.CreateClient(), Allowed);
        Assert.Equal(Allowed, Assert.Single(res.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Theory]
    [InlineData("https://ailleurs.example")]
    [InlineData("https://frontend.example.evil")]
    public async Task S3_une_origine_non_listee_ne_la_recoit_pas(string origin)
    {
        Assert.False((await Preflight(_factory.CreateClient(), origin)).Headers.Contains("Access-Control-Allow-Origin"));
        // Requête simple aussi : pas d'en-tête, le navigateur bloque la lecture de la réponse.
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/incidents");
        get.Headers.Add("Origin", origin);
        Assert.False((await api.Client("User", "s3.user").SendAsync(get)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task S3_sans_reglage_aucune_origine_externe()
    {
        // L'API de la collection garde le défaut : liste vide.
        var res = await Preflight(api.AnonymousClient(), Allowed);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
