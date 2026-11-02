import { expect, it } from 'vitest'
import { fmt, toCsv } from './exportCsv'

it('neutralise une cellule texte qui commence par un signe de formule, pas un nombre', () => {
  const csv = toCsv([{ title: 't', header: ['nom', 'valeur'], rows: [
    ['=1+1', -3.5], ['@SUM(1)', fmt(-3.5)], ['+SUM(1)', '-x'], ['\tonglet', 'ordinaire'], ['-', fmt(-1234.5)],
  ] }])
  expect(csv.split('\r\n').slice(2)).toEqual([
    "'=1+1;-3,5", "'@SUM(1);-3,5", "'+SUM(1);'-x", "'\tonglet;ordinaire", "'-;" + fmt(-1234.5),
  ])
})

it('met entre guillemets une cellule qui contient un retour chariot seul', () => {
  // Le tableur prend un retour chariot seul pour une fin de ligne : sans guillemets, la suite
  // ouvrirait une nouvelle ligne, et un signe de formule y serait évalué.
  const csv = toCsv([{ title: 't', header: ['nom', 'valeur'], rows: [['a\r=1+1', 3], ['a\r\nb', 1]] }])
  expect(csv.split('\r\n=')).toHaveLength(1)
  expect(csv).toContain('"a\r=1+1";3')
  expect(csv).toContain('"a\r\nb";1')
})
