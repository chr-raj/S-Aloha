import React, { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api, getUser } from '../../../api'
import RecordDetail from '../../../core/components/RecordDetail'
import { hrefOf } from '../../../core/itemTypes'
import useManagerOn from '../../../core/useManagerOn'
import useRequestConfig from '../useRequestConfig'

/**
 * REQ-12 : une demande qui révèle un besoin non standard se requalifie en changement (ou en
 * incident) relié ; la demande est annulée avec ce motif. Demandeur ou gestionnaire, avant
 * le traitement. Gestionnaire du périmètre de la demande, comme l'API (PER-06).
 */
export function Requalify({ request }) {
  const [target, setTarget] = useState('change')
  const [reason, setReason] = useState('')
  const [error, setError] = useState('')
  const nav = useNavigate()
  const user = getUser()
  const manager = useManagerOn(request.perimeterId)
  const allowed = manager || user?.username?.toLowerCase() === request.createdBy?.toLowerCase()
  if (!allowed || !['Soumise', 'Approuvée'].includes(request.status)) return null
  const go = () => api.requests.requalify(request.id, { target, reason })
    .then(r => nav(hrefOf(r.target.type, r.target.id))).catch(e => setError(e.message))
  return (
    <section className="card" style={{ marginTop: 16 }} aria-label="Requalifier la demande">
      <h3 className="card-title">Besoin non standard</h3>
      <p className="muted small" style={{ marginBottom: 8 }}>
        La demande ne relève pas du catalogue : la requalifier crée un changement (ou un incident) relié, et annule la demande avec ce motif.
      </p>
      {error && <div className="error" role="alert">{error}</div>}
      <div className="toolbar">
        <select value={target} onChange={e => setTarget(e.target.value)} aria-label="Requalifier en" style={{ maxWidth: 200 }}>
          <option value="change">En changement</option>
          <option value="incident">En incident</option>
        </select>
        <input value={reason} onChange={e => setReason(e.target.value)} placeholder="Motif, ex. : travaux d’infrastructure hors catalogue"
          aria-label="Motif de la requalification" style={{ flex: 1, minWidth: 200 }} />
        <button className="btn ghost" onClick={go} disabled={!reason.trim()}>Requalifier</button>
      </div>
    </section>
  )
}

const TASK_STATUSES = ['À faire', 'En cours', 'Terminée', 'Annulée']

/** REQ-24, REQ-25 : tâches d'exécution de la demande ; elle n'est satisfaite que toutes terminées ou annulées. */
export function RequestTasks({ request, reload }) {
  const [tasks, setTasks] = useState(null)
  const [title, setTitle] = useState('')
  const [error, setError] = useState('')
  const load = () => api.requests.tasks(request.id).then(setTasks).catch(e => setError(e.message))
  useEffect(() => { load() }, [request.id, request.status])
  const editable = ['Approuvée', 'En cours'].includes(request.status)
  const change = (t, status) => api.requests.updateTask(request.id, t.id, { title: t.title, ownerType: t.ownerType, ownerId: t.ownerId, ownerDisplayName: t.ownerDisplayName, status })
    .then(() => { setError(''); load(); reload?.() }).catch(e => setError(e.message))
  const add = () => api.requests.addTask(request.id, { title }).then(() => { setTitle(''); setError(''); load() }).catch(e => setError(e.message))
  if (!tasks) return error ? <div className="error">{error}</div> : <p>Chargement…</p>
  const open = tasks.filter(t => !['Terminée', 'Annulée'].includes(t.status)).length
  return (
    <div className="card">
      <h3 className="card-title">Tâches d’exécution</h3>
      {error && <div className="error" role="alert">{error}</div>}
      <p className="muted small" style={{ marginBottom: 8 }}>
        {tasks.length === 0 ? 'Aucune tâche : celles du modèle sont créées à l’approbation.'
          : open === 0 ? 'Toutes les tâches sont terminées ou annulées : la demande peut être satisfaite.'
          : `${open} tâche(s) encore ouverte(s) : la demande ne peut pas encore être satisfaite.`}
      </p>
      {tasks.length > 0 && (
        <div className="table-scroll">
          <table className="compact">
            <thead><tr><th>Tâche</th><th>Responsable</th><th>Statut</th></tr></thead>
            <tbody>{tasks.map(t => (
              <tr key={t.id}>
                <td>{t.title}{!t.fromCatalog && <span className="muted small"> (ajoutée)</span>}</td>
                <td>{t.ownerDisplayName ?? '—'}</td>
                <td><select value={t.status} onChange={e => change(t, e.target.value)} disabled={!editable} aria-label={`Statut de « ${t.title} »`}>
                  {TASK_STATUSES.map(s => <option key={s}>{s}</option>)}</select></td>
              </tr>))}</tbody>
          </table>
        </div>)}
      {editable && <div className="toolbar" style={{ marginTop: 8 }}>
        <input value={title} onChange={e => setTitle(e.target.value)} placeholder="Nouvelle tâche" aria-label="Nouvelle tâche" style={{ flex: 1 }} />
        <button className="btn ghost small" onClick={add} disabled={!title.trim()}>Ajouter</button>
      </div>}
    </div>
  )
}

/** REQ-32 : enquête de satisfaction, facultative, une fois la demande close (demandeur, bénéficiaire). */
export function Satisfaction({ request, reload }) {
  const [score, setScore] = useState(request.satisfactionScore ?? 0)
  const [comment, setComment] = useState(request.satisfactionComment ?? '')
  const [error, setError] = useState('')
  const me = getUser()?.username?.toLowerCase()
  const mine = [request.createdBy, request.requestedFor].some(u => u?.toLowerCase() === me)
  if (request.status !== 'Close' || !mine) return null
  const send = () => api.requests.rate(request.id, { score, comment }).then(() => { setError(''); reload?.() }).catch(e => setError(e.message))
  return (
    <section className="card" style={{ marginTop: 16 }} aria-label="Satisfaction">
      <h3 className="card-title">Votre avis</h3>
      {error && <div className="error" role="alert">{error}</div>}
      <p className="muted small" style={{ marginBottom: 8 }}>Facultatif : votre note aide l’équipe à améliorer le service.</p>
      <div className="toolbar">
        {[1, 2, 3, 4, 5].map(n => (
          <button key={n} type="button" className={'btn small ' + (n <= score ? '' : 'ghost')} onClick={() => setScore(n)} aria-label={`Note ${n} sur 5`}>{n}</button>))}
        <input value={comment} onChange={e => setComment(e.target.value)} placeholder="Commentaire (facultatif)" aria-label="Commentaire" style={{ flex: 1, minWidth: 180 }} />
        <button className="btn" onClick={send} disabled={score === 0}>{request.satisfactionAt ? 'Mettre à jour' : 'Envoyer'}</button>
      </div>
    </section>
  )
}

export default function RequestDetail() {
  const config = useRequestConfig()
  return config
    ? <RecordDetail config={config} tab={{ label: 'Tâches', render: (r, reload) => <RequestTasks request={r} reload={reload} /> }}>
        {(r, reload) => <><Requalify request={r} /><Satisfaction request={r} reload={reload} /></>}
      </RecordDetail>
    : <p>Chargement…</p>
}
