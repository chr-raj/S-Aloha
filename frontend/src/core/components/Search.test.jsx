import React from 'react'
import { afterEach, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import KnowledgeSearch from './KnowledgeSearch'
import SimilarCases from './SimilarCases'
import SearchIndexAdmin from '../pages/SearchIndexAdmin'

const query = vi.fn(() => Promise.resolve([
  { type: 'article', id: 3, reference: 'KB-2026-0003', title: 'Lenteurs de la messagerie', status: 'Publié',
    snippet: 'La purge hebdomadaire fragmente l\'index.', score: 0.03, lexical: false, semantic: true },
  { type: 'problem', id: 7, reference: 'PRB-2026-0007', title: 'Purge Exchange', status: 'Erreur connue',
    snippet: 'Contournement : relancer la purge.', score: 0.02, lexical: true, semantic: true },
]))
const status = vi.fn(() => Promise.resolve({ embeddings: { enabled: true, model: 'bge-m3', reachable: false }, vectors: 0,
  sources: [{ type: 'article', records: 2, passages: 3, withVector: 0 }], lastIndexedAt: null }))
vi.mock('../../api', () => ({ api: { search: {
  query: (...a) => query(...a),
  similar: () => Promise.resolve([]),
  status: (...a) => status(...a),
  reindex: vi.fn(),
} } }))

afterEach(cleanup)

it('« Chercher d\'abord » affiche les résultats et comment ils ont été trouvés', async () => {
  render(<MemoryRouter><KnowledgeSearch /></MemoryRouter>)
  fireEvent.change(screen.getByLabelText('Rechercher dans les connaissances'), { target: { value: 'courriel lent' } })
  fireEvent.click(screen.getByText('Rechercher'))
  expect(query).toHaveBeenCalledWith('courriel lent')
  expect((await screen.findByText('KB-2026-0003')).closest('a').getAttribute('href')).toBe('/knowledge/3')
  expect(screen.getByText('sens')).toBeTruthy()          // trouvé par le vecteur seul
  expect(screen.getByText('mots et sens')).toBeTruthy()
})

it('« Cas similaires » le dit quand il n\'y en a pas', async () => {
  render(<MemoryRouter><SimilarCases type="incident" id={4} /></MemoryRouter>)
  expect(await screen.findByText(/Aucun cas similaire/)).toBeTruthy()
})

it('l\'administration signale un service d\'embeddings injoignable', async () => {
  render(<MemoryRouter><SearchIndexAdmin /></MemoryRouter>)
  expect(await screen.findByText('Service d\'embeddings injoignable')).toBeTruthy()
  expect(screen.getByText(/par les mots seulement/)).toBeTruthy()
})

it('l\'administration dit que les vecteurs sont comparés dans PostgreSQL (ADR-0022)', async () => {
  status.mockResolvedValueOnce({ embeddings: { enabled: true, model: 'bge-m3', reachable: true }, vectors: 3,
    sources: [{ type: 'article', records: 2, passages: 3, withVector: 3 }], lastIndexedAt: null })
  render(<MemoryRouter><SearchIndexAdmin /></MemoryRouter>)
  expect(await screen.findByText('3 passage(s) vectorisé(s), comparés dans PostgreSQL (pgvector).')).toBeTruthy()
})
