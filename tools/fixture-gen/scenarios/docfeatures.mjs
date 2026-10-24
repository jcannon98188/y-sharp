// Spec 8 scenarios: Doc, subdocs, snapshots, relative positions, UndoManager and PermanentUserData (docs/research/doc-features.md).
// Every replica sets gc explicitly where it matters; snapshots need gc:false (SNAP-08). Time is virtual (tick).
import { replica, text, array, map } from '../lib/script.mjs'

const t = text('t')
const arr = array('arr')
const m = map('m')
const sync = (from, to) => ({ step: 'sync', from, to })
const both = (a, b) => [sync(a, b), sync(b, a)]
const ins = (doc, at, value, attributes, target = t) => ({ step: 'text.insert', doc, target, at, value, ...(attributes ? { attributes } : {}) })
const del = (doc, at, len, target = t) => ({ step: 'text.delete', doc, target, at, len })
const tick = ms => ({ step: 'tick', ms })
const sub = (doc, guid, extra = {}) => ({ step: 'subdoc.add', doc, target: arr, guid, ...extra })
const inspect = (doc, name) => ({ step: 'subdoc.inspect', doc, name })
const watch = (doc, name) => ({ step: 'subdoc.observe', doc, name })
const um = (doc, name, scope, extra = {}) => ({ step: 'undo.create', doc, name, scope, ...extra })
const undo = (name, as) => ({ step: 'undo.undo', name, ...(as ? { as } : {}) })
const redo = (name, as) => ({ step: 'undo.redo', name, ...(as ? { as } : {}) })
const stop = name => ({ step: 'undo.stopCapturing', name })

const subdocs = [
  {
    area: 'subdocs',
    name: 'array-options-and-events',
    covers: ['SUB-01', 'SUB-02', 'SUB-03', 'SUB-04', 'SUB-06', 'DOC-16', 'DOC-17', 'DOC-12'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        watch('a', 'ea'), watch('b', 'eb'),
        sub('a', 'plain'),
        sub('a', 'full', { opts: { gc: false, autoLoad: true, meta: 'm', collectionid: 'col' } }),
        sub('a', 'lazy', { opts: { shouldLoad: false, collectionid: 'col2' } }),
        sub('a', 'meta-number', { at: 0, opts: { meta: 7 } }),
        inspect('a', 'subA'),
        ...both('a', 'b'),
        inspect('b', 'subB'),
        { step: 'subdoc.load', doc: 'b', guid: 'plain' },
        { step: 'subdoc.load', doc: 'b', guid: 'plain' },
        inspect('b', 'subB2')
      ]
    }
  },
  {
    area: 'subdocs',
    name: 'duplicate-guids-and-delete',
    covers: ['SUB-07', 'SUB-08', 'DOC-16', 'DOC-12'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 3), replica('b', 4)],
      steps: [
        watch('a', 'ea'), watch('b', 'eb'),
        { step: 'subdoc.add', doc: 'a', target: m, key: 'a', guid: 'a' },
        { step: 'subdoc.add', doc: 'a', target: m, key: 'b', guid: 'a', opts: { shouldLoad: false } },
        { step: 'subdoc.add', doc: 'a', target: m, key: 'c', guid: 'c' },
        inspect('a', 'subA'),
        ...both('a', 'b'),
        inspect('b', 'subB'),
        { step: 'subdoc.load', doc: 'b', guid: 'a', index: 0 },
        { step: 'map.delete', doc: 'b', target: m, key: 'a' },
        inspect('b', 'subB2'),
        ...both('b', 'a'),
        inspect('a', 'subA2')
      ]
    }
  },
  {
    area: 'subdocs',
    name: 'destroy-and-reload',
    covers: ['SUB-11', 'SUB-12', 'SUB-13', 'DOC-15', 'DOC-12'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 5), replica('b', 6)],
      steps: [
        watch('a', 'ea'),
        sub('a', 'doc1'),
        sub('a', 'doc2', { opts: { autoLoad: true } }),
        { step: 'subdoc.destroy', doc: 'a', guid: 'doc1' },
        inspect('a', 'afterDestroy'),
        { step: 'subdoc.load', doc: 'a', guid: 'doc1' },
        inspect('a', 'afterLoad'),
        { step: 'subdoc.destroy', doc: 'a', guid: 'doc2' },
        inspect('a', 'afterDestroyAutoload'),
        // destroy a subdoc whose item was deleted in the same transaction: no replacement is reported as added
        { step: 'transact', doc: 'a', steps: [{ step: 'array.delete', doc: 'a', target: arr, at: 1, len: 1 }, { step: 'subdoc.destroy', doc: 'a', guid: 'doc2' }] },
        inspect('a', 'afterDeletedDestroy'),
        ...both('a', 'b'),
        inspect('b', 'subB')
      ]
    }
  },
  {
    area: 'subdocs',
    name: 'add-and-delete-in-one-transaction',
    covers: ['SUB-08', 'SUB-06'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 7), replica('b', 8)],
      steps: [
        watch('a', 'ea'),
        { step: 'transact', doc: 'a', steps: [sub('a', 'gone'), { step: 'array.delete', doc: 'a', target: arr, at: 0, len: 1 }] },
        sub('a', 'stays'),
        inspect('a', 'subA'),
        ...both('a', 'b'),
        inspect('b', 'subB')
      ]
    }
  },
  {
    area: 'subdocs',
    name: 'undo-redo-subdoc',
    covers: ['SUB-10', 'UM-19'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 9)],
      steps: [
        um('a', 'um', [arr]),
        watch('a', 'ea'),
        sub('a', 'u', { opts: { autoLoad: true, meta: 'kept' } }),
        undo('um', 'u1'),
        inspect('a', 'afterUndo'),
        redo('um', 'r1'),
        inspect('a', 'afterRedo')
      ]
    }
  }
]

