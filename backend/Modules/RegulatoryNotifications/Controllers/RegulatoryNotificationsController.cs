using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SAloha.Api.Core.Data;
using SAloha.Api.Core.Notifications;
using SAloha.Api.Core.Records;

namespace SAloha.Api.Modules.RegulatoryNotifications;

/// <summary>
/// Déclarations réglementaires (DRG) : registre lu par tous ; qualifier, décider, enregistrer
/// un envoi et clore sont des décisions de gestionnaire (DRG-02) ; le contenu se complète
/// par tous tant que la déclaration n'est pas close (DRG-07).
/// </summary>
[Route("api/regulatory-notifications")]
public class RegulatoryNotificationsController(AppDbContext db)
    : RecordController<RegulatoryNotification, RegulatoryNotificationDto, RegulatoryNotificationView>(db)
{
    protected override RegulatoryNotificationView View(RegulatoryNotification e) => RegulatoryNotificationView.From(e);
    protected override string Code => "DRG";
    protected override string LinkType => "notification";
    protected override string[] Statuses => RegulatoryNotification.Statuses;
    protected override IReadOnlyDictionary<string, string[]> Transitions => RegulatoryNotification.Transitions;

    // DRG-02 : créer une déclaration et en changer le statut sont des décisions de gestionnaire.
    protected override bool CanCreate() => IsManager;
    protected override bool RequiresManager(string from, string to) => true;

    protected override IQueryable<RegulatoryNotification> WithDetails(IQueryable<RegulatoryNotification> q) => q.Include(x => x.Incident);

    protected override IQueryable<RegulatoryNotification> Filter(IQueryable<RegulatoryNotification> q)
    {
        q = q.Include(x => x.Incident);
        if (int.TryParse(Request.Query["incident"], out var incident)) q = q.Where(x => x.IncidentId == incident);
        if (Request.Query["regime"].ToString() is { Length: > 0 } regime) q = q.Where(x => x.Regime == regime);
        return q;
    }

    protected override string? Apply(RegulatoryNotification e, RegulatoryNotificationDto dto, bool creating)
    {
        if (creating)
        {
            if (Allowed.Check("Régime", dto.Regime, RegulatoryNotification.Regimes) is { } regimeError) return regimeError;
            (e.IncidentId, e.Regime) = (dto.IncidentId, dto.Regime!);
        }
        else if (dto.IncidentId != e.IncidentId || (dto.Regime is not null && dto.Regime != e.Regime))
            return "Une déclaration ne change ni d'incident ni de régime : en créer une autre.";

        // DRG-07 : le contenu se complète tant que la déclaration n'est pas close.
        var content = (dto.Summary, dto.Impact, dto.MeasuresTaken, dto.SuspectedMalicious, dto.CrossBorderImpact,
            dto.PersonalDataCategories, dto.DataSubjectsCount, dto.LateReason);
        var before = (e.Summary, e.Impact, e.MeasuresTaken, e.SuspectedMalicious, e.CrossBorderImpact,
            e.PersonalDataCategories, e.DataSubjectsCount, e.LateReason);
        var rubrics = (Text(dto.Severity), Text(dto.CompromiseIndicators), Text(dto.BreachNature), Text(dto.LikelyConsequences));
        if (!creating && RegulatoryNotification.Done.Contains(e.Status)
            && (Clean(content) != Clean(before) || rubrics != (e.Severity, e.CompromiseIndicators, e.BreachNature, e.LikelyConsequences)))
            return $"Déclaration {e.Status.ToLowerInvariant()} : son contenu ne se modifie plus.";
        // DRG-08 : rubriques du modèle de contenu, valeurs fermées pour la gravité et la nature.
        if (Allowed.Check("Gravité", rubrics.Item1, RegulatoryNotification.Severities, optional: true) is { } severityError) return severityError;
        if (Allowed.Check("Nature de la violation", rubrics.Item3, RegulatoryNotification.BreachNatures, optional: true) is { } natureError) return natureError;
        (e.Severity, e.CompromiseIndicators, e.BreachNature, e.LikelyConsequences) = rubrics;
        if (dto.DataSubjectsCount is < 0) return "Le nombre de personnes concernées ne peut pas être négatif.";
        e.Summary = Text(dto.Summary); e.Impact = Text(dto.Impact); e.MeasuresTaken = Text(dto.MeasuresTaken);
        e.SuspectedMalicious = dto.SuspectedMalicious; e.CrossBorderImpact = dto.CrossBorderImpact;
        e.PersonalDataCategories = Text(dto.PersonalDataCategories); e.DataSubjectsCount = dto.DataSubjectsCount;
        e.LateReason = Text(dto.LateReason);

        // DRG-02 : autorité, prise de connaissance, envois, motif de non-déclaration : gestionnaire.
        var authority = Text(dto.Authority) ?? (creating ? RegulatoryNotification.AuthorityOf(e.Regime) : e.Authority);
        var awareAt = Dates.Utc(dto.AwareAt) ?? (creating ? default : e.AwareAt);
        var sent = (Dates.Utc(dto.EarlyWarningSentAt), Dates.Utc(dto.NotificationSentAt), Dates.Utc(dto.FinalReportSentAt));
        var managed = (authority, awareAt, sent, Text(dto.AuthorityCaseRef), Text(dto.NotRequiredReason));
        var current = (e.Authority, e.AwareAt, (e.EarlyWarningSentAt, e.NotificationSentAt, e.FinalReportSentAt), e.AuthorityCaseRef, e.NotRequiredReason);
        if (!creating && managed != current && !IsManager)
            return "Seul un gestionnaire modifie l'autorité, la prise de connaissance, les envois ou le motif de non-déclaration.";
        if (!creating && awareAt != e.AwareAt && string.IsNullOrWhiteSpace(dto.AwareAtReason))
            return "Motiver le changement de la prise de connaissance : il déplace les échéances légales.";

        // DRG-05 : un envoi a sa date réelle, ni avant la prise de connaissance ni dans le futur.
        var reference = awareAt == default ? (DateTime?)null : awareAt;
        foreach (var (label, at) in new[] { ("de l'alerte précoce", sent.Item1), ("de la notification", sent.Item2), ("du rapport final", sent.Item3) })
        {
            if (at is null) continue;
            if (at > DateTime.UtcNow.AddMinutes(5)) return $"La date d'envoi {label} ne peut pas être dans le futur.";
            if (reference is { } r && at < r) return $"La date d'envoi {label} ne peut pas précéder la prise de connaissance.";
        }
        e.Authority = authority;
        if (awareAt != default)
        {
            if (!creating && awareAt != e.AwareAt) e.AwareAtReason = dto.AwareAtReason!.Trim();
            e.AwareAt = awareAt;
        }
        (e.EarlyWarningSentAt, e.NotificationSentAt, e.FinalReportSentAt) = sent;
        e.AuthorityCaseRef = Text(dto.AuthorityCaseRef);
        e.NotRequiredReason = Text(dto.NotRequiredReason);
        return null;
    }

    private static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static (string?, string?, string?, bool?, bool?, string?, int?, string?) Clean(
        (string?, string?, string?, bool?, bool?, string?, int?, string?) c) =>
        (Text(c.Item1), Text(c.Item2), Text(c.Item3), c.Item4, c.Item5, Text(c.Item6), c.Item7, Text(c.Item8));

    /// <summary>DRG-01 : l'incident existe, l'utilisateur le voit, il est de nature cyber (INC-40), même périmètre.</summary>
    protected override async Task<string?> ValidateAsync(RegulatoryNotification e)
    {
        var incident = await Db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == e.IncidentId);
        if (incident is null || !(await ScopeAsync()).Sees(incident.PerimeterId)) return "Aucun incident de cet identifiant.";
        if (!incident.IsSecurity) return $"{incident.Reference} n'est pas marqué incident de sécurité : une déclaration réglementaire porte sur un incident cyber.";
        if (incident.PerimeterId != e.PerimeterId) return "Une déclaration est dans le périmètre de son incident.";
        return null;
    }

    /// <summary>
    /// DRG-01, PER-07 : une déclaration ne quitte pas seule le périmètre de son incident ; elle le
    /// suit quand l'incident change de périmètre. (La validation ci-dessus lit le périmètre d'avant.)
    /// </summary>
    protected override async Task<string?> CheckMoveAsync(RegulatoryNotification e, int to) =>
        await Db.Incidents.AnyAsync(i => i.Id == e.IncidentId && i.PerimeterId != to)
            ? "Une déclaration est dans le périmètre de son incident : déplacer l'incident, ses déclarations le suivent." : null;

    /// <summary>DRG-03 : une déclaration par régime et par incident.</summary>
    protected override async Task<string?> ConflictAsync(RegulatoryNotification e) =>
        await Db.RegulatoryNotifications.AnyAsync(x => x.Id != e.Id && x.IncidentId == e.IncidentId && x.Regime == e.Regime)
            ? $"Cet incident a déjà une déclaration {e.Regime}." : null;

    protected override string? CheckStatus(RegulatoryNotification e, string status)
    {
        var nis2 = e.Regime == RegulatoryNotification.Nis2;
        switch (status)
        {
            case RegulatoryNotification.NotRequired when e.NotRequiredReason is null:
                return "Motiver la décision de ne pas déclarer (ex. incident non important au sens NIS 2).";   // DRG-04
            case RegulatoryNotification.EarlyWarningSent when e.Status != status:
                if (!nis2) return "L'alerte précoce est une étape de NIS 2 : une déclaration RGPD passe directement à la notification.";
                if (e.EarlyWarningSentAt is null) return "Renseigner la date d'envoi de l'alerte précoce.";
                if (e.SuspectedMalicious is null || e.CrossBorderImpact is null)
                    return "L'alerte précoce indique si la cause est supposée malveillante et si l'incident a des effets transfrontières.";
                break;
            case RegulatoryNotification.NotificationSent when e.Status != status:
                if (nis2 && e.EarlyWarningSentAt is null) return "NIS 2 : envoyer d'abord l'alerte précoce.";   // DRG-06
                if (e.NotificationSentAt is null) return "Renseigner la date d'envoi de la notification.";
                if (e.Summary is null || e.Impact is null || e.MeasuresTaken is null)
                    return "La notification porte un résumé, l'impact et les mesures prises.";
                if (!nis2 && (e.PersonalDataCategories is null || e.DataSubjectsCount is null))
                    return "RGPD : renseigner les catégories de données et le nombre approximatif de personnes concernées.";
                if (!nis2 && e.NotificationSentAt > e.AwareAt.AddHours(72) && e.LateReason is null)
                    return "RGPD : notification envoyée après 72 h, en indiquer les motifs du retard.";   // DRG-12
                break;
            case RegulatoryNotification.FinalReportSent when e.Status != status:
                if (!nis2) return "Le rapport final est une étape de NIS 2.";
                if (e.FinalReportSentAt is null) return "Renseigner la date d'envoi du rapport final.";
                break;
            case RegulatoryNotification.Closed when e.Status == RegulatoryNotification.NotificationSent && nis2:
                return "NIS 2 : envoyer le rapport final avant de clore la déclaration.";
        }
        return null;
    }

    /// <summary>DRG-07 : le contenu est figé à chaque envoi (version conservée).</summary>
    protected override void OnStatusChanged(RegulatoryNotification e, string from)
    {
        switch (e.Status)
        {
            case RegulatoryNotification.EarlyWarningSent: e.Freeze("Alerte précoce", e.EarlyWarningSentAt!.Value); break;
            case RegulatoryNotification.NotificationSent: e.Freeze("Notification", e.NotificationSentAt!.Value); break;
            case RegulatoryNotification.FinalReportSent: e.Freeze("Rapport final", e.FinalReportSentAt!.Value); break;
        }
    }

    /// <summary>Prise de connaissance par défaut : la création de l'incident ; DRG-10 : échéances.</summary>
    protected override async Task BeforeSaveAsync(RegulatoryNotification e, bool creating)
    {
        if (e.AwareAt == default)
            e.AwareAt = await Db.Incidents.Where(i => i.Id == e.IncidentId).Select(i => i.CreatedAt).FirstAsync();
        if (creating && string.IsNullOrWhiteSpace(e.Title))
            e.Title = $"{e.Regime} — " + await Db.Incidents.Where(i => i.Id == e.IncidentId).Select(i => i.Reference + " " + i.Title).FirstAsync();
        e.ComputeDueDates();
    }

    /// <summary>DRG-13 : déclaration créée → tous les gestionnaires.</summary>
    protected override void OnNotify(RegulatoryNotification e, string? from, Notifier n)
    {
        if (from is null) n.ToManagers(e, LinkType, "Déclaration réglementaire créée");
    }

    /// <summary>La déclaration est reliée à son incident (onglet Liens des deux fiches).</summary>
    protected override async Task OnCreatedAsync(RegulatoryNotification e)
    {
        Db.ItemLinks.Add(new Core.Links.ItemLink { FromType = LinkType, FromId = e.Id, ToType = "incident", ToId = e.IncidentId, CreatedBy = Me });
        await Db.SaveChangesAsync();
    }
}
