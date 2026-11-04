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

    [Fact]
    public async Task REQ_12_INC_21_l_incident_requalifie_a_son_echeance_SLA()
    {
        var m = api.Client("Manager", "req12sla.manager");
        var svc = Id(await Json(await m.PostAsJsonAsync("/api/services", new
        {
            title = "Service REQ-12 SLA", status = "En service", criticality = "Moyenne", ownerType = "User", ownerId = "svc.owner", ownerDisplayName = "Owner"
        })));
        object Sla(string? status) => new
        {
            title = "SLA REQ-12", status, serviceId = svc, customer = "REQ-12", resolutionHoursP1 = 4, resolutionHoursP2 = 8,
            resolutionHoursP3 = 24, resolutionHoursP4 = 48, validFrom = DateTime.UtcNow.Date.AddMonths(-6),
            responseHoursP1 = 1, responseHoursP2 = 2, responseHoursP3 = 4, responseHoursP4 = 8
        };
        var sla = await Json(await m.PostAsJsonAsync("/api/agreements", Sla(null)));
        await Json(await m.PutAsJsonAsync($"/api/agreements/{Id(sla)}", Sla("En vigueur")));
        // Témoin : un incident déclaré directement sur le service a son échéance.
        var direct = await Json(await m.PostAsJsonAsync("/api/incidents", new { title = "Témoin REQ-12", impact = "Moyen", urgency = "Moyenne", serviceId = svc }));
        Assert.Equal(JsonValueKind.String, direct.GetProperty("resolutionDueAt").ValueKind);
        var requester = api.Client("User", "req12sla.demandeur");
        var r = await Json(await requester.PostAsJsonAsync("/api/requests", new
        {
            title = "Messagerie inaccessible", requestedItem = "Messagerie", serviceId = svc,
            ownerType = "User", ownerId = "tech.req", ownerDisplayName = "Tech"
        }));
        var target = (await Json(await requester.PostAsJsonAsync($"/api/requests/{Id(r)}/requalify",
            new { target = "incident", reason = "C'est une panne, pas une demande" }))).GetProperty("target");

        // Comme un incident déclaré directement sur le service : échéances posées dès la création.
        var incident = await Json(await requester.GetAsync($"/api/incidents/{Id(target)}"));
        Assert.Equal(svc, incident.GetProperty("serviceId").GetInt32());
        Assert.Equal(JsonValueKind.String, incident.GetProperty("resolutionDueAt").ValueKind);
        Assert.Equal(JsonValueKind.String, incident.GetProperty("responseDueAt").ValueKind);
        Assert.NotEqual("Sans SLA", Str(incident, "slaState"));
    }
}
