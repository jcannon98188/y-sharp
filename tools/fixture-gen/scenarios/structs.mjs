// Struct model, integration, pending retries, GC and merge fixtures (TASK-2.6; STR-* and GC-* in docs/inventory).
// Every fixture records the `structs` and `ds` dumps so a port is compared struct by struct, not only by document text.
import { array, map, replica, text } from '../lib/script.mjs'
import { updatesOf } from '../lib/authoring.mjs'
import { Y } from '../lib/oracle.mjs'

const t = text('t')
const arr = array('arr')
const m = map('m')
const sync = (from, to) => ({ step: 'sync', from, to })
const ins = (doc, at, value) => ({ step: 'text.insert', doc, target: t, at, value })
const del = (doc, at, len) => ({ step: 'text.delete', doc, target: t, at, len })
const apply = (doc, bytes, encoding) => ({ step: 'applyUpdate', doc, bytes, ...(encoding ? { encoding } : {}) })
const watch = doc => ({ step: 'observe', doc, target: t, name: `watch-${doc}` })
const both = (a, b) => [sync(a, b), sync(b, a)]
const all3 = (a, b, c) => [sync(a, b), sync(b, a), sync(a, c), sync(c, a), sync(b, c), sync(c, b)]

/** Last update event a replica emitted while running `script` (its own local change when it is the last step). */
const lastUpdate = (script, name, encoding = 'v1') => {
  const list = updatesOf(script, name, encoding)
  return list[list.length - 1]
}

// ---- three authors a(1), b(2), c(3): each update depends on the previous one (a <- b <- c).
const chainScript = {
  replicas: [replica('a', 1), replica('b', 2), replica('c', 3)],
  steps: [
    ins('a', 0, 'A'), sync('a', 'b'),
    ins('b', 1, 'B'), sync('b', 'c'),
    ins('c', 2, 'C')
  ]
}
const chainUpdates = [lastUpdate({ replicas: chainScript.replicas, steps: chainScript.steps.slice(0, 1) }, 'a'), (() => {
  const s = { replicas: chainScript.replicas, steps: chainScript.steps.slice(0, 3) }
  return lastUpdate(s, 'b')
})(), lastUpdate(chainScript, 'c')]
const [u1, u2, u3] = chainUpdates

// ---- b's item sits between a's and c's items, so it depends on two different clients.
const twoDepScript = {
  replicas: [replica('a', 1), replica('b', 2), replica('c', 3)],
  steps: [
    ins('a', 0, 'A'), ins('c', 0, 'C'),
    sync('a', 'b'), sync('c', 'b'),
    ins('b', 1, 'B')
  ]
}
const t1 = lastUpdate({ replicas: twoDepScript.replicas, steps: twoDepScript.steps.slice(0, 1) }, 'a')
const t3 = lastUpdate({ replicas: twoDepScript.replicas, steps: twoDepScript.steps.slice(0, 2) }, 'c')
const t2 = lastUpdate(twoDepScript, 'b')

// ---- one author, three transactions: insert, insert, then a delete-only update.
const authorScript = {
  replicas: [replica('a', 1)],
  steps: [ins('a', 0, 'ab'), ins('a', 2, 'cd'), del('a', 1, 2)]
}
const author = n => lastUpdate({ replicas: authorScript.replicas, steps: authorScript.steps.slice(0, n) }, 'a')
const [w1, w2, w3] = [author(1), author(2), author(3)]

// ---- gap source: three separate inserts by one client; merging 1 and 3 must write a Skip for 2.
const gapScript = {
  replicas: [replica('a', 1)],
  steps: [ins('a', 0, 'a'), ins('a', 1, 'b'), ins('a', 2, 'c')]
}
const gap = (n, enc = 'v1') => lastUpdate({ replicas: gapScript.replicas, steps: gapScript.steps.slice(0, n) }, 'a', enc)
const [g1, g3] = [gap(1), gap(3)]
const [g1v2, g3v2] = [gap(1, 'v2'), gap(3, 'v2')]

// ---- mid-item diff source.
const midScript = { replicas: [replica('a', 1)], steps: [ins('a', 0, 'he'), ins('a', 2, 'llo')] }
const m1 = lastUpdate({ replicas: midScript.replicas, steps: midScript.steps.slice(0, 1) }, 'a')

