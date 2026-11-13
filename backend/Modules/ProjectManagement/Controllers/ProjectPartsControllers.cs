using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;
using SAloha.Api.Core.Records;

namespace SAloha.Api.Modules.ProjectManagement;

/// <summary>Phases (cycle en V) et étapes de gestion (PRINCE2) — PRJ-10, PRJ-20, PRJ-21.</summary>
[Route("api/projects/{projectId:int}/phases")]
public class PhasesController(AppDbContext db) : ProjectPartController<ProjectPhase, PhaseDto, ProjectPhaseView>(db)
{
    protected override ProjectPhaseView View(ProjectPhase e) => ProjectPhaseView.From(e);
    protected override string[] Components => [Methods.Phases, Methods.Stages];
    protected override bool LeadOnly => true;
    protected override IQueryable<ProjectPhase> Order(IQueryable<ProjectPhase> q) => q.OrderBy(x => x.Order).ThenBy(x => x.Id);

    protected override string? Apply(ProjectPhase e, PhaseDto dto, Project project, bool creating)
    {
        var error = Required(dto.Name, "Nom") ?? Allowed.Check("Statut", dto.Status, ProjectPhase.Statuses, optional: true);
        if (error is not null) return error;
        e.Kind = project.Has(Methods.Stages) && !project.Has(Methods.Phases) ? "Étape"
            : Allowed.Check("Type", dto.Kind, ProjectPhase.Kinds, optional: true) is null && dto.Kind is { Length: > 0 } k ? k : "Phase";
        e.Name = dto.Name.Trim();
        e.Order = dto.Order ?? e.Order;
        e.PlannedStart = Dates.Utc(dto.PlannedStart); e.PlannedEnd = Dates.Utc(dto.PlannedEnd);
        if (e.PlannedStart is not null && e.PlannedEnd is not null && e.PlannedEnd < e.PlannedStart)
            return "La fin prévue précède le début prévu.";
        // PRJ-21 : une étape PRINCE2 ne se termine que par la validation de fin d'étape.
        if (dto.Status == "Terminée" && e.Kind == "Étape" && e.ApprovedAt is null)
            return "Une étape se termine par sa validation de fin d'étape.";
        e.Status = dto.Status ?? e.Status;
        return null;
    }

    protected override async Task OnSavingAsync(ProjectPhase e, ProjectPhase? before)
    {
        // Sans ordre fourni, la phase vient en dernier.
        if (before is null && e.Order == 0)
            e.Order = (await Db.ProjectPhases.Where(x => x.ProjectId == e.ProjectId).MaxAsync(x => (int?)x.Order) ?? 0) + 1;
    }

    /// <summary>PRJ-21 : validation de fin d'étape, par le chef de projet ou un gestionnaire.</summary>
    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int projectId, int id)
    {
        // Une écriture comme les autres : projet figé (PRJ-07), composant (PRJ-03), chef de projet.
        var (_, error) = await Load(projectId, true);
        if (error is not null) return error;
        var e = await Db.ProjectPhases.FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
        if (e is null) return NotFound();
        if (e.Status == "Terminée") return BadRequest(new { message = "Cette étape est déjà terminée." });
        // On n'engage qu'une étape à la fois : les précédentes doivent être terminées.
        var pending = await Db.ProjectPhases.Where(x => x.ProjectId == projectId && x.Order < e.Order && x.Status != "Terminée")
            .OrderBy(x => x.Order).Select(x => x.Name).FirstOrDefaultAsync();
        if (pending is not null) return BadRequest(new { message = $"Valider d'abord l'étape « {pending} »." });
        e.Status = "Terminée"; e.ApprovedAt = DateTime.UtcNow; e.ApprovedBy = MyDisplay;
        // L'étape suivante démarre (PRINCE2 : on n'engage qu'une étape à la fois).
        var next = await Db.ProjectPhases.Where(x => x.ProjectId == projectId && x.Order > e.Order && x.Status == "À venir")
            .OrderBy(x => x.Order).FirstOrDefaultAsync();
        if (next is not null) next.Status = "En cours";
        await Db.SaveChangesAsync();
        return Ok(View(e));
    }
}

