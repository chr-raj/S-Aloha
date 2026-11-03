using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;

namespace SAloha.Api.Tests;

/// <summary>Étape 4 du lot 1 : droits et statuts (INC-11, INC-13, REQ-09, CFG-12 à 14, PRB-24).</summary>
[Collection("api")]
public class RightsAndStatusesTests(ApiFixture api)
{
    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static async Task<string> Refused(HttpResponseMessage res, HttpStatusCode code = HttpStatusCode.BadRequest)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == code, $"Attendu {(int)code}, reçu {(int)res.StatusCode} : {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("message").GetString()!;
    }

    private static int Id(JsonElement e) => e.GetProperty("id").GetInt32();
    private static string Ref(JsonElement e) => e.GetProperty("reference").GetString()!;

    // ---------- INC-11 : Réouverture encadrée d'un incident ----------

    [Fact]
    public async Task INC_11_reouverture_dans_les_10_jours_avec_motif_et_compteur()
    {
        var m = api.Client("Manager");
        var dto = new
        {
            title = "Incident à rouvrir", impact = "Moyen", urgency = "Moyenne",
            status = (string?)null, resolution = "Patch appliqué", resolutionCode = "Correctif appliqué",
            ownerType = "User", ownerId = "hery.rakoto", ownerDisplayName = "Hery Rakoto"
        };
        var inc = await Json(await m.PostAsJsonAsync("/api/incidents", dto));
        await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", dto with { status = "En cours" }));
        await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", dto with { status = "Résolu" }));

        // Réouverture sans motif refusée
        var err = await Refused(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", dto with { status = "En cours" }));
        Assert.Equal("Motiver la réouverture de l'incident.", err);

        // Réouverture avec motif acceptée
        object Reopen(string? reason) => new
        {
            dto.title, dto.impact, dto.urgency, status = "En cours", dto.resolution, dto.resolutionCode,
            dto.ownerType, dto.ownerId, dto.ownerDisplayName, reopenReason = reason
        };
        var reopened = await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", Reopen("Le bug se reproduit")));
        Assert.Equal("En cours", reopened.GetProperty("status").GetString());
        Assert.Equal(1, reopened.GetProperty("reopenCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("resolvedAt").ValueKind);

        // Le motif est effacé à la résolution suivante : une nouvelle réouverture en exige un nouveau.
        var resolved = await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", dto with { status = "Résolu" }));
        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("reopenReason").ValueKind);
        await Refused(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", Reopen(null)));
        var again = await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", Reopen("Toujours présent")));
        Assert.Equal(2, again.GetProperty("reopenCount").GetInt32());
    }

    [Fact]
    public void INC_11_jours_ouvres_sautent_le_week_end()
    {
        var friday = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc), SAloha.Api.Core.Records.Dates.AddBusinessDays(friday, 1));
        Assert.Equal(new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc), SAloha.Api.Core.Records.Dates.AddBusinessDays(friday, 10));
    }

    // ---------- INC-13 : Incident majeur ----------

    [Fact]
    public async Task INC_13_seul_un_gestionnaire_declare_ou_retire_incident_majeur_et_priorite_P1()
    {
        var u = api.Client("User", "user.lambda");
        var m = api.Client("Manager", "manager.test");

        // Utilisateur ne peut pas déclarer à la création
        var createErr = await Refused(await u.PostAsJsonAsync("/api/incidents", new
        {
            title = "Majeur par user", impact = "Faible", urgency = "Faible", isMajor = true
        }));
        Assert.Equal("Seul un gestionnaire peut déclarer un incident majeur.", createErr);

        // Utilisateur crée incident normal
        var inc = await Json(await u.PostAsJsonAsync("/api/incidents", new
        {
            title = "Incident normal", impact = "Faible", urgency = "Faible", isMajor = false
        }));
        Assert.Equal("P4", inc.GetProperty("priority").GetString());

        // Utilisateur ne peut pas changer isMajor à la modification
        var modErr = await Refused(await u.PutAsJsonAsync($"/api/incidents/{Id(inc)}", new
        {
            title = "Incident normal", impact = "Faible", urgency = "Faible", isMajor = true
        }));
        Assert.Equal("Seul un gestionnaire peut déclarer ou retirer le caractère majeur d'un incident.", modErr);

        // Gestionnaire déclare l'incident majeur : priorité forcée à P1
        var major = await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", new
        {
            title = "Incident normal", impact = "Faible", urgency = "Faible", isMajor = true
        }));
        Assert.True(major.GetProperty("isMajor").GetBoolean());
        Assert.Equal("P1", major.GetProperty("priority").GetString());

        // Un groupe ne peut pas être responsable d'un incident majeur
        var groupErr = await Refused(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", new
        {
            title = "Incident normal", impact = "Faible", urgency = "Faible", isMajor = true,
            ownerType = "Group", ownerId = "GRP-TEST"
        }));
        Assert.Equal("Un incident majeur doit avoir un gestionnaire pour responsable (pas un groupe).", groupErr);

        // Le gestionnaire désigne le responsable ; un utilisateur ne le remplace pas…
        var owned = new
        {
            title = "Incident normal", impact = "Faible", urgency = "Faible", isMajor = true,
            ownerType = "User", ownerId = "manager.test", ownerDisplayName = "Manager Test"
        };
        await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", owned));
        var ownerErr = await Refused(await u.PutAsJsonAsync($"/api/incidents/{Id(inc)}", owned with { ownerId = "user.lambda" }));
        Assert.Equal("Seul un gestionnaire désigne le responsable d'un incident majeur.", ownerErr);

        // … mais complète la fiche et la fait avancer comme pour tout incident.
        var edited = await Json(await u.PutAsJsonAsync($"/api/incidents/{Id(inc)}", owned with { title = "Incident majeur documenté" }));
        Assert.Equal("Incident majeur documenté", edited.GetProperty("title").GetString());
        var taken = await Json(await u.PutAsJsonAsync($"/api/incidents/{Id(inc)}", new
        {
            owned.title, owned.impact, owned.urgency, owned.isMajor, owned.ownerType, owned.ownerId, owned.ownerDisplayName,
            status = "En cours"
        }));
        Assert.Equal("En cours", taken.GetProperty("status").GetString());
    }

    // ---------- REQ-09 : Statut Annulée des demandes ----------

    [Fact]
    public async Task REQ_09_demandeur_ou_gestionnaire_peut_annuler_demande()
    {
        var demandeur = api.Client("User", "alice");
        var autre = api.Client("User", "bob");
        var manager = api.Client("Manager", "charlie");

        var reqDto = new { title = "Accès VPN", requestedItem = "VPN", status = (string?)null };
        var r = await Json(await demandeur.PostAsJsonAsync("/api/requests", reqDto));

        // Un autre utilisateur ne peut pas annuler
        var err = await Refused(await autre.PutAsJsonAsync($"/api/requests/{Id(r)}", reqDto with { status = "Annulée" }));
        Assert.Equal("Seul le demandeur ou un gestionnaire peut annuler la demande.", err);

        // Le demandeur peut annuler
        var cancelled = await Json(await demandeur.PutAsJsonAsync($"/api/requests/{Id(r)}", reqDto with { status = "Annulée" }));
        Assert.Equal("Annulée", cancelled.GetProperty("status").GetString());
        Assert.NotNull(cancelled.GetProperty("cancelledAt").GetString());

        // Annulation par un manager sur une demande approuvée
        var r2 = await Json(await demandeur.PostAsJsonAsync("/api/requests", reqDto));
        await Json(await manager.PutAsJsonAsync($"/api/requests/{Id(r2)}", reqDto with { status = "Approuvée" }));
        var cancelledByMgr = await Json(await manager.PutAsJsonAsync($"/api/requests/{Id(r2)}", reqDto with { status = "Annulée" }));
        Assert.Equal("Annulée", cancelledByMgr.GetProperty("status").GetString());
    }

    // ---------- CFG-12, CFG-13, CFG-14 : Retrait d'un CI ----------

    [Fact]
    public async Task CFG_12_retrait_ci_reserve_au_gestionnaire()
    {
        var u = api.Client("User");
        var m = api.Client("Manager");

        var ci = await Json(await m.PostAsJsonAsync("/api/configuration-items", new
        {
            title = "srv-cfg-12", ciType = "Serveur", environment = "Production",
            ownerType = "User", ownerId = "hery.rakoto"
        }));

        // Non-gestionnaire refusé (403)
        var res = await u.PutAsJsonAsync($"/api/configuration-items/{Id(ci)}", new
        {
            title = "srv-cfg-12", ciType = "Serveur", environment = "Production", status = "Retiré"
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // Gestionnaire accepté
        var retired = await Json(await m.PutAsJsonAsync($"/api/configuration-items/{Id(ci)}", new
        {
            title = "srv-cfg-12", ciType = "Serveur", environment = "Production", status = "Retiré"
        }));
        Assert.Equal("Retiré", retired.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CFG_12_creation_au_statut_retire_reservee_au_gestionnaire()
    {
        var u = api.Client("User");
        var m = api.Client("Manager");
        object Dto(string title) => new
        {
            title, ciType = "Serveur", environment = "Production",
            ownerType = "User", ownerId = "hery.rakoto", status = "Retiré"
        };

        // Le statut choisi à la création n'ouvre pas ce que la transition ferme (403).
        var res = await u.PostAsJsonAsync("/api/configuration-items", Dto("srv-cfg-12-creation-user"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var created = await Json(await m.PostAsJsonAsync("/api/configuration-items", Dto("srv-cfg-12-creation-mgr")));
        Assert.Equal("Retiré", created.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CFG_13_retrait_ci_refuse_si_incident_ou_changement_ouvert()
    {
        var m = api.Client("Manager");

        var ci = await Json(await m.PostAsJsonAsync("/api/configuration-items", new
        {
            title = "srv-cfg-13", ciType = "Serveur", environment = "Production",
            ownerType = "User", ownerId = "hery.rakoto"
        }));

        var inc = await Json(await m.PostAsJsonAsync("/api/incidents", new
        {
            title = "Panne sur srv-cfg-13", impact = "Moyen", urgency = "Moyenne"
        }));

        // Relie le CI à l'incident
        await m.PostAsJsonAsync("/api/links", new { fromType = "ci", fromId = Id(ci), toReference = Ref(inc) });

        // Retrait refusé avec la référence de l'incident ouvert
        var err = await Refused(await m.PutAsJsonAsync($"/api/configuration-items/{Id(ci)}", new
        {
            title = "srv-cfg-13", ciType = "Serveur", environment = "Production", status = "Retiré"
        }));
        Assert.Contains(Ref(inc), err);

        // Résolution de l'incident
        await Json(await m.PutAsJsonAsync($"/api/incidents/{Id(inc)}", new
        {
            title = "Panne sur srv-cfg-13", impact = "Moyen", urgency = "Moyenne",
            status = "Résolu", resolution = "Corrigé", resolutionCode = "Correctif appliqué"
        }));

        // Retrait désormais permis
        var retired = await Json(await m.PutAsJsonAsync($"/api/configuration-items/{Id(ci)}", new
        {
            title = "srv-cfg-13", ciType = "Serveur", environment = "Production", status = "Retiré"
        }));
        Assert.Equal("Retiré", retired.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CFG_14_ci_retire_ne_peut_plus_etre_relie()
    {
        var m = api.Client("Manager");

        var ci = await Json(await m.PostAsJsonAsync("/api/configuration-items", new
        {
            title = "srv-cfg-14-retire", ciType = "Serveur", environment = "Production",
            ownerType = "User", ownerId = "hery.rakoto"
        }));
        await Json(await m.PutAsJsonAsync($"/api/configuration-items/{Id(ci)}", new
        {
            title = "srv-cfg-14-retire", ciType = "Serveur", environment = "Production", status = "Retiré"
        }));

        var autreCi = await Json(await m.PostAsJsonAsync("/api/configuration-items", new
        {
            title = "srv-cfg-14-actif", ciType = "Serveur", environment = "Production",
            ownerType = "User", ownerId = "hery.rakoto"
        }));

        // Liaison de CI à CI refusée
        var relErr = await Refused(await m.PostAsJsonAsync($"/api/configuration-items/{Id(autreCi)}/relations", new
        {
            type = "Dépend de", targetReference = Ref(ci)
        }));
        Assert.Equal("Un CI retiré ne peut plus être relié.", relErr);

        // Le type de relation reste contrôlé.
        var autre2 = await Json(await m.PostAsJsonAsync("/api/configuration-items", new
        {
            title = "srv-cfg-14-cible", ciType = "Serveur", environment = "Production",
            ownerType = "User", ownerId = "hery.rakoto"
        }));
        var typeErr = await Refused(await m.PostAsJsonAsync($"/api/configuration-items/{Id(autreCi)}/relations", new
        {
            type = "Inconnu", targetReference = Ref(autre2)
        }));
        Assert.StartsWith("Type de relation invalide", typeErr);

        // Liaison inter-processus via /api/links refusée
        var inc = await Json(await m.PostAsJsonAsync("/api/incidents", new
        {
            title = "Incident CFG-14", impact = "Moyen", urgency = "Moyenne"
        }));
        var linkErr = await Refused(await m.PostAsJsonAsync("/api/links", new
        {
            fromType = "incident", fromId = Id(inc), toReference = Ref(ci)
        }));
        Assert.Equal("Un CI retiré ne peut plus être relié.", linkErr);
    }

    // ---------- PRB-24 : Modification d'une action réservée à R, A ou Manager ----------

    [Fact]
    public async Task PRB_24_modification_action_permise_pour_R_A_ou_Manager_refusee_sinon()
    {
        var m = api.Client("Manager", "manager.it");
        var alice = api.Client("User", "alice.r");
        var bob = api.Client("User", "bob.a");
        var charlie = api.Client("User", "charlie.outsider");

        var p = await Json(await m.PostAsJsonAsync("/api/problems", new
        {
            title = "Problème pour actions", description = "d", impact = "Moyen", urgency = "Moyenne",
            category = "Réseau", affectedService = "Messagerie"
        }));

        var actDto = new
        {
            title = "Action correctrice", description = "Test", dueDate = DateTime.UtcNow.AddDays(7),
            raci = new[]
            {
                new { role = "R", assigneeType = "User", assigneeId = "alice.r", assigneeDisplayName = "Alice" },
                new { role = "A", assigneeType = "User", assigneeId = "bob.a", assigneeDisplayName = "Bob" }
            }
        };

        var a = await Json(await m.PostAsJsonAsync($"/api/problems/{Id(p)}/actions", actDto));

        // Charlie (ni R ni A ni Manager) se voit refuser la modification (403)
        var forbiddenRes = await charlie.PutAsJsonAsync($"/api/actions/{Id(a)}", actDto with { title = "Piraté" });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRes.StatusCode);

        // Alice (R) peut modifier
        var rRes = await alice.PutAsJsonAsync($"/api/actions/{Id(a)}", actDto with { title = "Modifié par R" });
        Assert.Equal(HttpStatusCode.OK, rRes.StatusCode);

        // Bob (A) peut modifier
        var aRes = await bob.PutAsJsonAsync($"/api/actions/{Id(a)}", actDto with { title = "Modifié par A" });
        Assert.Equal(HttpStatusCode.OK, aRes.StatusCode);

        // Le Manager peut modifier
        var mRes = await m.PutAsJsonAsync($"/api/actions/{Id(a)}", actDto with { title = "Modifié par Manager" });
        Assert.Equal(HttpStatusCode.OK, mRes.StatusCode);

        // Mettre à jour le seul statut reste ouvert à tous.
        var current = await Json(mRes);
        var statusRes = await charlie.PutAsJsonAsync($"/api/actions/{Id(a)}", new
        {
            title = "Modifié par Manager", actDto.description, dueDate = current.GetProperty("dueDate").GetDateTime(),
            status = "En cours", actDto.raci
        });
        Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
    }
}
