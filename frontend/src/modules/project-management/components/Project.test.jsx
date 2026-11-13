import React from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { tabsFor } from '../pages/ProjectDetail'
import { Board } from './Agile'
import BurndownChart from './BurndownChart'
import { Phases } from './Registers'

const update = vi.fn(() => Promise.resolve({}))
vi.mock('../../../api', () => ({
  getUser: () => ({ username: 'tiana.ravelo', displayName: 'Tiana', role: 'User' }),
  api: { projects: { parts: (id, segment) => ({
    list: () => Promise.resolve(segment === 'sprints'
      ? [{ id: 1, name: 'S1', status: 'Actif' }]
      : [{ id: 7, title: 'Connexion SSO', type: 'User story', points: 5, rank: 1, sprintId: 1, status: 'À faire',
          description: '', acceptanceCriteria: '', assigneeId: null, assigneeDisplayName: null },
         { id: 8, title: 'Hors sprint', type: 'Tâche', points: 2, rank: 2, sprintId: null, status: 'À faire',
          description: '', acceptanceCriteria: '', assigneeId: null, assigneeDisplayName: null }]),
    update: (...a) => update(...a),
  }) } },
}))

afterEach(() => { cleanup(); update.mockClear() })

const labels = (components) => tabsFor({ components }).map(t => t.label)

describe('Onglets selon les composants (PRJ-03)', () => {
  it('chaque méthode a ses onglets', () => {
    expect(labels('backlog,sprints,board,burndown')).toEqual(['Backlog', 'Sprints et burndown', 'Tableau'])
    expect(labels('backlog,board')).toEqual(['Backlog', 'Tableau'])
    expect(labels('phases,milestones,deliverables')).toEqual(['Phases', 'Jalons', 'Livrables'])
    expect(labels('stages,business-case,risks,issues,tolerances')).toEqual(['Étapes', 'Business case', 'Risques', 'Incidents projet'])
  })
  it('un projet hybride combine les onglets de ses composants', () => {
    expect(labels('sprints,phases,risks,tolerances')).toEqual(['Phases', 'Sprints', 'Tolérances', 'Risques'])
  })
})

describe('Tableau', () => {
  it('Scrum : montre le sprint actif, et déplacer une carte renvoie l\'élément complet', async () => {
    render(<Board project={{ id: 3, status: 'En cours', components: 'backlog,sprints,board' }} />)
    expect(await screen.findByText('Tableau — S1')).toBeTruthy()
    expect(screen.queryByText('Hors sprint')).toBeNull()
    fireEvent.change(screen.getByLabelText('Statut de Connexion SSO'), { target: { value: 'En cours' } })
    await waitFor(() => expect(update).toHaveBeenCalled())
    const [id, dto] = update.mock.calls[0]
    expect(id).toBe(7)
    expect(dto).toMatchObject({ title: 'Connexion SSO', points: 5, sprintId: 1, status: 'En cours' })
  })
  it('projet clos : les cartes ne bougent plus', async () => {
    render(<Board project={{ id: 3, status: 'Clôturé', components: 'backlog,board' }} />)
    expect((await screen.findByLabelText('Statut de Connexion SSO')).disabled).toBe(true)
  })
})

describe('Étapes de gestion (PRJ-21)', () => {
  const project = (status) => ({ id: 3, status, components: 'stages', ownerType: 'User', ownerId: 'tiana.ravelo' })
  it('le chef de projet valide la fin de l\'étape courante', async () => {
    render(<Phases project={project('En cours')} stages />)
    expect(await screen.findAllByText('Valider la fin d\'étape')).toHaveLength(1)
  })
  it.each(['Clôturé', 'Abandonné'])('projet %s : la fin d\'étape ne se valide plus (PRJ-07)', async (status) => {
    render(<Phases project={project(status)} stages />)
    await screen.findByText('Étapes de gestion (2)')
    expect(screen.queryByText('Valider la fin d\'étape')).toBeNull()
  })
})

describe('Burndown (PRJ-13)', () => {
  const data = { total: 8, days: [
    { date: '2026-10-10T00:00:00Z', ideal: 8, remaining: 8 },
    { date: '2026-10-11T00:00:00Z', ideal: 4, remaining: 3 },
    { date: '2026-10-12T00:00:00Z', ideal: 0, remaining: null },
  ] }
  it('a une légende, une étiquette de fin et une vue tableau', () => {
    render(<BurndownChart data={data} sprintName="S1" />)
    expect(screen.getByText('Points restants')).toBeTruthy()
    expect(screen.getByText('Idéal')).toBeTruthy()
    expect(screen.getByText('3 pts')).toBeTruthy()          // étiquette directe au dernier jour connu
    fireEvent.click(screen.getByText('Voir les données'))
    expect(screen.getAllByRole('row')).toHaveLength(4)     // en-tête + 3 jours
    expect(screen.getByText('—')).toBeTruthy()             // jour à venir sans valeur réelle
  })
})