// ---- raw documents the step vocabulary cannot author (a subdocument inside a map).
const subdocBytes = () => {
  const d = new Y.Doc()
  d.clientID = 7
  const sub = new Y.Doc({ guid: 'sub-guid-1', autoLoad: true })
  d.getMap('m').set('sub', sub)
  d.getMap('m').set('plain', 1)
  return Y.encodeStateAsUpdate(d)
}

export default [
  {
    area: 'structs',
    name: 'golden-single-insert',
    covers: ['STR-01', 'STR-02', 'STR-04', 'STR-08', 'STR-29', 'STR-56', 'STR-66', 'STR-67'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [ins('a', 0, 'abc'), sync('a', 'b')]
    }
  },
  {
    area: 'structs',
    name: 'split-delete-merge',
    covers: ['STR-10', 'STR-11', 'STR-26', 'STR-28', 'STR-45', 'STR-48', 'STR-49', 'STR-51', 'STR-60', 'STR-65', 'GC-08'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        ins('a', 0, 'hello'), sync('a', 'b'),
        del('b', 1, 2), sync('b', 'a'),
        del('a', 0, 1), del('a', 0, 1), sync('a', 'b'),
        ins('b', 1, 'XY'), sync('b', 'a')
      ]
    }
  },
  {
    area: 'structs',
    name: 'conflict-same-origin-low-client-left',
    covers: ['STR-18', 'STR-19', 'STR-20'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [ins('a', 0, 'ab'), sync('a', 'b'), ins('a', 1, 'A'), ins('b', 1, 'B'), ...both('a', 'b')]
    }
  },
  {
    area: 'structs',
    name: 'conflict-same-origin-high-client-first-writer',
    covers: ['STR-18', 'STR-19', 'STR-20'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 2), replica('b', 1)],
      steps: [ins('a', 0, 'ab'), sync('a', 'b'), ins('a', 1, 'A'), ins('b', 1, 'B'), ...both('b', 'a')]
    }
  },
  {
    area: 'structs',
    name: 'conflict-null-origin',
    covers: ['STR-18', 'STR-19', 'STR-20', 'STR-21'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 5), replica('b', 3), replica('c', 4)],
      steps: [
        { step: 'array.unshift', doc: 'a', target: arr, values: ['a1'] },
        { step: 'array.unshift', doc: 'b', target: arr, values: ['b1'] },
        { step: 'array.unshift', doc: 'c', target: arr, values: ['c1'] },
        ...all3('a', 'b', 'c')
      ]
    }
  },
  {
    area: 'structs',
    name: 'conflict-interleave-three-clients',
    covers: ['STR-18', 'STR-19', 'STR-20', 'STR-23'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2), replica('c', 3)],
      steps: [
        ins('a', 0, 'ab'), sync('a', 'b'), sync('a', 'c'),
        ins('a', 1, 'AA'), ins('b', 1, 'BB'), ins('c', 1, 'CC'),
        ins('b', 3, 'x'), ins('c', 2, 'y'),
        ...all3('a', 'b', 'c'), ...all3('a', 'b', 'c')
      ]
    }
  },
  {
    area: 'structs',
    name: 'map-key-overwrite-race',
    covers: ['STR-21', 'STR-22', 'STR-23', 'STR-25'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 'a1' },
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 'a2' },
        { step: 'map.set', doc: 'b', target: m, key: 'k', value: 'b1' },
        { step: 'map.set', doc: 'b', target: m, key: 'other', value: 1 },
        ...both('a', 'b'), ...both('a', 'b'),
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 'a3' },
        { step: 'map.delete', doc: 'b', target: m, key: 'other' },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'delete-gc-on',
    covers: ['STR-26', 'STR-27', 'STR-58', 'STR-59', 'GC-01', 'GC-02', 'GC-03', 'GC-04', 'GC-06', 'GC-08'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        ins('a', 0, 'hello world'), sync('a', 'b'),
        del('b', 0, 6), sync('b', 'a'),
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 1 },
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 2 },
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 3 },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'delete-gc-off',
    covers: ['STR-26', 'STR-60', 'GC-01', 'GC-06', 'GC-08'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, false), replica('b', 2, false)],
      steps: [
        ins('a', 0, 'hello world'), sync('a', 'b'),
        del('b', 0, 6), sync('b', 'a'),
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 1 },
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 2 },
        { step: 'map.set', doc: 'a', target: m, key: 'k', value: 3 },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'gc-mixed-replicas',
    covers: ['GC-01', 'GC-06', 'GC-10'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, true), replica('b', 2, false)],
      steps: [ins('a', 0, 'abcdef'), sync('a', 'b'), del('a', 1, 3), ...both('a', 'b')]
    }
  },
  {
    area: 'structs',
    name: 'nested-type-delete',
    covers: ['STR-27', 'STR-42', 'STR-44', 'GC-04'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'text', init: 'hello' },
        { step: 'array.insertType', doc: 'a', target: arr, at: 1, kind: 'map', init: { k: 'v', n: 1 } },
        { step: 'array.insertType', doc: 'a', target: arr, at: 2, kind: 'array', init: [1, 2, 3] },
        sync('a', 'b'),
        { step: 'array.delete', doc: 'a', target: arr, at: 0, len: 2 },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'nested-type-delete-gc-off',
    covers: ['STR-42', 'GC-04'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, false), replica('b', 2, false)],
      steps: [
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'map', init: { k: 'v' } },
        sync('a', 'b'),
        { step: 'array.delete', doc: 'a', target: arr, at: 0 },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'orphan-child-of-collected-type',
    covers: ['STR-15', 'STR-24', 'GC-07'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'map' },
        sync('a', 'b'),
        { step: 'map.set', doc: 'b', target: [arr, 0], key: 'k', value: 'late' },
        { step: 'array.delete', doc: 'a', target: arr, at: 0 },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'orphan-child-gc-off-keeps-hidden',
    covers: ['STR-15', 'GC-07'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, false), replica('b', 2)],
      steps: [
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'map' },
        sync('a', 'b'),
        { step: 'map.set', doc: 'b', target: [arr, 0], key: 'k', value: 'late' },
        { step: 'array.delete', doc: 'a', target: arr, at: 0 },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'pending-chain-permutations',
    covers: ['STR-13', 'STR-61', 'STR-62', 'STR-63', 'STR-64', 'STR-67'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('d', 10), replica('e', 11), replica('f', 12)],
      steps: [
        watch('d'), watch('e'), watch('f'),
        apply('d', u3), apply('d', u2), apply('d', u1),
        apply('e', u2), apply('e', u3), apply('e', u1),
        apply('f', u3), apply('f', u1), apply('f', u2)
      ]
    }
  },
  {
    area: 'structs',
    name: 'pending-never-completed',
    covers: ['STR-13', 'STR-61', 'STR-62', 'STR-63', 'STR-64', 'STR-67'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('d', 10), replica('e', 11)],
      steps: [
        watch('d'), watch('e'),apply('d', u3), apply('d', u2), apply('e', u3), sync('d', 'e')]
    }
  },
  {
    area: 'structs',
    name: 'pending-two-missing-clients',
    covers: ['STR-13', 'STR-62', 'STR-63', 'STR-64'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('d', 10), replica('e', 11)],
      steps: [
        watch('d'), watch('e'),
        apply('d', t2), apply('d', t1), apply('d', t3),
        apply('e', t2), apply('e', t3), apply('e', t1)
      ]
    }
  },
  {
    area: 'structs',
    name: 'pending-delete-set-before-structs',
    covers: ['STR-64', 'STR-65'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('d', 10), replica('e', 11)],
      steps: [
        watch('d'), watch('e'),
        apply('d', w3), apply('d', w2), apply('d', w1),
        apply('e', w1), apply('e', w3), apply('e', w2)
      ]
    }
  },
  {
    area: 'structs',
    name: 'skip-in-merged-update',
    covers: ['STR-37', 'STR-38', 'STR-39'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('d', 10), replica('e', 11)],
      steps: [
        watch('d'), watch('e'),
        { step: 'mergeUpdates', name: 'gapped-v1', updates: [g1, g3] },
        { step: 'mergeUpdates', name: 'gapped-v2', updates: [g1v2, g3v2], encoding: 'v2' },
        apply('d', g1), apply('d', g3),
        apply('e', g1v2, 'v2'), apply('e', g3v2, 'v2')
      ]
    }
  },
  {
    area: 'structs',
    name: 'diff-starts-mid-item',
    covers: ['STR-17', 'STR-29', 'STR-34', 'STR-66'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2), replica('c', 3, false)],
      steps: [
        ...midScript.steps.map(s => ({ ...s })),
        apply('b', m1),
        sync('a', 'b'),
        del('a', 0, 5),
        apply('c', m1), sync('a', 'c')
      ]
    }
  },
  {
    area: 'structs',
    name: 'content-kinds',
    covers: ['STR-30', 'STR-40', 'STR-42', 'STR-44'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'bold plain', attributes: { bold: true } },
        { step: 'text.format', doc: 'a', target: t, at: 5, len: 5, attributes: { bold: null } },
        { step: 'array.push', doc: 'a', target: arr, values: [1, 'two', true, null, { k: [1, 2] }, new Uint8Array([1, 2, 3])] },
        { step: 'array.insertType', doc: 'a', target: arr, at: 3, kind: 'text', init: 'nested' },
        { step: 'map.set', doc: 'a', target: m, key: 'bytes', value: new Uint8Array([9, 8]) },
        { step: 'map.set', doc: 'a', target: m, key: 'any', value: { x: 1 } },
        sync('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'surrogate-split-gc-off',
    covers: ['STR-41', 'STR-11'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, false), replica('b', 2, false)],
      steps: [ins('a', 0, 'a\u{1F600}b'), sync('a', 'b'), del('b', 2, 1), sync('b', 'a')]
    }
  },
  {
    area: 'structs',
    name: 'surrogate-split-at-boundary',
    covers: ['STR-41'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, false), replica('b', 2, false)],
      steps: [ins('a', 0, 'a\u{1F600}b'), sync('a', 'b'), del('b', 1, 2), sync('b', 'a')]
    }
  },
  {
    area: 'structs',
    name: 'subdoc-content',
    covers: ['STR-43', 'STR-44'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [apply('a', subdocBytes()), sync('a', 'b')]
    }
  },
  {
    area: 'structs',
    name: 'update-events-and-empty-transactions',
    covers: ['STR-53', 'STR-54', 'STR-57'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1)],
      steps: [
        { step: 'transact', doc: 'a', steps: [] },
        ins('a', 0, 'abc'),
        { step: 'transact', doc: 'a', steps: [del('a', 0, 1), { step: 'transact', doc: 'a', steps: [ins('a', 2, 'Z')] }] },
        { step: 'transact', doc: 'a', steps: [del('a', 0, 1)] },
        del('a', 0, 0)
      ]
    }
  },
  {
    area: 'structs',
    name: 'client-id-collision',
    covers: ['STR-68', 'Q20'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 1)],
      steps: [ins('a', 0, 'abc'), sync('a', 'b')]
    }
  },
  {
    area: 'structs',
    name: 'merge-adjacent-structs',
    covers: ['STR-28', 'STR-45', 'STR-60', 'GC-08'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        { step: 'array.push', doc: 'a', target: arr, values: [1] },
        { step: 'array.push', doc: 'a', target: arr, values: [2] },
        { step: 'array.push', doc: 'a', target: arr, values: [3] },
        { step: 'array.push', doc: 'b', target: arr, values: ['b'] },
        sync('a', 'b'),
        { step: 'array.delete', doc: 'a', target: arr, at: 1 },
        { step: 'array.delete', doc: 'a', target: arr, at: 1 },
        ...both('a', 'b'),
        ins('a', 0, 'xy'), ins('a', 2, 'z'), ins('a', 3, 'w'), del('a', 1, 2), sync('a', 'b')
      ]
    }
  },
  {
    area: 'structs',
    name: 'client-id-extremes',
    covers: ['Q21', 'STR-02', 'STR-18', 'STR-20'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 4294967295), replica('b', 2147483648), replica('c', 1)],
      steps: [
        ins('a', 0, 'A'), ins('b', 0, 'B'), ins('c', 0, 'C'),
        ...all3('a', 'b', 'c'),
        ins('a', 1, 'x'), ins('b', 1, 'y'), ins('c', 1, 'z'),
        ...all3('a', 'b', 'c')
      ]
    }
  },
  {
    area: 'structs',
    name: 'client-id-53-bit',
    covers: ['Q21', 'STR-02'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 9007199254740991), replica('b', 1099511627776), replica('c', 4294967296)],
      steps: [
        ins('a', 0, 'A'), ins('b', 0, 'B'), ins('c', 0, 'C'),
        ...all3('a', 'b', 'c'),
        ins('a', 1, 'x'), del('b', 0, 1), ins('c', 1, 'z'),
        ...all3('a', 'b', 'c')
      ]
    }
  }
]