const snapshots = [
  {
    area: 'snapshot',
    name: 'take-restore-and-encode',
    covers: ['SNAP-01', 'SNAP-02', 'SNAP-05', 'SNAP-08', 'SNAP-09', 'DOC-04'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, false)],
      steps: [
        ins('a', 0, 'world!'),
        { step: 'snapshot.take', doc: 'a', name: 's1' },
        ins('a', 0, 'hello '),
        { step: 'snapshot.restore', doc: 'a', snapshot: 's1', as: 'r1', clientId: 90 },
        del('a', 0, 6),
        { step: 'snapshot.take', doc: 'a', name: 's2' },
        { step: 'snapshot.restore', doc: 'a', snapshot: 's2', as: 'r2', clientId: 91 },
        { step: 'snapshot.restore', doc: 'a', snapshot: 's1', as: 'r3', clientId: 92 },
        { step: 'snapshot.read', doc: 'a', target: t, snapshot: 's2', name: 'read2' }
      ]
    }
  },
  {
    area: 'snapshot',
    name: 'array-deletions-and-empty',
    covers: ['SNAP-01', 'SNAP-02', 'SNAP-08', 'SNAP-05'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 2, false)],
      steps: [
        { step: 'snapshot.take', doc: 'a', name: 'empty' },
        { step: 'array.insert', doc: 'a', target: arr, at: 0, values: ['item1', 'item2', 'item3'] },
        { step: 'array.delete', doc: 'a', target: arr, at: 1 },
        { step: 'snapshot.take', doc: 'a', name: 'deleted' },
        { step: 'array.insert', doc: 'a', target: arr, at: 0, values: ['item0'] },
        { step: 'snapshot.restore', doc: 'a', snapshot: 'empty', as: 'rEmpty', clientId: 80 },
        { step: 'snapshot.restore', doc: 'a', snapshot: 'deleted', as: 'rDeleted', clientId: 81 }
      ]
    }
  },
  {
    area: 'snapshot',
    name: 'dependent-changes-two-clients',
    covers: ['SNAP-01', 'SNAP-08', 'SNAP-05'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 3, false), replica('b', 4, false)],
      steps: [
        { step: 'array.insert', doc: 'a', target: arr, at: 0, values: ['user1item1'] },
        ...both('a', 'b'),
        { step: 'array.insert', doc: 'b', target: arr, at: 1, values: ['user2item1'] },
        ...both('a', 'b'),
        { step: 'snapshot.take', doc: 'a', name: 'snap' },
        { step: 'array.insert', doc: 'a', target: arr, at: 2, values: ['user1item2'] },
        ...both('a', 'b'),
        { step: 'array.insert', doc: 'b', target: arr, at: 3, values: ['user2item2'] },
        ...both('a', 'b'),
        { step: 'snapshot.restore', doc: 'a', snapshot: 'snap', as: 'ra', clientId: 70 },
        { step: 'snapshot.restore', doc: 'b', snapshot: 'snap', as: 'rb', clientId: 71 }
      ]
    }
  },
  {
    area: 'snapshot',
    name: 'text-delta-between-snapshots',
    covers: ['SNAP-05', 'SNAP-06', 'SNAP-07', 'SNAP-01'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 5, false)],
      steps: [
        ins('a', 0, 'abc'),
        { step: 'snapshot.take', doc: 'a', name: 's1' },
        ins('a', 3, 'def', { bold: true }),
        del('a', 0, 1),
        { step: 'snapshot.take', doc: 'a', name: 's2' },
        ins('a', 0, 'Z'),
        { step: 'snapshot.read', doc: 'a', target: t, snapshot: 's1', name: 'readS1' },
        { step: 'snapshot.read', doc: 'a', target: t, snapshot: 's2', name: 'readS2' },
        { step: 'snapshot.read', doc: 'a', target: t, snapshot: 's2', prev: 's1', name: 'readS2PrevS1' }
      ]
    }
  }
]

