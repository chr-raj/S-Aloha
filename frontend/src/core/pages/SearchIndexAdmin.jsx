import React, { useEffect, useState } from 'react'
import { api } from '../../api'
import { dateTimeFr } from '../fields'
import { ITEM_TYPES } from '../itemTypes'

/**
 * Administration → Index de recherche (RAG-08, Admin) : état de l'index et
 * du service d'embeddings, reconstruction complète.
 */
export default function SearchIndexAdmin() {
  const [s, setS] = useState(null)
  const [error, setError] = useState('')
  const [msg, setMsg] = useState('')
  const load = () => api.search.status().then(setS).catch(e => setError(e.message))
  useEffect(() => { load() }, [])
  const reindex = () => api.search.reindex().then(r => { setMsg(r.message); setTimeout(load, 3000) }).catch(e => setError(e.message))

  if (!s) return error ? <div className="error">{error}</div> : <p>Chargement…</p>
  const e = s.embeddings
  return (
    <>
      <h1 className="page-title">Index de recherche</h1>
      <p className="page-sub">Recherche hybride : plein texte PostgreSQL et similarité de sens ({e.model}), fusionnés par rang.</p>
      {error && <div className="error">{error}</div>}
      <div className={'notice ' + (e.reachable ? 'ok' : 'warn')}>
        <div>
          <b>{!e.enabled ? 'Service d\'embeddings non configuré' : e.reachable ? 'Service d\'embeddings joignable' : 'Service d\'embeddings injoignable'}</b>
          <div className="small">{e.reachable
            ? `${s.vectors} passage(s) vectorisé(s), comparés dans PostgreSQL (pgvector).`
            : 'La recherche fonctionne par les mots seulement ; les vecteurs manquants seront calculés au retour du service.'}</div>
        </div>
      </div>
      <table style={{ marginBottom: 16 }}>
        <thead><tr><th>Source</th><th>Enregistrements</th><th>Passages</th><th>Avec vecteur</th></tr></thead>
        <tbody>
          {s.sources.map(x => (
            <tr key={x.type}><td>{ITEM_TYPES[x.type]?.label ?? x.type}</td><td>{x.records}</td><td>{x.passages}</td><td>{x.withVector}</td></tr>))}
          {s.sources.length === 0 && <tr><td colSpan={4} className="muted">Index vide.</td></tr>}
        </tbody>
      </table>
      <p className="muted small" style={{ marginBottom: 12 }}>Dernière indexation : {dateTimeFr(s.lastIndexedAt)}. L'index se met à jour seul à chaque enregistrement ;
        la reconstruction complète sert après une restauration de base ou un changement de modèle.</p>
      <button className="btn ghost" onClick={reindex}>Reconstruire l'index</button>
      {msg && <p className="small" style={{ marginTop: 8 }}>{msg}</p>}
    </>
  )
}
