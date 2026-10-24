import { replica, text } from '../lib/script.mjs'

const t = text('t')
const ins = (doc, at, value, attributes) => ({ step: 'text.insert', doc, target: t, at, value, ...(attributes ? { attributes } : {}) })
const sync = (from, to) => ({ step: 'sync', from, to })

export default [
  {
    area: 'text',
    name: 'insert-delete-sync',
    covers: ['T-1', 'T-2', 'ENC-21', 'ENC-28'],
    observe: ['structs', 'ds'],
    truncation: [{ replica: 'a', encoding: 'v1' }, { replica: 'b', encoding: 'v2' }],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        ins('a', 0, 'hello'),
        sync('a', 'b'),
        ins('b', 5, ' world'),
        { step: 'text.delete', doc: 'a', target: t, at: 1, len: 2 },
        sync('a', 'b'),
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'text',
    name: 'concurrent-same-position',
    covers: ['T-1', 'A-18'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2), replica('c', 3)],
      steps: [
        ins('a', 0, 'base'),
        sync('a', 'b'),
        sync('a', 'c'),
        ins('a', 2, 'AA'),
        ins('b', 2, 'BB'),
        ins('c', 2, 'CC'),
        { step: 'text.delete', doc: 'b', target: t, at: 0, len: 3 },
        sync('a', 'b'), sync('b', 'a'), sync('a', 'c'), sync('c', 'a'), sync('c', 'b'), sync('b', 'c')
      ]
    }
  },
  {
    area: 'text',
    name: 'formatting',
    covers: ['F-1', 'F-4', 'F-6', 'D-1'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 10), replica('b', 11)],
      steps: [
        ins('a', 0, 'bold plain', { bold: true }),
        { step: 'text.format', doc: 'a', target: t, at: 5, len: 5, attributes: { bold: null } },
        { step: 'text.format', doc: 'a', target: t, at: 0, len: 2, attributes: { italic: true, size: 0, flag: false, empty: '' } },
        { step: 'text.insertEmbed', doc: 'a', target: t, at: 4, value: { image: 'x.png' } },
        sync('a', 'b'),
        { step: 'text.format', doc: 'b', target: t, at: 1, len: 6, attributes: { color: 'red' } },
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'text',
    name: 'surrogates-and-embeds',
    covers: ['T-5', 'E-1', 'E-2'],
    script: {
      replicas: [replica('a', 20), replica('b', 21)],
      steps: [
        ins('a', 0, 'a\u{1F600}b'),
        ins('a', 2, '\ud83d'),
        { step: 'text.insertEmbed', doc: 'a', target: t, at: 0, value: new Map([['10', 1], ['2', 2.5], ['b', null], ['a', 'x']]) },
        { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: [null, true, 'z', { deep: [1] }] },
        sync('a', 'b')
      ]
    }
  },
  {
    area: 'text',
    name: 'observe-events',
    covers: ['Y-2', 'TX-1'],
    script: {
      replicas: [replica('a', 30), replica('b', 31)],
      steps: [
        { step: 'observe', doc: 'a', target: t, name: 'ta' },
        { step: 'observe', doc: 'b', target: t, name: 'tb' },
        {
          step: 'transact',
          doc: 'a',
          origin: 'local',
          steps: [ins('a', 0, 'abc'), { step: 'text.delete', doc: 'a', target: t, at: 1, len: 1 }, ins('a', 1, 'XY', { bold: true })]
        },
        sync('a', 'b'),
        { step: 'tick', ms: 500 },
        { step: 'text.format', doc: 'b', target: t, at: 0, len: 2, attributes: { italic: true } },
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'text',
    name: 'apply-delta',
    covers: ['T-6', 'T-8'],
    script: {
      replicas: [replica('a', 40)],
      steps: [
        { step: 'text.applyDelta', doc: 'a', target: t, delta: [{ insert: 'hello' }, { insert: '', attributes: { x: 1 } }, { insert: ' there', attributes: { bold: true } }] },
        { step: 'text.applyDelta', doc: 'a', target: t, delta: [{ retain: 2 }, { delete: 2 }, { retain: 3, attributes: { bold: null } }] }
      ]
    }
  }
]