const relpos = [
  {
    area: 'relpos',
    name: 'anchors-assoc-and-encoding',
    covers: ['RP-01', 'RP-02', 'RP-03', 'RP-04', 'RP-05', 'RP-06'],
    script: {
      replicas: [replica('a', 1)],
      steps: [
        ins('a', 0, 'abc'),
        ...[-1, 0, 1].flatMap(assoc => [0, 1, 3].flatMap(at => [
          { step: 'relpos.create', doc: 'a', target: t, name: `t${at}a${assoc}`, at, assoc },
          { step: 'relpos.resolve', doc: 'a', relpos: `t${at}a${assoc}`, name: `r${at}a${assoc}` }
        ])),
        // empty root text: a type-name anchor; nested type: a type-id anchor
        { step: 'relpos.create', doc: 'a', target: text('empty'), name: 'emptyRoot', at: 0 },
        { step: 'relpos.resolve', doc: 'a', relpos: 'emptyRoot', name: 'rEmptyRoot' },
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'text' },
        { step: 'relpos.create', doc: 'a', target: [arr, 0], name: 'nested', at: 0 },
        { step: 'relpos.resolve', doc: 'a', relpos: 'nested', name: 'rNested' },
        { step: 'text.insert', doc: 'a', target: [arr, 0], at: 0, value: 'xy' },
        { step: 'relpos.create', doc: 'a', target: [arr, 0], name: 'nested2', at: 1, assoc: -1 },
        { step: 'relpos.resolve', doc: 'a', relpos: 'nested2', name: 'rNested2' }
      ]
    }
  },
  {
    area: 'relpos',
    name: 'default-name-json-quirk',
    covers: ['RP-05', 'RP-04'],
    script: {
      replicas: [replica('a', 2)],
      steps: [
        { step: 'text.insert', doc: 'a', target: text(''), at: 0, value: 'hi' },
        { step: 'relpos.create', doc: 'a', target: text(''), name: 'start', at: 0, assoc: -1 },
        { step: 'relpos.create', doc: 'a', target: text(''), name: 'end', at: 2 },
        { step: 'relpos.resolve', doc: 'a', relpos: 'start', name: 'rStart' },
        { step: 'relpos.resolve', doc: 'a', relpos: 'end', name: 'rEnd' }
      ]
    }
  },
  {
    area: 'relpos',
    name: 'concurrent-edits-and-deleted-anchor',
    covers: ['RP-06', 'RP-08', 'RP-09', 'RP-02'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 3), replica('b', 4)],
      steps: [
        ins('a', 0, 'abcdef'),
        ...both('a', 'b'),
        { step: 'relpos.create', doc: 'a', target: t, name: 'mid', at: 3 },
        { step: 'relpos.create', doc: 'a', target: t, name: 'midLeft', at: 3, assoc: -1 },
        { step: 'relpos.create', doc: 'a', target: t, name: 'end', at: 6 },
        ins('b', 0, 'XX'),
        ins('b', 5, 'YY'),
        del('a', 2, 3),
        ...both('a', 'b'),
        { step: 'relpos.resolve', doc: 'a', relpos: 'mid', name: 'midA', followUndone: false },
        { step: 'relpos.resolve', doc: 'b', relpos: 'mid', name: 'midB', followUndone: false },
        { step: 'relpos.resolve', doc: 'a', relpos: 'midLeft', name: 'midLeftA', followUndone: false },
        { step: 'relpos.resolve', doc: 'b', relpos: 'midLeft', name: 'midLeftB', followUndone: false },
        { step: 'relpos.resolve', doc: 'a', relpos: 'end', name: 'endA', followUndone: false },
        ins('a', 4, '!'),
        { step: 'relpos.resolve', doc: 'a', relpos: 'mid', name: 'midA2', followUndone: false }
      ]
    }
  },
  {
    area: 'relpos',
    name: 'follow-undone-deletions',
    covers: ['RP-06', 'RP-08'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 5), replica('b', 6)],
      steps: [
        ins('a', 0, 'hello world'),
        um('a', 'um', [t]),
        // the undo manager was created after the first insert; stop capturing so the delete is its own stack item
        { step: 'relpos.create', doc: 'a', target: t, name: 'p', at: 1 },
        del('a', 0, 6),
        { step: 'relpos.resolve', doc: 'a', relpos: 'p', name: 'afterDelete' },
        undo('um'),
        { step: 'relpos.resolve', doc: 'a', relpos: 'p', name: 'afterUndoFollow' },
        { step: 'relpos.resolve', doc: 'a', relpos: 'p', name: 'afterUndoNoFollow', followUndone: false },
        ...both('a', 'b'),
        { step: 'relpos.resolve', doc: 'b', relpos: 'p', name: 'cloneFollow' },
        { step: 'relpos.resolve', doc: 'b', relpos: 'p', name: 'cloneNoFollow', followUndone: false }
      ]
    }
  }
]