[Route("api/projects/{projectId:int}/milestones")]
public class MilestonesController(AppDbContext db) : ProjectPartController<Milestone, MilestoneDto, MilestoneView>(db)
{
    protected override MilestoneView View(Milestone e) => MilestoneView.From(e);
    protected override string[] Components => [Methods.Milestones];
    protected override bool LeadOnly => true;
    protected override IQueryable<Milestone> Order(IQueryable<Milestone> q) => q.OrderBy(x => x.DueDate).ThenBy(x => x.Id);

    protected override string? Apply(Milestone e, MilestoneDto dto, Project project, bool creating)
    {
        var error = Required(dto.Name, "Nom");
        if (error is not null) return error;
        e.Name = dto.Name.Trim(); e.PhaseId = dto.PhaseId; e.DueDate = Dates.Utc(dto.DueDate);
        e.AchievedAt = dto.Achieved ? e.AchievedAt ?? DateTime.UtcNow : null;
        return null;
    }
}

/// <summary>Livrables (PRJ-11) : seul le chef de projet ou un gestionnaire valide.</summary>
[Route("api/projects/{projectId:int}/deliverables")]
public class DeliverablesController(AppDbContext db) : ProjectPartController<Deliverable, DeliverableDto, DeliverableView>(db)
{
    protected override DeliverableView View(Deliverable e) => DeliverableView.From(e);
    protected override string[] Components => [Methods.Deliverables];
    protected override IQueryable<Deliverable> Order(IQueryable<Deliverable> q) => q.OrderBy(x => x.DueDate).ThenBy(x => x.Id);

    protected override string? Apply(Deliverable e, DeliverableDto dto, Project project, bool creating)
    {
        var error = Required(dto.Name, "Nom") ?? Allowed.Check("Statut", dto.Status, Deliverable.Statuses, optional: true);
        if (error is not null) return error;
        var status = dto.Status ?? e.Status;
        if (status == "Validé" && e.Status != "Validé" && !IsLead(project))
            return "Seul le chef de projet ou un gestionnaire valide un livrable.";
        e.Name = dto.Name.Trim(); e.Description = dto.Description ?? ""; e.PhaseId = dto.PhaseId;
        e.DueDate = Dates.Utc(dto.DueDate);
        if (status == "Validé" && e.Status != "Validé") { e.ValidatedAt = DateTime.UtcNow; e.ValidatedBy = MyDisplay; }
        else if (status != "Validé") { e.ValidatedAt = null; e.ValidatedBy = null; }
        e.Status = status;
        return null;
    }
}

/// <summary>Sprints (PRJ-12) : un seul actif ; un sprint clos ne se rouvre pas.</summary>
[Route("api/projects/{projectId:int}/sprints")]
public class SprintsController(AppDbContext db) : ProjectPartController<Sprint, SprintDto, SprintView>(db)
{
    protected override SprintView View(Sprint e) => SprintView.From(e);
    protected override string[] Components => [Methods.Sprints];
    protected override bool LeadOnly => true;
    protected override IQueryable<Sprint> Order(IQueryable<Sprint> q) => q.OrderBy(x => x.StartDate).ThenBy(x => x.Id);

    protected override string? Apply(Sprint e, SprintDto dto, Project project, bool creating)
    {
        var error = Required(dto.Name, "Nom") ?? Allowed.Check("Statut", dto.Status, Sprint.Statuses, optional: true);
        if (error is not null) return error;
        var status = dto.Status ?? e.Status;
        if (e.Status == "Clos" && status != "Clos") return "Un sprint clos ne se rouvre pas.";
        e.Name = dto.Name.Trim(); e.Goal = dto.Goal ?? "";
        e.StartDate = Dates.Utc(dto.StartDate); e.EndDate = Dates.Utc(dto.EndDate);
        if (status == "Actif" && (e.StartDate is null || e.EndDate is null))
            return "Un sprint actif a une date de début et une date de fin.";
        if (e.StartDate is not null && e.EndDate is not null && e.EndDate < e.StartDate)
            return "La fin du sprint précède son début.";
        e.Status = status;
        return null;
    }

    protected override async Task<string?> ValidateAsync(Sprint e, Sprint? before) =>
        e.Status == "Actif" && await Db.Sprints.AnyAsync(x => x.ProjectId == e.ProjectId && x.Id != e.Id && x.Status == "Actif")
            ? "Un autre sprint est déjà actif : le clore d'abord." : null;

