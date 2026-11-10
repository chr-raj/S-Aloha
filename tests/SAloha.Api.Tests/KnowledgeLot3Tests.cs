using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SAloha.Api.Tests;

/// <summary>Connaissances, lot 3 : retour des lecteurs (KB-13), versions (KB-15), quatre yeux (KB-16).</summary>
[Collection("api")]
public class KnowledgeLot3Tests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();
    private static object Article(string content, string? status = null, string owner = "responsable.kb13") => new
    {
        title = "Réinitialiser un mot de passe", description = "", articleType = "Procédure", content, keywords = "", status,
        ownerType = "User", ownerId = owner, ownerDisplayName = owner
    };

    [Fact]
    public async Task KB_16_KB_15_quatre_yeux_puis_versions_conservees_a_chaque_publication()
    {
        var chef = api.Client("Manager", "chef.kb16");
        var writer = api.Client("User", "redacteur.kb16");
        // Seul rédacteur : le gestionnaire ne publie pas son propre article ; l'Admin le peut.
        var own = await Json(await chef.PostAsJsonAsync("/api/knowledge", Article("Procédure écrite par le chef")));
        var refused = await chef.PutAsJsonAsync($"/api/knowledge/{Id(own)}", Article("Procédure écrite par le chef", "Publié"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Quatre yeux", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Brouillon", (await Json(await chef.PostAsJsonAsync("/api/knowledge", Article("Direct", "Publié")))).GetProperty("status").GetString());   // un article naît brouillon
        Assert.Equal("Publié", (await Json(await api.Client("Admin", "admin.kb16").PutAsJsonAsync($"/api/knowledge/{Id(own)}",
            Article("Procédure écrite par le chef", "Publié")))).GetProperty("status").GetString());

        // Rédigé par un autre : publié (version 1) ; retouché puis republié (version 2) ; les deux versions restent.
        var a = await Json(await writer.PostAsJsonAsync("/api/knowledge", Article("Version initiale")));
        var url = $"/api/knowledge/{Id(a)}";
        Assert.Equal(1, (await Json(await chef.PutAsJsonAsync(url, Article("Version initiale", "Publié")))).GetProperty("version").GetInt32());
        Assert.Equal("Brouillon", (await Json(await writer.PutAsJsonAsync(url, Article("Version corrigée", "Publié")))).GetProperty("status").GetString());
        Assert.Equal(2, (await Json(await chef.PutAsJsonAsync(url, Article("Version corrigée", "Publié")))).GetProperty("version").GetInt32());
        var versions = (await Json(await writer.GetAsync($"{url}/versions"))).EnumerateArray().ToList();
        Assert.Equal(new[] { 2, 1 }, versions.Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal(("Version corrigée", "Version initiale"), (versions[0].GetProperty("content").GetString(), versions[1].GetProperty("content").GetString()));
        Assert.Contains("redacteur.kb16", versions[0].GetProperty("authors").GetString());
        // Retouche d'un article publié par un gestionnaire : republication, version 3 ; sans changement de contenu, pas de version.
        Assert.Equal(3, (await Json(await chef.PutAsJsonAsync(url, Article("Version complétée", "Publié")))).GetProperty("version").GetInt32());
        Assert.Equal(3, (await Json(await chef.PutAsJsonAsync(url, Article("Version complétée", "Publié")))).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task KB_03_KB_16_le_seul_redacteur_gestionnaire_qui_retouche_son_article_publie_le_renvoie_en_brouillon()
    {
        var auteur = api.Client("Manager", "auteur.kb03");
        var relecteur = api.Client("Manager", "relecteur.kb03");
        var a = await Json(await auteur.PostAsJsonAsync("/api/knowledge", Article("Brouillon court")));
        var url = $"/api/knowledge/{Id(a)}";
        Assert.Equal(1, (await Json(await relecteur.PutAsJsonAsync(url, Article("Brouillon court", "Publié")))).GetProperty("version").GetInt32());

        // Seul rédacteur : sa réécriture ne vaut pas publication, l'article repasse en brouillon.
        var rewritten = await Json(await auteur.PutAsJsonAsync(url, Article("Contenu entièrement réécrit", "Publié")));
        Assert.Equal(("Brouillon", 1), (rewritten.GetProperty("status").GetString(), rewritten.GetProperty("version").GetInt32()));
        Assert.Single((await Json(await auteur.GetAsync($"{url}/versions"))).EnumerateArray());
        // Mots-clés seuls : l'article publié le reste (KB-03), même pour le seul rédacteur.
        var other = await Json(await auteur.PostAsJsonAsync("/api/knowledge", Article("Autre procédure")));
        await Json(await relecteur.PutAsJsonAsync($"/api/knowledge/{Id(other)}", Article("Autre procédure", "Publié")));
        var tagged = new
        {
            title = "Réinitialiser un mot de passe", description = "", articleType = "Procédure", content = "Autre procédure",
            keywords = "mot de passe", status = "Publié", ownerType = "User", ownerId = "responsable.kb13", ownerDisplayName = "responsable.kb13"
        };
        Assert.Equal("Publié", (await Json(await auteur.PutAsJsonAsync($"/api/knowledge/{Id(other)}", tagged))).GetProperty("status").GetString());

        // Témoin : un autre gestionnaire republie (version 2) ; sa propre retouche vaut publication (version 3).
        Assert.Equal(2, (await Json(await relecteur.PutAsJsonAsync(url, Article("Contenu entièrement réécrit", "Publié")))).GetProperty("version").GetInt32());
        var retouched = await Json(await relecteur.PutAsJsonAsync(url, Article("Contenu réécrit et relu", "Publié")));
        Assert.Equal(("Publié", 3), (retouched.GetProperty("status").GetString(), retouched.GetProperty("version").GetInt32()));
    }

    [Fact]
    public async Task KB_13_trois_pas_utile_de_plus_que_d_utile_signalent_l_article_a_son_responsable()
    {
        var chef = api.Client("Manager", "chef.kb13");
        var a = await Json(await api.Client("User", "redacteur.kb13").PostAsJsonAsync("/api/knowledge", Article("Étapes à suivre")));
        var url = $"/api/knowledge/{Id(a)}";
        Assert.Equal(HttpStatusCode.BadRequest, (await chef.PostAsJsonAsync($"{url}/feedback", new { helpful = false })).StatusCode);   // brouillon
        await Json(await chef.PutAsJsonAsync(url, Article("Étapes à suivre", "Publié")));

        async Task<JsonElement> Vote(string who, bool helpful) =>
            (await Json(await api.Client("User", who).PostAsJsonAsync($"{url}/feedback", new { helpful }))).GetProperty("view");
        await Vote("lecteur1.kb13", true);
        var changed = await Vote("lecteur1.kb13", false);   // un vote par lecteur, modifiable
        Assert.Equal((0, 1), (changed.GetProperty("helpfulCount").GetInt32(), changed.GetProperty("notHelpfulCount").GetInt32()));
        await Vote("lecteur2.kb13", false);
        Assert.False((await Vote("lecteur3.kb13", true)).GetProperty("flaggedUnhelpful").GetBoolean());
        await Vote("lecteur4.kb13", false);
        Assert.True((await Vote("lecteur5.kb13", false)).GetProperty("flaggedUnhelpful").GetBoolean());   // 4 contre 1
        await Vote("lecteur6.kb13", false);
        Assert.False((await Json(await api.Client("User", "lecteur6.kb13").GetAsync($"{url}/feedback"))).GetProperty("myVote").GetBoolean());

        await using (var db = api.Database())
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.TargetType == "article" && n.TargetId == Id(a)
                                                                   && n.RecipientId == "responsable.kb13" && n.Event == "Article jugé peu utile par ses lecteurs"));
        var mine = await Json(await api.Client("User", "responsable.kb13").GetAsync("/api/console"));
        Assert.Contains(mine.GetProperty("myUnhelpfulArticles").EnumerateArray(), x => Id(x) == Id(a));
        var managers = await Json(await chef.GetAsync("/api/console"));
        Assert.Contains(managers.GetProperty("management").GetProperty("unhelpfulArticles").EnumerateArray(), x => Id(x) == Id(a));

        // Une nouvelle version repart sans votes.
        await Json(await api.Client("User", "redacteur.kb13").PutAsJsonAsync(url, Article("Étapes revues", "Publié")));
        var republished = await Json(await chef.PutAsJsonAsync(url, Article("Étapes revues", "Publié")));
        Assert.Equal((0, false), (republished.GetProperty("notHelpfulCount").GetInt32(), republished.GetProperty("flaggedUnhelpful").GetBoolean()));
    }
}