const undos = [
  {
    area: 'undo',
    name: 'capture-timeout-merging',
    covers: ['UM-03', 'UM-04', 'UM-05', 'UM-08', 'UM-14', 'UM-06'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1)],
      steps: [
        um('a', 'um', [t]),
        ins('a', 0, 'a'), tick(100), ins('a', 1, 'b'), tick(499), ins('a', 2, 'c'),
        tick(500), ins('a', 3, 'd'),
        stop('um'), ins('a', 4, 'e'),
        undo('um', 'u1'), undo('um', 'u2'), undo('um', 'u3'), undo('um', 'u4'),
        redo('um', 'r1'), redo('um', 'r2'),
        ins('a', 0, 'N'),
        redo('um', 'r3')
      ]
    }
  },
  {
    area: 'undo',
    name: 'zero-and-infinite-capture-timeout',
    covers: ['UM-03', 'UM-08'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 2)],
      steps: [
        um('a', 'zero', [arr], { captureTimeout: 0 }),
        um('a', 'inf', [m], { captureTimeout: 1000000000000 }),
        { step: 'array.push', doc: 'a', target: arr, values: [1, 2] },
        { step: 'array.push', doc: 'a', target: arr, values: [3] },
        { step: 'map.set', doc: 'a', target: m, key: 'x', value: 1 },
        tick(100000),
        { step: 'map.set', doc: 'a', target: m, key: 'y', value: 2 },
        undo('zero', 'z1'), undo('zero', 'z2'), undo('zero', 'z3'),
        undo('inf', 'i1'), undo('inf', 'i2')
      ]
    }
  },
  {
    area: 'undo',
    name: 'remote-edits-and-redo',
    covers: ['UM-10', 'UM-20', 'UM-11', 'UM-01'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        um('a', 'um', [t]),
        ins('a', 0, 'abc'),
        ins('b', 0, 'xyz'),
        ...both('a', 'b'),
        undo('um'),
        ...both('a', 'b'),
        redo('um'),
        ...both('a', 'b'),
        del('b', 0, 1),
        ...both('b', 'a'),
        undo('um'),
        redo('um'),
        { step: 'text.format', doc: 'a', target: t, at: 1, len: 3, attributes: { bold: true } },
        undo('um'),
        redo('um'),
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'undo',
    name: 'tracked-origins-and-scope',
    covers: ['UM-01', 'UM-02', 'UM-05'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 3)],
      steps: [
        um('a', 'tracked', [t], { trackedOrigins: ['mine', null], captureTimeout: 0 }),
        um('a', 'other', [arr], { trackedOrigins: ['mine'], captureTimeout: 0 }),
        ins('a', 0, 'null-origin'),
        { step: 'transact', doc: 'a', origin: 'mine', steps: [ins('a', 0, 'M'), { step: 'array.push', doc: 'a', target: arr, values: ['x'] }] },
        { step: 'transact', doc: 'a', origin: 'theirs', steps: [ins('a', 0, 'T'), { step: 'array.push', doc: 'a', target: arr, values: ['y'] }] },
        undo('tracked', 't1'), undo('tracked', 't2'), undo('tracked', 't3'),
        undo('other', 'o1'), undo('other', 'o2')
      ]
    }
  },
  {
    area: 'undo',
    name: 'redo-stack-invalidation',
    covers: ['UM-05', 'UM-04'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 4)],
      steps: [
        um('a', 'um', [t], { captureTimeout: 0 }),
        ins('a', 0, 'one'), ins('a', 3, 'two'),
        undo('um', 'u1'),
        ins('a', 3, 'three'),
        redo('um', 'r1'),
        undo('um', 'u2'), undo('um', 'u3'), undo('um', 'u4')
      ]
    }
  },
  {
    area: 'undo',
    name: 'map-delete-and-overwrite',
    covers: ['UM-10', 'UM-12'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 5), replica('b', 6)],
      steps: [
        um('a', 'um', [m], { captureTimeout: 0 }),
        um('a', 'ign', [map('n')], { captureTimeout: 0, ignoreRemoteMapChanges: true }),
        { step: 'map.set', doc: 'a', target: m, key: 'a', value: 'a' },
        { step: 'map.delete', doc: 'a', target: m, key: 'a' },
        { step: 'map.set', doc: 'a', target: m, key: 'a', value: 'b' },
        { step: 'map.set', doc: 'a', target: m, key: 'a', value: 'c' },
        ...both('a', 'b'),
        { step: 'map.set', doc: 'b', target: m, key: 'a', value: 'remote' },
        ...both('b', 'a'),
        undo('um', 'u1'), undo('um', 'u2'), redo('um', 'r1'),
        { step: 'map.set', doc: 'a', target: map('n'), key: 'x', value: 1 },
        { step: 'map.set', doc: 'b', target: map('n'), key: 'x', value: 2 },
        ...both('b', 'a'),
        { step: 'map.set', doc: 'a', target: map('n'), key: 'x', value: 3 },
        { step: 'map.set', doc: 'b', target: map('n'), key: 'x', value: 4 },
        ...both('b', 'a'),
        undo('ign', 'ig1'),
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'undo',
    name: 'nested-types-and-formatting',
    covers: ['UM-10', 'UM-11', 'UM-18'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 7, false), replica('b', 8, false)],
      steps: [
        um('a', 'um', [arr], { captureTimeout: 0 }),
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'map' },
        { step: 'map.set', doc: 'a', target: [arr, 0], key: 'k', value: 1 },
        { step: 'array.insertType', doc: 'a', target: arr, at: 1, kind: 'text', init: 'initial text' },
        { step: 'text.format', doc: 'a', target: [arr, 1], at: 0, len: 7, attributes: { bold: true } },
        ...both('a', 'b'),
        { step: 'map.set', doc: 'b', target: [arr, 0], key: 'remote', value: true },
        ...both('b', 'a'),
        { step: 'array.delete', doc: 'a', target: arr, at: 1 },
        undo('um', 'u1'), undo('um', 'u2'), undo('um', 'u3'), undo('um', 'u4'), undo('um', 'u5'),
        redo('um', 'r1'), redo('um', 'r2'), redo('um', 'r3'), redo('um', 'r4'),
        ...both('a', 'b')
      ]
    }
  }
]