    protected override async Task OnSavingAsync(Sprint e, Sprint? before)
    {
        // À la clôture, ce qui n'est pas terminé retourne au backlog.
        if (e.Status == "Clos" && before?.Status != "Clos")
            await Db.WorkItems.Where(w => w.SprintId == e.Id && w.Status != "Terminé")
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.SprintId, (int?)null));
    }

    /// <summary>
    /// PRJ-13 : burndown d'un sprint — points restants à la fin de chaque jour
    /// (points du sprint − points terminés à cette date), et ligne idéale.
    /// </summary>
    [HttpGet("{id:int}/burndown")]
    public async Task<IActionResult> Burndown(int projectId, int id)
    {
        if (await ProjectAsync(projectId) is null) return NotFound();
        var s = await Db.Sprints.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ProjectId == projectId);
        if (s is null) return NotFound();
        if (s.StartDate is null || s.EndDate is null) return BadRequest(new { message = "Le sprint n'a pas de dates." });
        var items = await Db.WorkItems.AsNoTracking().Where(w => w.SprintId == id || (s.Status == "Clos" && w.ProjectId == projectId
            && w.CompletedAt != null && w.CompletedAt >= s.StartDate && w.CompletedAt < s.EndDate.Value.AddDays(1))).ToListAsync();
        var total = items.Sum(w => w.Points ?? 0);
        var start = s.StartDate.Value.Date;
        var days = (s.EndDate.Value.Date - start).Days;
        var today = DateTime.UtcNow.Date;
        return Ok(new
        {
            Total = total,
            Days = Enumerable.Range(0, days + 1).Select(i =>
            {
                var day = start.AddDays(i);
                var done = items.Where(w => w.CompletedAt != null && w.CompletedAt.Value.Date <= day).Sum(w => w.Points ?? 0);
                return new
                {
                    Date = day,
                    Ideal = days == 0 ? 0 : Math.Round(total - total * (double)i / days, 1),
                    // Les jours à venir n'ont pas encore de valeur réelle.
                    Remaining = day <= today ? total - done : (int?)null
                };
            })
        });
    }
}

/// <summary>Éléments de travail (PRJ-14) : tous les rôles les créent et les déplacent.</summary>
[Route("api/projects/{projectId:int}/workitems")]
public class WorkItemsController(AppDbContext db) : ProjectPartController<WorkItem, WorkItemDto, WorkItemView>(db)
{
    protected override WorkItemView View(WorkItem e) => WorkItemView.From(e);
    protected override string[] Components => [Methods.Backlog, Methods.Board];
    protected override IQueryable<WorkItem> Order(IQueryable<WorkItem> q) => q.OrderBy(x => x.Rank).ThenBy(x => x.Id);

    protected override string? Apply(WorkItem e, WorkItemDto dto, Project project, bool creating)
    {
        var error = Required(dto.Title, "Titre") ?? Allowed.Check("Type", dto.Type, WorkItem.Types, optional: true)
            ?? Allowed.Check("Statut", dto.Status, WorkItem.Statuses, optional: true);
        if (error is not null) return error;
        if (dto.Points is < 0 or > 100) return "Les points vont de 0 à 100.";
        if (dto.SprintId is not null && !project.Has(Methods.Sprints)) return "Les sprints ne sont pas activés pour ce projet.";
        e.Type = dto.Type ?? e.Type; e.Title = dto.Title.Trim(); e.Description = dto.Description ?? "";
        e.AcceptanceCriteria = dto.AcceptanceCriteria ?? ""; e.Points = dto.Points; e.SprintId = dto.SprintId;
        if (dto.Rank is not null) e.Rank = dto.Rank.Value;
        var status = dto.Status ?? e.Status;
        // La date d'achèvement nourrit le burndown : posée à l'entrée dans
        // « Terminé », effacée si l'élément en ressort.
        if (status == "Terminé" && e.Status != "Terminé") e.CompletedAt = DateTime.UtcNow;
        else if (status != "Terminé") e.CompletedAt = null;
        e.Status = status;
        var hasAssignee = !string.IsNullOrWhiteSpace(dto.AssigneeId);
        e.AssigneeId = hasAssignee ? dto.AssigneeId : null;
        e.AssigneeDisplayName = hasAssignee ? dto.AssigneeDisplayName ?? dto.AssigneeId : null;
        return null;
    }

