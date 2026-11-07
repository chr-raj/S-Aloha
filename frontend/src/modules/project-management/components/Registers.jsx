import React from 'react'
import { api } from '../../../api'
import { dateFr, dateTimeFr } from '../../../core/fields'
import PartTable from './PartTable'
import useIsLead from '../rights'

const badge = (text, tone) => <span className={'badge t-' + tone}>{text}</span>
const PHASE_TONE = { 'À venir': 'new', 'En cours': 'progress', 'Terminée': 'done' }

/** Phases (cycle en V) ou étapes de gestion (PRINCE2) — PRJ-10, PRJ-20, PRJ-21. */
export function Phases({ project, stages, onChanged }) {
  const lead = useIsLead(project)
  // PRJ-21 : seule l'étape courante (la première non terminée) se valide.
  const approve = (row, reload, rows) => stages && lead && row.id === rows.find(x => x.status !== 'Terminée')?.id && (
    <button className="btn small" onClick={() => api.projects.approveStage(project.id, row.id).then(() => { reload(); onChanged?.() })}
      title="Validation de fin d'étape : l'étape se termine et la suivante démarre">Valider la fin d'étape</button>)
  return (
    <PartTable project={project} segment="phases" onChanged={onChanged}
      title={stages ? 'Étapes de gestion' : 'Phases'}
      hint={stages
        ? 'PRINCE2 : on n\'engage qu\'une étape à la fois ; elle se termine par la validation de fin d\'étape du chef de projet ou d\'un gestionnaire.'
        : 'Cycle en V : phases séquentielles, de la spécification à la mise en production.'}
      canWrite={lead} canDelete={lead} actions={approve} empty="Aucune phase."
      fields={[
        { key: 'name', label: 'Nom', required: true },
        { key: 'order', label: 'Ordre', type: 'number' },
        { key: 'plannedStart', label: 'Début prévu', type: 'date' },
        { key: 'plannedEnd', label: 'Fin prévue', type: 'date' },
        { key: 'status', label: 'Statut', type: 'select', options: ['À venir', 'En cours', ...(stages ? [] : ['Terminée'])], default: 'À venir' },
      ]}
      columns={[
        { h: '#', v: r => r.order },
        { h: stages ? 'Étape' : 'Phase', v: r => <b>{r.name}</b> },
        { h: 'Prévu', v: r => `${dateFr(r.plannedStart)} → ${dateFr(r.plannedEnd)}` },
        { h: 'Statut', v: r => <>{badge(r.status, PHASE_TONE[r.status])}{r.approvedAt && <div className="muted small">Validée par {r.approvedBy}, {dateTimeFr(r.approvedAt)}</div>}</> },
      ]} />
  )
}

export function Milestones({ project, onChanged }) {
  const lead = useIsLead(project)
  const today = new Date().toISOString().slice(0, 10)
  return (
    <PartTable project={project} segment="milestones" title="Jalons" onChanged={onChanged}
      hint="Échéances clés du projet ; un jalon dépassé sans être atteint est signalé en retard."
      canWrite={lead} canDelete={lead} empty="Aucun jalon."
      toForm={r => ({ ...r, achieved: !!r.achievedAt })}
      fields={[
        { key: 'name', label: 'Jalon', required: true },
        { key: 'dueDate', label: 'Échéance', type: 'date' },
        { key: 'achieved', label: 'Atteint', type: 'checkbox' },
      ]}
      columns={[
        { h: 'Jalon', v: r => <b>{r.name}</b> },
        { h: 'Échéance', v: r => dateFr(r.dueDate) },
        { h: 'État', v: r => r.achievedAt ? badge('Atteint', 'done')
          : r.dueDate && r.dueDate.slice(0, 10) < today ? badge('En retard', 'bad') : badge('À venir', 'new') },
      ]} />
  )
}

const DELIVERABLE_TONE = { 'À produire': 'new', 'En cours': 'progress', 'Livré': 'wait', 'Validé': 'done' }

/** Livrables — PRJ-11 : seul le chef de projet ou un gestionnaire valide. */
export function Deliverables({ project, onChanged }) {
  const lead = useIsLead(project)
  return (
    <PartTable project={project} segment="deliverables" title="Livrables" onChanged={onChanged}
      hint="À produire → En cours → Livré → Validé. La validation revient au chef de projet ou à un gestionnaire."
      canWrite canDelete={lead} empty="Aucun livrable."
      fields={[
        { key: 'name', label: 'Livrable', required: true },
        { key: 'dueDate', label: 'Échéance', type: 'date' },
        { key: 'status', label: 'Statut', type: 'select', default: 'À produire',
          options: lead ? ['À produire', 'En cours', 'Livré', 'Validé'] : ['À produire', 'En cours', 'Livré'] },
        { key: 'description', label: 'Description', type: 'textarea', rows: 2, full: true },
      ]}
      columns={[
        { h: 'Livrable', v: r => <b>{r.name}</b> },
        { h: 'Échéance', v: r => dateFr(r.dueDate) },
        { h: 'Statut', v: r => <>{badge(r.status, DELIVERABLE_TONE[r.status])}{r.validatedBy && <div className="muted small">par {r.validatedBy}</div>}</> },
      ]} />
  )
}

/** Registre des risques — PRJ-15 : score = probabilité × impact, critique à partir de 15. */
export function Risks({ project, onChanged }) {
  const lead = useIsLead(project)
  const scale = [1, 2, 3, 4, 5].map(n => ({ value: n, label: String(n) }))
  return (
    <PartTable project={project} segment="risks" title="Registre des risques" onChanged={onChanged}
      hint="Score = probabilité × impact (1 à 5 chacun) ; critique à partir de 15." canWrite canDelete={lead}
      empty="Aucun risque enregistré."
      fields={[
        { key: 'title', label: 'Risque', required: true },
        { key: 'probability', label: 'Probabilité', type: 'select', options: scale, numeric: true, default: 3 },
        { key: 'impact', label: 'Impact', type: 'select', options: scale, numeric: true, default: 3 },
        { key: 'response', label: 'Réponse', type: 'select', options: ['Éviter', 'Réduire', 'Transférer', 'Partager', 'Accepter'], default: 'Réduire' },
        { key: 'ownerDisplayName', label: 'Propriétaire' },
        { key: 'status', label: 'Statut', type: 'select', options: ['Ouvert', 'Clos'], default: 'Ouvert' },
        { key: 'description', label: 'Description et plan de réponse', type: 'textarea', rows: 2, full: true },
      ]}
      columns={[
        { h: 'Risque', v: r => <b>{r.title}</b> },
        { h: 'Score', v: r => <span title={`Probabilité ${r.probability} × impact ${r.impact}`}>
          {r.score >= 15 && r.status === 'Ouvert' ? badge(`${r.score} critique`, 'bad') : r.score}</span> },
        { h: 'Réponse', v: r => r.response },
        { h: 'Propriétaire', v: r => r.ownerDisplayName || '—' },
        { h: 'Statut', v: r => badge(r.status, r.status === 'Ouvert' ? 'progress' : 'closed') },
      ]} />
  )
}

/** Registre des incidents projet (PRINCE2 « issues ») — distincts des incidents ITIL. */
export function Issues({ project, onChanged }) {
  const lead = useIsLead(project)
  return (
    <PartTable project={project} segment="issues" title="Registre des incidents projet" onChanged={onChanged}
      hint="Demandes de changement, hors-spécification, problèmes ou préoccupations du projet — à ne pas confondre avec les incidents de service. Clore exige une résolution."
      canWrite canDelete={lead} empty="Aucun incident projet."
      fields={[
        { key: 'title', label: 'Intitulé', required: true },
        { key: 'type', label: 'Type', type: 'select', options: ['Demande de changement', 'Hors spécification', 'Problème ou préoccupation'], default: 'Problème ou préoccupation' },
        { key: 'severity', label: 'Gravité', type: 'select', options: ['Faible', 'Moyenne', 'Élevée'], default: 'Moyenne' },
        { key: 'status', label: 'Statut', type: 'select', options: ['Ouvert', 'En traitement', 'Clos'], default: 'Ouvert' },
        { key: 'description', label: 'Description', type: 'textarea', rows: 2, full: true },
        { key: 'resolution', label: 'Résolution', type: 'textarea', rows: 2, full: true, create: false },
      ]}
      columns={[
        { h: 'Intitulé', v: r => <b>{r.title}</b> },
        { h: 'Type', v: r => r.type },
        { h: 'Gravité', v: r => r.severity },
        { h: 'Statut', v: r => badge(r.status, r.status === 'Clos' ? 'closed' : 'progress') },
      ]} />
  )
}