const puds = [
  {
    area: 'pud',
    name: 'users-map-layout-and-lookups',
    covers: ['PUD-01', 'PUD-03', 'PUD-06', 'PUD-08', 'PUD-09'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        { step: 'pud.setUserDescription', doc: 'a', clientId: 1, description: 'user a' },
        { step: 'pud.setUserDescription', doc: 'b', clientId: 2, description: 'user b' },
        ins('a', 0, 'xhi'), del('a', 0, 1),
        ins('b', 0, 'hxxi'), del('b', 1, 2),
        tick(10),
        ...both('a', 'b'),
        tick(10),
        ...both('a', 'b'),
        { step: 'pud.observe', doc: 'a', name: 'pudA', clientIds: [1, 2, 3], deletedIds: [[1, 3], [1, 4], [1, 5], [2, 4], [2, 5], [2, 6], [2, 7], [9, 0]] },
        { step: 'pud.observe', doc: 'b', name: 'pudB', clientIds: [1, 2], deletedIds: [[1, 4], [2, 5], [2, 7]] }
      ]
    }
  },
  {
    area: 'pud',
    name: 'late-construction-reads-existing-data',
    covers: ['PUD-02', 'PUD-01', 'PUD-06', 'PUD-08'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 3), replica('c', 5)],
      steps: [
        { step: 'pud.setUserDescription', doc: 'a', clientId: 3, description: 'user a' },
        ins('a', 0, 'hello'), del('a', 1, 2),
        tick(0),
        { step: 'sync', from: 'a', to: 'c' },
        { step: 'pud.observe', doc: 'c', name: 'pudC', clientIds: [3, 5], deletedIds: [[3, 4], [3, 5], [3, 6], [3, 7]] }
      ]
    }
  },
  {
    area: 'pud',
    name: 'concurrent-user-creation-overwrite',
    covers: ['PUD-04', 'PUD-05', 'PUD-03', 'PUD-09'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2), replica('c', 7)],
      steps: [
        { step: 'pud.setUserDescription', doc: 'a', clientId: 1, description: 'user a' },
        { step: 'pud.setUserDescription', doc: 'b', clientId: 2, description: 'user a' },
        ins('a', 0, 'abc'), del('a', 0, 1),
        ins('b', 0, 'xyz'), del('b', 0, 1),
        tick(0),
        ...both('a', 'b'),
        tick(0),
        ...both('a', 'b'),
        tick(0),
        ...both('a', 'b'),
        { step: 'pud.observe', doc: 'a', name: 'pudA', clientIds: [1, 2], deletedIds: [[1, 0], [1, 4], [2, 3], [2, 4], [2, 5]] },
        { step: 'pud.observe', doc: 'b', name: 'pudB', clientIds: [1, 2], deletedIds: [[1, 0], [1, 4], [2, 3], [2, 4], [2, 5]] }
      ]
    }
  }
]

