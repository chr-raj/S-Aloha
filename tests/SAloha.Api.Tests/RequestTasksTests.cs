using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SAloha.Api.Tests;

/// <summary>Demandes, lot 3 : tâches d'exécution (REQ-24, REQ-25), clôture (REQ-11), satisfaction (REQ-32).</summary>
[Collection("api")]
public class RequestTasksTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();
    private static string? Str(JsonElement e, string p) => e.GetProperty(p).GetString();

    private HttpClient Manager => api.Client("Manager", "chef.req24");

    private async Task<JsonElement> Item(string name, bool approval = true) => await Json(await Manager.PostAsJsonAsync("/api/request-catalog", new
    {
        name, description = "d", requiresApproval = approval, fulfillmentGroup = "GRP-POSTES",
        tasks = new object[]
        {
            new { title = "Préparer le poste", ownerType = "User", ownerId = "tech.req24", ownerDisplayName = "Tech" },
            new { title = "Livrer et former", ownerType = (string?)null, ownerId = (string?)null, ownerDisplayName = (string?)null },
        }
    }));

    // Le groupe d'exécution du modèle, responsable dès l'approbation (REQ-23) : renvoyé avec la fiche (REQ-13).
    private static object Request(string title, int item, string? status = null) => new { title, requestedItem = "", catalogItemId = item, status,
        ownerType = "Group", ownerId = "GRP-POSTES", ownerDisplayName = "GRP-POSTES" };

    private async Task<List<JsonElement>> Tasks(HttpClient c, int request) =>
        (await Json(await c.GetAsync($"/api/requests/{request}/tasks"))).EnumerateArray().ToList();

    [Fact]
    public async Task REQ_24_REQ_25_taches_du_modele_creees_a_l_approbation_et_exigees_pour_satisfaire()
    {
        var item = await Item($"Poste de travail {Guid.NewGuid():N}"[..28]);
        Assert.Equal(2, item.GetProperty("tasks").GetArrayLength());
        var requester = api.Client("User", "demandeur.req24");
        var r = await Json(await requester.PostAsJsonAsync("/api/requests", Request("Poste pour un nouvel arrivant", Id(item))));
        Assert.Empty(await Tasks(requester, Id(r)));   // pas avant l'approbation
        var url = $"/api/requests/{Id(r)}";
        await Json(await Manager.PutAsJsonAsync(url, Request("Poste pour un nouvel arrivant", Id(item), "Approuvée")));
        var tasks = await Tasks(requester, Id(r));
        Assert.Equal(new[] { "Préparer le poste", "Livrer et former" }, tasks.Select(t => Str(t, "title")));
        Assert.Equal(("tech.req24", "GRP-POSTES"), (Str(tasks[0], "ownerId"), Str(tasks[1], "ownerId")));   // sans responsable : celui de la demande
        Assert.All(tasks, t => Assert.Equal("À faire", Str(t, "status")));
        // Une tâche s'ajoute ; elle seule se supprime (gestionnaire).
        var extra = await Json(await api.Client("User", "tech.req24").PostAsJsonAsync($"{url}/tasks", new { title = "Commander un écran", ownerId = "tech.req24" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Manager.DeleteAsync($"{url}/tasks/{Id(tasks[0])}")).StatusCode);

        await Json(await Manager.PutAsJsonAsync(url, Request("Poste pour un nouvel arrivant", Id(item), "En cours")));
        var refused = await Manager.PutAsJsonAsync(url, Request("Poste pour un nouvel arrivant", Id(item), "Satisfaite"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Préparer le poste", await refused.Content.ReadAsStringAsync());
        foreach (var t in tasks) await Json(await Manager.PutAsJsonAsync($"{url}/tasks/{Id(t)}", new { title = Str(t, "title"), ownerId = Str(t, "ownerId"), status = "Terminée" }));
        await Json(await Manager.PutAsJsonAsync($"{url}/tasks/{Id(extra)}", new { title = "Commander un écran", status = "Annulée" }));
        Assert.Equal("Satisfaite", Str(await Json(await Manager.PutAsJsonAsync(url, Request("Poste pour un nouvel arrivant", Id(item), "Satisfaite"))), "status"));
        // Satisfaite : les tâches ne changent plus.
        Assert.Equal(HttpStatusCode.BadRequest, (await Manager.PutAsJsonAsync($"{url}/tasks/{Id(extra)}", new { title = "x", status = "À faire" })).StatusCode);

        // Modèle pré-approuvé : tâches créées avec la demande.
        var auto = await Item($"Accès VPN {Guid.NewGuid():N}"[..22], approval: false);
        var pre = await Json(await requester.PostAsJsonAsync("/api/requests", Request("Accès VPN", Id(auto))));
        Assert.Equal("Approuvée", Str(pre, "status"));
        Assert.Equal(2, (await Tasks(requester, Id(pre))).Count);
    }

    private async Task<JsonElement> Satisfied(HttpClient requester, string title)
    {
        var item = await Item($"{title} {Guid.NewGuid():N}"[..24]);
        var r = await Json(await requester.PostAsJsonAsync("/api/requests", Request(title, Id(item))));
        var url = $"/api/requests/{Id(r)}";
        await Json(await Manager.PutAsJsonAsync(url, Request(title, Id(item), "Approuvée")));
        foreach (var t in await Tasks(Manager, Id(r)))
            await Json(await Manager.PutAsJsonAsync($"{url}/tasks/{Id(t)}", new { title = Str(t, "title"), status = "Terminée" }));
        await Json(await Manager.PutAsJsonAsync(url, Request(title, Id(item), "En cours")));
        return await Json(await Manager.PutAsJsonAsync(url, Request(title, Id(item), "Satisfaite")));
    }

    [Fact]
    public async Task REQ_11_REQ_32_cloture_confirmee_ou_automatique_puis_enquete_de_satisfaction()
    {
        var requester = api.Client("User", "demandeur.req11");
        var r = await Satisfied(requester, "Badge");
        var url = $"/api/requests/{Id(r)}";
        var item = r.GetProperty("catalogItemId").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client("User", "tiers.req11").PutAsJsonAsync(url, Request("Badge", item, "Close"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.PostAsJsonAsync($"{url}/satisfaction", new { score = 4 })).StatusCode);   // pas encore close
        var closed = await Json(await requester.PutAsJsonAsync(url, Request("Badge", item, "Close")));
        Assert.False(closed.GetProperty("autoClosed").GetBoolean());

        // REQ-32 : demandeur seul, note de 1 à 5.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client("User", "tiers.req11").PostAsJsonAsync($"{url}/satisfaction", new { score = 4 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await requester.PostAsJsonAsync($"{url}/satisfaction", new { score = 6 })).StatusCode);
        var rated = await Json(await requester.PostAsJsonAsync($"{url}/satisfaction", new { score = 4, comment = "Rapide" }));
        Assert.Equal((4, "Rapide"), (rated.GetProperty("satisfactionScore").GetInt32(), Str(rated, "satisfactionComment")));

        // REQ-11 : sans contestation, close automatiquement 5 jours ouvrés après la satisfaction.
        var later = await Satisfied(requester, "Écran");
        await using (var db = api.Database())
        {
            var e = await db.ServiceRequests.SingleAsync(x => x.Id == Id(later));
            e.FulfilledAt = DateTime.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        }
        var recent = await Satisfied(requester, "Clavier");
        await Json(await api.Client("Admin", "admin.req11").PostAsync("/api/notifications/run", null));
        var auto = await Json(await requester.GetAsync($"/api/requests/{Id(later)}"));
        Assert.Equal(("Close", true), (Str(auto, "status"), auto.GetProperty("autoClosed").GetBoolean()));
        Assert.Equal("Satisfaite", Str(await Json(await requester.GetAsync($"/api/requests/{Id(recent)}")), "status"));
        await using var check = api.Database();
        Assert.True(await check.Notifications.AnyAsync(n => n.TargetType == "request" && n.TargetId == Id(later) && n.Event == "Demande close automatiquement"));
    }
}
