using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SAloha.Api.Tests;

/// <summary>Demandes, lot 2 : bénéficiaire (REQ-07), contestation (REQ-10), requalification (REQ-12).</summary>
[Collection("api")]
public class RequestContestTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();
    private static string? Str(JsonElement e, string p) => e.GetProperty(p).GetString();

    private static object Request(string title, string? status = null, string? requestedFor = null, string? contestReason = null) => new
    {
        title, requestedItem = "Poste de travail", status, requestedFor, requestedForDisplayName = requestedFor,
        ownerType = "User", ownerId = "tech.req", ownerDisplayName = "Tech", contestReason
    };

    [Fact]
    public async Task REQ_07_le_beneficiaire_vaut_le_demandeur_par_defaut()
    {
        var user = api.Client("User", "req07.demandeur");
        var mine = await Json(await user.PostAsJsonAsync("/api/requests", Request("Pour moi")));
        Assert.Equal("req07.demandeur", Str(mine, "requestedFor"));
        var other = await Json(await user.PostAsJsonAsync("/api/requests", Request("Pour un collègue", requestedFor: "collegue.req07")));
        Assert.Equal(("collegue.req07", "req07.demandeur"), (Str(other, "requestedFor"), Str(other, "createdBy")));
        // Modifiée sans bénéficiaire : celui-ci reste.
        var kept = await Json(await user.PutAsJsonAsync($"/api/requests/{Id(other)}", Request("Pour un collègue (précisé)")));
        Assert.Equal("collegue.req07", Str(kept, "requestedFor"));
    }

    private async Task<JsonElement> Satisfied(HttpClient requester, string title, string? requestedFor = null)
    {
        var manager = api.Client("Manager", "req10.chef");
        var r = await Json(await requester.PostAsJsonAsync("/api/requests", Request(title, requestedFor: requestedFor)));
        var url = $"/api/requests/{Id(r)}";
        await Json(await manager.PutAsJsonAsync(url, Request(title, "Approuvée", requestedFor)));
        await Json(await manager.PutAsJsonAsync(url, Request(title, "En cours", requestedFor)));
        return await Json(await manager.PutAsJsonAsync(url, Request(title, "Satisfaite", requestedFor)));
    }

    [Fact]
    public async Task REQ_25_seuls_le_responsable_et_un_gestionnaire_satisfont_la_demande()
    {
        var manager = api.Client("Manager", "req25s.manager");
        var requester = api.Client("User", "req25s.demandeur");
        object Body(string status, string ownerType = "User", string ownerId = "tech.req25s") => new
        {
            title = "Accès à l'outil de paie", requestedItem = "Accès applicatif", status, ownerType, ownerId, ownerDisplayName = ownerId
        };
        async Task<string> InProgress(string ownerType = "User", string ownerId = "tech.req25s")
        {
            var r = await Json(await requester.PostAsJsonAsync("/api/requests", Body("Soumise", ownerType, ownerId)));
            var url = $"/api/requests/{Id(r)}";
            await Json(await manager.PutAsJsonAsync(url, Body("Approuvée", ownerType, ownerId)));
            await Json(await manager.PutAsJsonAsync(url, Body("En cours", ownerType, ownerId)));
            return url;
        }

        // Ni le tiers, ni le demandeur : la satisfaction est déclarée par qui exécute (§ 4).
        var url = await InProgress();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client("User", "req25s.tiers").PutAsJsonAsync(url, Body("Satisfaite"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.PutAsJsonAsync(url, Body("Satisfaite"))).StatusCode);
        // Se désigner responsable dans la même modification ne suffit pas : le responsable d'avant compte.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await requester.PutAsJsonAsync(url, Body("Satisfaite", ownerId: "req25s.demandeur"))).StatusCode);
        Assert.Equal("En cours", Str(await Json(await requester.GetAsync(url)), "status"));
        // Le responsable, oui.
        Assert.Equal("Satisfaite", Str(await Json(await api.Client("User", "tech.req25s").PutAsJsonAsync(url, Body("Satisfaite"))), "status"));

        // Responsable de type groupe : un membre du groupe satisfait la demande.
        var grp = await InProgress("Group", "GRP-REQ25S");
        Assert.Equal("Satisfaite", Str(await Json(await api.Client("User", "membre.req25s", "GRP-REQ25S").PutAsJsonAsync(grp, Body("Satisfaite", "Group", "GRP-REQ25S"))), "status"));

        // Un gestionnaire, oui.
        var m = await InProgress();
        Assert.Equal("Satisfaite", Str(await Json(await manager.PutAsJsonAsync(m, Body("Satisfaite"))), "status"));
    }

    [Fact]
    public async Task REQ_10_contestation_motivee_par_le_demandeur_ou_le_beneficiaire_dans_les_5_jours_ouvres()
    {
        var requester = api.Client("User", "req10.demandeur");
        var r = await Satisfied(requester, "Contestée", requestedFor: "req10.beneficiaire");
        var url = $"/api/requests/{Id(r)}";
        // Un tiers ne conteste pas ; une contestation se motive.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client("User", "req10.tiers").PutAsJsonAsync(url,
            Request("Contestée", "En cours", "req10.beneficiaire", "Pas conforme"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.PutAsJsonAsync(url, Request("Contestée", "En cours", "req10.beneficiaire"))).StatusCode);
        var contested = await Json(await api.Client("User", "req10.beneficiaire").PutAsJsonAsync(url,
            Request("Contestée", "En cours", "req10.beneficiaire", "Écran livré sans station d'accueil")));
        Assert.Equal(("En cours", "Écran livré sans station d'accueil"), (Str(contested, "status"), Str(contested, "contestReason")));
        Assert.Equal(JsonValueKind.Null, contested.GetProperty("fulfilledAt").ValueKind);
        await using (var db = api.Database())
            Assert.True(await db.Notifications.AnyAsync(n => n.TargetType == "request" && n.TargetId == Id(r) && n.Event == "Demande contestée"));

        // Au-delà de 5 jours ouvrés, la contestation est refusée.
        var old = await Satisfied(requester, "Trop tard");
        await using (var db = api.Database())
        {
            var e = await db.ServiceRequests.SingleAsync(x => x.Id == Id(old));
            e.FulfilledAt = DateTime.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        }
        var late = await requester.PutAsJsonAsync($"/api/requests/{Id(old)}", Request("Trop tard", "En cours", null, "Toujours pas"));
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Contains("Délai de contestation", await late.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task REQ_12_requalification_en_changement_ou_en_incident()
    {
        var requester = api.Client("User", "req12.demandeur");
        var r = await Json(await requester.PostAsJsonAsync("/api/requests", Request("Nouveau lien réseau vers l'annexe")));
        var url = $"/api/requests/{Id(r)}/requalify";
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client("User", "req12.tiers").PostAsJsonAsync(url, new { target = "change", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.PostAsJsonAsync(url, new { target = "change", reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.PostAsJsonAsync(url, new { target = "problème", reason = "x" })).StatusCode);

        var result = await Json(await requester.PostAsJsonAsync(url, new { target = "change", reason = "Travaux d'infrastructure hors catalogue" }));
        var change = result.GetProperty("target");
        Assert.StartsWith("CHG-", Str(change, "reference"));
        var request = result.GetProperty("request");
        Assert.Equal("Annulée", Str(request, "status"));
        Assert.Contains(Str(change, "reference")!, Str(request, "cancelReason"));
        var created = await Json(await requester.GetAsync($"/api/changes/{Id(change)}"));
        Assert.Equal(("Demandé", "Normal"), (Str(created, "status"), Str(created, "changeType")));
        var links = (await Json(await requester.GetAsync($"/api/links?type=request&id={Id(r)}"))).EnumerateArray().Select(l => Str(l, "reference"));
        Assert.Contains(Str(change, "reference"), links);
        // Annulée : elle ne se requalifie plus.
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.PostAsJsonAsync(url, new { target = "incident", reason = "x" })).StatusCode);

        var panne = await Json(await requester.PostAsJsonAsync("/api/requests", Request("Imprimante qui ne répond plus")));
        var incident = (await Json(await requester.PostAsJsonAsync($"/api/requests/{Id(panne)}/requalify",
            new { target = "incident", reason = "C'est une panne, pas une demande" }))).GetProperty("target");
        Assert.StartsWith("INC-", Str(incident, "reference"));
    }
}
