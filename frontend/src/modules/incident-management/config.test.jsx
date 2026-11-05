import { describe, expect, it, vi } from 'vitest'

vi.mock('../../api', () => ({ api: {} }))
import { incidentConfig } from './config'
import { toForm, toPayload } from '../../core/components/RecordForm'

// Corps envoyé par RecordDetail à l'enregistrement de la fiche (keep, puis champs du formulaire).
const saved = (record) => ({ ...(incidentConfig.keep?.(record) ?? {}), ...toPayload(incidentConfig.fields, toForm(incidentConfig.fields, record)) })

describe('Fiche incident enregistrée (INC-27)', () => {
  it('renvoie le début constaté, absent du formulaire, sans l’effacer', () => {
    const record = { id: 3, title: 'Supervision : lien WAN coupé', impact: 'Moyen', urgency: 'Moyenne', channel: 'Supervision',
      occurredAt: '2026-11-03T06:12:00Z', status: 'Nouveau' }
    expect(saved(record).occurredAt).toBe('2026-11-03T06:12:00Z')
  })

  it('renvoie un début constaté absent tel quel (null)', () => {
    expect(saved({ id: 4, title: 'Sans début', impact: 'Faible', urgency: 'Faible', occurredAt: null, status: 'Nouveau' }).occurredAt).toBeNull()
  })
})