const docs = [
  {
    area: 'doc',
    name: 'update-events-and-gc-flag',
    covers: ['DOC-10', 'DOC-01', 'DOC-08', 'DOC-09'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1, true), replica('b', 2, false)],
      steps: [
        ins('a', 0, 'abcdef'), ins('b', 0, 'ghijkl'),
        ...both('a', 'b'),
        del('a', 1, 2), del('b', 1, 2),
        { step: 'transact', doc: 'a', origin: 'o', steps: [ins('a', 0, 'T1'), del('a', 3, 1), ins('a', 0, 'T2')] },
        ...both('a', 'b'),
        { step: 'gc.run', doc: 'b' },
        { step: 'gc.run', doc: 'b', gc: true },
        { step: 'transact', doc: 'a', steps: [] },
        ...both('a', 'b'),
        { step: 'gc.run', doc: 'a', gc: false }
      ]
    }
  },
  {
    area: 'doc',
    name: 'root-type-promotion-from-update',
    covers: ['DOC-03', 'DOC-04', 'DOC-06'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 3), replica('b', 4)],
      steps: [
        { step: 'text.insert', doc: 'a', target: text(''), at: 0, value: 'h' },
        { step: 'text.insert', doc: 'a', target: text(''), at: 1, value: 'i' },
        { step: 'text.insert', doc: 'a', target: text('named'), at: 0, value: 'n' },
        ...both('a', 'b'),
        { step: 'text.insert', doc: 'b', target: text(''), at: 2, value: '!' },
        ...both('b', 'a')
      ]
    }
  }
]

export default [...subdocs, ...snapshots, ...relpos, ...undos, ...puds, ...docs]
