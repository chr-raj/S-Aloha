import React, { useEffect, useState } from 'react'
import { api } from '../../../api'
import { toForm, toPayload } from '../../../core/components/RecordForm'
import RecordForm from '../../../core/components/RecordForm'
import { COMPONENTS, componentsOf, projectConfig } from '../config'
import useIsLead from '../rights'

/** Corps complet du projet, avec des changements : l'API remplace l'enregistrement. */
export const projectDto = (r, patch = {}) => ({
  ...projectConfig.keep(r), ...toPayload(projectConfig.fields, toForm(projectConfig.fields, r)), status: r.status, ...patch,
})

const TOLERANCE_TONE = { 'Dans la cible': 'done', 'À surveiller': 'wait', 'Hors tolérance': 'bad', 'Sans objet': 'closed' }

/**
 * Synthèse du projet (PRJ-30 à PRJ-33), sous ses informations, et choix des
 * composants d'un projet hybride (PRJ-03), par le chef de projet.
 */
export default function Overview({ project, onChanged }) {
  const [s, setS] = useState(null)
  const [msg, setMsg] = useState('')
  const comps = componentsOf(project)
  useEffect(() => { api.projects.summary(project.id).then(setS).catch(() => setS(null)) }, [project])
  const lead = useIsLead(project)
  const frozen = ['Clôturé', 'Abandonné'].includes(project.status)
  const toggle = (id) => {
    const next = comps.includes(id) ? comps.filter(c => c !== id) : [...comps, id]
    if (next.length === 0) { setMsg('Un projet hybride garde au moins un composant.'); return }
    api.projects.update(project.id, projectDto(project, { components: next })).then(() => { setMsg(''); onChanged() }).catch(e => setMsg(e.message))
  }
  return (
    <>
      {s && (
        <div className="card" style={{ marginTop: 16 }}>
          <h3 style={{ marginBottom: 12 }}>Synthèse</h3>
          <div className="kpi-row">
            <div className="kpi"><div className="l">Délai</div><span className={'badge t-' + TOLERANCE_TONE[s.tolerances.time]}>{s.tolerances.time}</span></div>
            <div className="kpi"><div className="l">Coût</div><span className={'badge t-' + TOLERANCE_TONE[s.tolerances.cost]}>{s.tolerances.cost}</span></div>
            {/* PRJ-22 : date de fin prévue et sa position par rapport à la tolérance de délai. */}
            {s.forecast && <div className="kpi" title={s.forecast.basis}>
              <div className="n">{s.forecast.date ? new Date(s.forecast.date).toLocaleDateString('fr-FR') : '—'}</div>
              <div className="l">Fin prévue ({s.forecast.method.toLowerCase()}){' '}
                {s.forecast.date && <span className={'badge t-' + TOLERANCE_TONE[s.forecast.tolerance]}>{s.forecast.tolerance}</span>}</div></div>}
            {(comps.includes('phases') || comps.includes('stages')) &&
              <div className="kpi"><div className="n">{s.phases.done}/{s.phases.total}</div><div className="l">{comps.includes('stages') ? 'Étapes' : 'Phases'} terminées{s.phases.current && ` · en cours : ${s.phases.current}`}</div></div>}
            {comps.includes('backlog') &&
              <div className="kpi"><div className="n">{s.workItems.pointsDone}/{s.workItems.points}</div><div className="l">Points terminés ({s.workItems.done}/{s.workItems.total} éléments)</div></div>}
            {comps.includes('deliverables') &&
              <div className="kpi"><div className="n">{s.deliverables.validated}/{s.deliverables.total}</div><div className="l">Livrables validés</div></div>}
            {comps.includes('milestones') &&
              <div className="kpi"><div className="n">{s.milestones.achieved}/{s.milestones.total}</div><div className="l">Jalons atteints{s.milestones.late > 0 && ` · ${s.milestones.late} en retard`}</div></div>}
            {comps.includes('risks') &&
              <div className="kpi"><div className="n">{s.risks.open}</div><div className="l">Risques ouverts{s.risks.critical > 0 && ` · ${s.risks.critical} critique(s)`}</div></div>}
            {comps.includes('issues') &&
              <div className="kpi"><div className="n">{s.openIssues}</div><div className="l">Incidents projet ouverts</div></div>}
          </div>
        </div>
      )}
      {project.method === 'Hybride' && (
        <div className="card" style={{ marginTop: 16 }}>
          <h3 style={{ marginBottom: 4 }}>Composants du projet hybride</h3>
          <p className="muted small" style={{ marginBottom: 10 }}>Chaque composant ajoute son onglet. Désactiver un composant masque ses données sans les supprimer.</p>
          {msg && <div className="error">{msg}</div>}
          <div className="component-grid">
            {COMPONENTS.map(c => (
              <label key={c.id} className="component-choice">
                <input type="checkbox" checked={comps.includes(c.id)} disabled={!lead || frozen} onChange={() => toggle(c.id)} />
                <span><b>{c.label}</b><small className="muted">{c.hint}</small></span>
              </label>))}
          </div>
        </div>
      )}
    </>
  )
}