    protected override async Task<string?> ValidateAsync(WorkItem e, WorkItem? before)
    {
        if (e.SprintId is null || e.SprintId == before?.SprintId) return null;
        var sprint = await Db.Sprints.AsNoTracking().FirstOrDefaultAsync(s => s.Id == e.SprintId && s.ProjectId == e.ProjectId);
        if (sprint is null) return "Sprint inconnu pour ce projet.";
        return sprint.Status == "Clos" ? "On n'ajoute rien à un sprint clos." : null;
    }

    protected override async Task OnSavingAsync(WorkItem e, WorkItem? before)
    {
        // Un nouvel élément se range en fin de backlog.
        if (before is null && e.Rank == 0)
            e.Rank = (await Db.WorkItems.Where(x => x.ProjectId == e.ProjectId).MaxAsync(x => (int?)x.Rank) ?? 0) + 1;
    }
}

/// <summary>Registre des risques (PRJ-15) : score = probabilité × impact.</summary>
[Route("api/projects/{projectId:int}/risks")]
public class ProjectRisksController(AppDbContext db) : ProjectPartController<ProjectRisk, RiskDto, ProjectRiskView>(db)
{
    protected override ProjectRiskView View(ProjectRisk e) => ProjectRiskView.From(e);
    protected override string[] Components => [Methods.Risks];
    protected override IQueryable<ProjectRisk> Order(IQueryable<ProjectRisk> q) =>
        q.OrderBy(x => x.Status).ThenByDescending(x => x.Probability * x.Impact).ThenBy(x => x.Id);

    protected override string? Apply(ProjectRisk e, RiskDto dto, Project project, bool creating)
    {
        var error = Required(dto.Title, "Titre") ?? Allowed.Check("Réponse", dto.Response, ProjectRisk.Responses, optional: true)
            ?? Allowed.Check("Statut", dto.Status, ProjectRisk.Statuses, optional: true);
        if (error is not null) return error;
        if (dto.Probability is < 1 or > 5 || dto.Impact is < 1 or > 5) return "Probabilité et impact vont de 1 à 5.";
        e.Title = dto.Title.Trim(); e.Description = dto.Description ?? "";
        e.Probability = dto.Probability; e.Impact = dto.Impact;
        e.Response = dto.Response ?? e.Response; e.OwnerDisplayName = dto.OwnerDisplayName; e.Status = dto.Status ?? e.Status;
        return null;
    }
}

/// <summary>Registre des incidents projet (PRJ-15) : clos seulement avec une résolution.</summary>
[Route("api/projects/{projectId:int}/issues")]
public class ProjectIssuesController(AppDbContext db) : ProjectPartController<ProjectIssue, IssueDto, ProjectIssueView>(db)
{
    protected override ProjectIssueView View(ProjectIssue e) => ProjectIssueView.From(e);
    protected override string[] Components => [Methods.Issues];
    protected override IQueryable<ProjectIssue> Order(IQueryable<ProjectIssue> q) => q.OrderBy(x => x.Status == "Clos").ThenByDescending(x => x.CreatedAt);

    protected override string? Apply(ProjectIssue e, IssueDto dto, Project project, bool creating)
    {
        var error = Required(dto.Title, "Titre") ?? Allowed.Check("Type", dto.Type, ProjectIssue.Types, optional: true)
            ?? Allowed.Check("Gravité", dto.Severity, ProjectIssue.Severities, optional: true)
            ?? Allowed.Check("Statut", dto.Status, ProjectIssue.Statuses, optional: true);
        if (error is not null) return error;
        var status = dto.Status ?? e.Status;
        if (status == "Clos" && string.IsNullOrWhiteSpace(dto.Resolution)) return "Décrire la résolution avant de clore.";
        e.Type = dto.Type ?? e.Type; e.Title = dto.Title.Trim(); e.Description = dto.Description ?? "";
        e.Severity = dto.Severity ?? e.Severity; e.Resolution = dto.Resolution;
        e.ClosedAt = status == "Clos" ? e.ClosedAt ?? DateTime.UtcNow : null;
        e.Status = status;
        return null;
    }
}
