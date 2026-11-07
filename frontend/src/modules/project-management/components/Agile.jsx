import React, { useEffect, useState } from 'react'
import { api } from '../../../api'
import { dateFr } from '../../../core/fields'
import PartTable from './PartTable'
import BurndownChart from './BurndownChart'
import { componentsOf } from '../config'
import useIsLead from '../rights'

export const STATUSES = ['À faire', 'En cours', 'En revue', 'Terminé']
const TYPES = ['User story', 'Tâche', 'Bogue']

/** Corps complet d'un élément de travail, avec des changements (l'API remplace tout). */
const itemDto = (w, patch = {}) => ({
  type: w.type, title: w.title, description: w.description, acceptanceCriteria: w.acceptanceCriteria,
  points: w.points, rank: w.rank, sprintId: w.sprintId, status: w.status,
  assigneeId: w.assigneeId, assigneeDisplayName: w.assigneeDisplayName, ...patch,
})

const useSprints = (project) => {
  const [sprints, setSprints] = useState([])
  const enabled = componentsOf(project).includes('sprints')
  useEffect(() => { if (enabled) api.projects.parts(project.id, 'sprints').list().then(setSprints).catch(() => setSprints([])) }, [project.id, enabled])
  return enabled ? sprints : null
}

/** Backlog (PRJ-14) : éléments priorisés, tout rôle les crée ; ↑ ↓ change la priorité. */
export function Backlog({ project, onChanged }) {
  const sprints = useSprints(project)
  const lead = useIsLead(project)
  const client = api.projects.parts(project.id, 'workitems')
  const sprintName = (id) => sprints?.find(s => s.id === id)?.name
  const move = (row, rows, delta, reload) => {
    const i = rows.findIndex(r => r.id === row.id), j = i + delta
    if (j < 0 || j >= rows.length) return
    const other = rows[j]
    Promise.all([client.update(row.id, itemDto(row, { rank: other.rank })), client.update(other.id, itemDto(other, { rank: row.rank }))])
      .then(reload)
  }
  return (
    <PartTable project={project} segment="workitems" title="Backlog" onChanged={onChanged}
      hint="User stories, tâches et bogues, du plus prioritaire au moins prioritaire. Tout membre de l'équipe peut en ajouter."
      canWrite canDelete={lead} empty="Backlog vide : ajoutez une première user story."
      actions={(row, reload, rows) => <>
        <button className="btn ghost small" onClick={() => move(row, rows, -1, reload)} aria-label={`Monter ${row.title}`}>↑</button>{' '}
        <button className="btn ghost small" onClick={() => move(row, rows, 1, reload)} aria-label={`Descendre ${row.title}`}>↓</button>{' '}
      </>}
      fields={[
        { key: 'title', label: 'Titre', required: true, full: true, placeholder: 'En tant que…, je veux…, afin de…' },
        { key: 'type', label: 'Type', type: 'select', options: TYPES, default: 'User story' },
        { key: 'points', label: 'Points', type: 'number' },
        { key: 'status', label: 'Statut', type: 'select', options: STATUSES, default: 'À faire' },
        ...(sprints ? [{ key: 'sprintId', label: 'Sprint', type: 'select', numeric: true,
          options: [{ value: '', label: '— Backlog —' }, ...sprints.filter(s => s.status !== 'Clos').map(s => ({ value: s.id, label: s.name }))] }] : []),
        { key: 'assigneeId', nameKey: 'assigneeDisplayName', label: 'Assigné à', type: 'person' },
        { key: 'acceptanceCriteria', label: 'Critères d\'acceptation', type: 'textarea', rows: 2, full: true },
        { key: 'description', label: 'Description', type: 'textarea', rows: 2, full: true },
      ]}
      columns={[
        { h: 'Élément', v: r => <><b>{r.title}</b><div className="muted small">{r.type}</div></> },
        { h: 'Points', v: r => r.points ?? '—' },
        ...(sprints ? [{ h: 'Sprint', v: r => sprintName(r.sprintId) ?? '—' }] : []),
        { h: 'Statut', v: r => r.status },
        { h: 'Assigné', v: r => r.assigneeDisplayName || '—' },
      ]} />
  )
}

/**
 * Tableau (PRJ-14) : quatre colonnes, glisser-déposer, et une liste de statut
 * sur chaque carte pour le clavier. Scrum : le sprint actif ; Kanban : tout le flux.
 */