const CASE_FIELDS = [
  { key: 'justification', label: 'Justification de l\'affaire', type: 'textarea', rows: 3, full: true,
    placeholder: 'Pourquoi ce projet ? Requise pour lancer un projet PRINCE2.' },
  { key: 'expectedBenefits', label: 'Bénéfices attendus', type: 'textarea', rows: 2, full: true },
  { key: 'expectedCosts', label: 'Coûts et délais', type: 'textarea', rows: 2, full: true },
  { key: 'majorRisks', label: 'Principaux risques', type: 'textarea', rows: 2, full: true },
]
const TOLERANCE_FIELDS = [
  { key: 'toleranceDays', label: 'Tolérance de délai (jours)', type: 'number' },
  { key: 'toleranceCostPercent', label: 'Tolérance de coût (%)', type: 'number' },
  { key: 'toleranceScope', label: 'Tolérance de périmètre', full: true },
  { key: 'toleranceQuality', label: 'Tolérance de qualité', full: true },
]

/** Business case (PRJ-06) et tolérances (PRJ-31, PRJ-32), modifiés par le chef de projet. */
export function BusinessCase({ project, onChanged }) {
  const comps = componentsOf(project)
  const fields = [...(comps.includes('business-case') ? CASE_FIELDS : []), ...(comps.includes('tolerances') ? TOLERANCE_FIELDS : [])]
  const [f, setF] = useState(() => toForm(fields, project))
  const [msg, setMsg] = useState(null)
  const lead = useIsLead(project)
  const readOnly = !lead || ['Clôturé', 'Abandonné'].includes(project.status)
  const save = () => api.projects.update(project.id, projectDto(project, toPayload(fields, f)))
    .then(() => { setMsg({ ok: true, text: 'Enregistré.' }); onChanged() }).catch(e => setMsg({ ok: false, text: e.message }))
  return (
    <div className="card">
      <h3 style={{ marginBottom: 4 }}>{comps.includes('business-case') ? 'Business case' : 'Tolérances'}</h3>
      <p className="muted small" style={{ marginBottom: 12 }}>
        {comps.includes('tolerances') && 'Au-delà des tolérances de délai et de coût, la synthèse signale le projet « Hors tolérance » : le chef de projet remonte une exception. '}
        {readOnly && 'Lecture seule : réservé au chef de projet et aux gestionnaires.'}</p>
      {msg && <div className={msg.ok ? 'notice ok' : 'error'}>{msg.text}</div>}
      <RecordForm fields={fields} form={f} onChange={patch => setF(prev => ({ ...prev, ...patch }))} disabled={readOnly} />
      {!readOnly && <button className="btn" onClick={save}>Enregistrer</button>}
    </div>
  )
}