export function Board({ project, onChanged }) {
  const sprints = useSprints(project)
  const [items, setItems] = useState(null)
  const [over, setOver] = useState(null)
  const [error, setError] = useState('')
  const client = api.projects.parts(project.id, 'workitems')
  const load = () => client.list().then(setItems).catch(e => setError(e.message))
  useEffect(() => { load() }, [project.id])
  const frozen = ['Clôturé', 'Abandonné'].includes(project.status)
  if (!items) return error ? <div className="error">{error}</div> : <p>Chargement…</p>

  const active = sprints?.find(s => s.status === 'Actif')
  const shown = sprints ? items.filter(w => active && w.sprintId === active.id) : items
  const setStatus = (w, status) => w.status !== status
    && client.update(w.id, itemDto(w, { status })).then(() => { load(); onChanged?.() }).catch(e => setError(e.message))

  return (
    <div className="card">
      <h3 style={{ marginBottom: 4 }}>{sprints ? (active ? `Tableau — ${active.name}` : 'Tableau') : 'Tableau Kanban'}</h3>
      <p className="muted small" style={{ marginBottom: 12 }}>
        {sprints && !active ? 'Aucun sprint actif : activez un sprint pour alimenter le tableau.'
          : 'Faites glisser une carte, ou changez son statut dans sa liste.'}</p>
      {error && <div className="error">{error}</div>}
      <div className="board">
        {STATUSES.map(col => (
          <div key={col} className={'board-col' + (over === col ? ' over' : '')}
            onDragOver={e => { if (!frozen) { e.preventDefault(); setOver(col) } }} onDragLeave={() => setOver(null)}
            onDrop={e => { setOver(null); const w = shown.find(x => String(x.id) === e.dataTransfer.getData('text/plain')); if (w) setStatus(w, col) }}>
            <h4>{col} ({shown.filter(w => w.status === col).length})</h4>
            {shown.filter(w => w.status === col).map(w => (
              <div key={w.id} className="board-card" draggable={!frozen}
                onDragStart={e => e.dataTransfer.setData('text/plain', String(w.id))}>
                <b>{w.title}</b>
                <div className="muted small">{w.type}{w.points !== null && ` · ${w.points} pts`}{w.assigneeDisplayName && ` · ${w.assigneeDisplayName}`}</div>
                <select value={w.status} disabled={frozen} aria-label={`Statut de ${w.title}`} onChange={e => setStatus(w, e.target.value)}>
                  {STATUSES.map(s => <option key={s}>{s}</option>)}
                </select>
              </div>))}
          </div>))}
      </div>
    </div>
  )
}

/** Sprints (PRJ-12) et burndown (PRJ-13). */
export function Sprints({ project, onChanged }) {
  const lead = useIsLead(project)
  const hasBurndown = componentsOf(project).includes('burndown')
  const [selected, setSelected] = useState(null)
  const [burn, setBurn] = useState(null)
  const [version, setVersion] = useState(0)
  useEffect(() => {
    if (!selected || !hasBurndown) { setBurn(null); return }
    api.projects.burndown(project.id, selected.id).then(setBurn).catch(() => setBurn(null))
  }, [selected, version])
  return (
    <>
      <PartTable project={project} segment="sprints" title="Sprints" onChanged={() => { setVersion(v => v + 1); onChanged?.() }}
        hint="Un seul sprint actif à la fois. À la clôture, ce qui n'est pas terminé retourne au backlog ; un sprint clos ne se rouvre pas."
        canWrite={lead} canDelete={lead} empty="Aucun sprint."
        actions={(row) => hasBurndown && row.startDate && row.endDate &&
          <button className="btn ghost small" onClick={() => setSelected(row)}>Burndown</button>}
        fields={[
          { key: 'name', label: 'Nom', required: true },
          { key: 'status', label: 'Statut', type: 'select', options: ['Planifié', 'Actif', 'Clos'], default: 'Planifié' },
          { key: 'startDate', label: 'Début', type: 'date' },
          { key: 'endDate', label: 'Fin', type: 'date' },
          { key: 'goal', label: 'Objectif du sprint', full: true },
        ]}
        columns={[
          { h: 'Sprint', v: r => <><b>{r.name}</b>{r.goal && <div className="muted small">{r.goal}</div>}</> },
          { h: 'Dates', v: r => `${dateFr(r.startDate)} → ${dateFr(r.endDate)}` },
          { h: 'Statut', v: r => <span className={'badge t-' + ({ Planifié: 'new', Actif: 'progress', Clos: 'closed' })[r.status]}>{r.status}</span> },
        ]} />
      {selected && burn && (
        <div className="card">
          <h3 style={{ marginBottom: 10 }}>Burndown — {selected.name}</h3>
          <BurndownChart data={burn} sprintName={selected.name} />
        </div>
      )}
    </>
  )
}
