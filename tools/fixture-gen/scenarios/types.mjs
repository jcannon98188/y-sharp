// Spec 7 (TASK-2.7): shared types and events. Text formatting and deltas, array and map details, XML trees and attributes,
// observer and transaction ordering. Registry prefixes: T-0, A, A-E, M, M-E, T, F, E, D, X, Y, X-E, V, TX and the quirks Q23, Q26, Q27.
import { replica, text, array, map, xmlFragment, xmlText } from '../lib/script.mjs'

const sync = (from, to) => ({ step: 'sync', from, to })
const both = (a, b) => [sync(a, b), sync(b, a)]
const t = text('t')
const arr = array('arr')
const m = map('m')
const x = xmlFragment('x')
const xt = xmlText('xt')

const tins = (doc, at, value, attributes, target = t) => ({ step: 'text.insert', doc, target, at, value, ...(attributes ? { attributes } : {}) })
const tfmt = (doc, at, len, attributes, target = t) => ({ step: 'text.format', doc, target, at, len, attributes })
const tdel = (doc, at, len, target = t) => ({ step: 'text.delete', doc, target, at, len })
const mset = (doc, key, value, target = m) => ({ step: 'map.set', doc, target, key, value })
const xel = (doc, target, at, name) => ({ step: 'xml.insert', doc, target, at, kind: 'element', name })
const xtext = (doc, target, at, init) => ({ step: 'xml.insert', doc, target, at, kind: 'text', ...(init === undefined ? {} : { init }) })
const xattr = (doc, target, name, value) => ({ step: 'xml.setAttribute', doc, target, name, value })

export default [
  // ---------------------------------------------------------------- text
  {
    area: 'text',
    name: 'format-cleanup',
    covers: ['F-2', 'F-3', 'F-7', 'F-8', 'T-3'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 51), replica('b', 52)],
      steps: [
        tins('a', 0, 'abcdef', { bold: true }),
        sync('a', 'b'),
        tfmt('a', 2, 2, { bold: null }),
        tins('b', 3, 'XY'),
        tfmt('b', 0, 6, { bold: true }),
        tdel('b', 1, 4),
        ...both('a', 'b'),
        tins('a', 0, 'inherit'),
        tfmt('a', 0, 3, { bold: null }),
        tdel('a', 0, 3),
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'text',
    name: 'concurrent-formatting',
    covers: ['F-9', 'Y-6', 'Y-3'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 61), replica('b', 62)],
      steps: [
        tins('a', 0, 'abcde'),
        sync('a', 'b'),
        tfmt('a', 0, 3, { bold: true }),
        tfmt('b', 2, 2, { bold: true }),
        { step: 'observe', doc: 'b', target: t, name: 'tb' },
        ...both('a', 'b'),
        tfmt('b', 0, 5, { bold: null }),
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'text',
    name: 'attribute-equality',
    covers: ['Q27', 'F-5', 'F-4', 'D-2'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 71), replica('b', 72)],
      steps: [
        tins('a', 0, 'abcdef'),
        tfmt('a', 0, 6, { em: { x: 1 }, n: 1, s: 'a' }),
        // equal flat objects, equal scalars: no new format marks
        tfmt('a', 1, 2, { em: { x: 1 }, n: 1, s: 'a' }),
        // a nested object compares by reference in equalFlat, so this is a change
        tfmt('a', 3, 1, { em: { x: { y: 1 } } }),
        tfmt('a', 4, 1, { em: { x: { y: 1 } } }),
        // falsy values are values
        tfmt('a', 0, 2, { z: false, zero: 0, empty: '' }),
        tfmt('a', 1, 1, { z: false, zero: 0, empty: '' }),
        sync('a', 'b')
      ]
    }
  },
  {
    area: 'text',
    name: 'apply-delta-edges',
    covers: ['T-6', 'T-8', 'T-9', 'E-4', 'E-6', 'D-1', 'D-4'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 81), replica('b', 82)],
      steps: [
        { step: 'text.applyDelta', doc: 'a', target: t, delta: [] },
        { step: 'text.applyDelta', doc: 'a', target: t, delta: [{ insert: 'abc' }, { insert: { linebreak: 's' } }, { insert: 'def', attributes: { i: true } }] },
        { step: 'text.applyDelta', doc: 'a', target: t, delta: [{ retain: 2 }, { delete: 1 }, { insert: 'ZZ', attributes: { b: 1 } }, { retain: 1, attributes: { b: null } }] },
        { step: 'text.applyDelta', doc: 'a', target: t, delta: [{ retain: 100 }, { insert: 'tail' }] },
        { step: 'text.applyDelta', doc: 'a', target: t, delta: [{ retain: 2 }, { delete: 100 }] },
        sync('a', 'b')
      ]
    }
  },
  {
    area: 'text',
    name: 'embed-attributes',
    covers: ['E-1', 'E-2', 'E-3', 'E-6', 'D-1'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 91), replica('b', 92)],
      steps: [
        tins('a', 0, 'ab', { bold: true }),
        { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: { image: 'imageSrc.png' }, attributes: { width: 100 } },
        { step: 'text.insertEmbed', doc: 'a', target: t, at: 2, value: { image: 'second.png' } },
        { step: 'text.insertEmbed', doc: 'a', target: t, at: 0, value: 42 },
        sync('a', 'b'),
        tdel('b', 1, 2),
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'text',
    name: 'nested-text-embeds',
    covers: ['E-5', 'T-0.8', 'T-4', 'A-19'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 101), replica('b', 102)],
      steps: [
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'text', init: 'fresh' },
        { step: 'array.insertType', doc: 'a', target: arr, at: 1, kind: 'array', init: [[1, 2], [3]] },
        tins('a', 5, '!', undefined, [arr, 0]),
        sync('a', 'b'),
        tfmt('b', 0, 2, { bold: true }, [arr, 0]),
        { step: 'array.delete', doc: 'b', target: arr, at: 0 },
        sync('b', 'a')
      ]
    }
  },
  // ---------------------------------------------------------------- array
  {
    area: 'array',
    name: 'insert-in-deleted-region',
    covers: ['A-17', 'A-13', 'A-16'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 111), replica('b', 112), replica('c', 113)],
      steps: [
        { step: 'array.push', doc: 'a', target: arr, values: [1, 2, 3, 4, 5] },
        sync('a', 'b'), sync('a', 'c'),
        { step: 'array.delete', doc: 'b', target: arr, at: 1, len: 3 },
        { step: 'array.insert', doc: 'a', target: arr, at: 2, values: ['mid'] },
        { step: 'array.insert', doc: 'c', target: arr, at: 3, values: ['mid-c', 'mid-c2'] },
        ...both('a', 'b'), ...both('a', 'c'), ...both('b', 'c'), ...both('a', 'b')
      ]
    }
  },
  {
    area: 'array',
    name: 'binary-and-mixed',
    covers: ['A-15', 'A-16', 'A-4', 'A-3'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 121), replica('b', 122)],
      steps: [
        { step: 'array.push', doc: 'a', target: arr, values: [] },
        { step: 'array.push', doc: 'a', target: arr, values: [new Uint8Array([1, 2, 3]), 'str', new Uint8Array([]), 7, null] },
        { step: 'array.insert', doc: 'a', target: arr, at: 0, values: [] },
        { step: 'array.insert', doc: 'a', target: arr, at: 2, values: [{ k: new Uint8Array([9]) }, [new Uint8Array([8])]] },
        sync('a', 'b'),
        { step: 'array.delete', doc: 'b', target: arr, at: 1, len: 2 },
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'array',
    name: 'events-and-targets',
    covers: ['A-E1', 'A-E2', 'A-E4', 'A-E5', 'A-E6', 'A-E8', 'V-6', 'V-7', 'V-8'],
    script: {
      replicas: [replica('a', 131), replica('b', 132)],
      steps: [
        { step: 'observe', doc: 'a', target: arr, name: 'arr-a' },
        { step: 'observe', doc: 'b', target: arr, name: 'arr-b' },
        { step: 'array.insert', doc: 'a', target: arr, at: 0, values: [0, 1, 2] },
        { step: 'array.delete', doc: 'a', target: arr, at: 0 },
        {
          step: 'transact',
          doc: 'a',
          origin: 'tx',
          steps: [
            { step: 'array.push', doc: 'a', target: arr, values: ['x', 'y'] },
            { step: 'array.delete', doc: 'a', target: arr, at: 3 },
            { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'map' },
            mset('a', 'tst', 42, [arr, 0]),
            { step: 'array.insert', doc: 'a', target: arr, at: 1, values: [9] }
          ]
        },
        sync('a', 'b'),
        { step: 'array.delete', doc: 'b', target: arr, at: 0, len: 2 },
        sync('b', 'a')
      ]
    }
  },
  // ---------------------------------------------------------------- map
  {
    area: 'map',
    name: 'key-order',
    covers: ['Q26', 'M-11', 'M-3', 'M-6'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 141), replica('b', 142)],
      steps: [
        mset('a', 'b', 1), mset('a', '10', 2), mset('a', '2', 3), mset('a', 'a', 4), mset('a', '-1', 5), mset('a', '01', 6),
        mset('a', '4294967294', 7), mset('a', '4294967295', 8), mset('a', '1.5', 9), mset('a', '', 10), mset('a', '__proto__', 11), mset('a', 'constructor', 12),
        mset('a', 'u', undefined),
        sync('a', 'b'),
        { step: 'map.delete', doc: 'b', target: m, key: '2' },
        mset('b', '2', 'again'),
        mset('b', '3', 'new'),
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'map',
    name: 'events-keys',
    covers: ['M-E1', 'M-E2', 'M-E3', 'M-E4', 'M-E7', 'V-5', 'Q23'],
    script: {
      replicas: [replica('a', 151), replica('b', 152)],
      steps: [
        { step: 'observe', doc: 'a', target: m, name: 'm-a' },
        { step: 'observe', doc: 'b', target: m, name: 'm-b' },
        mset('a', 'k', 1),
        mset('a', 'k', 2),
        { step: 'transact', doc: 'a', steps: [mset('a', 'k', 3), mset('a', 'k', 4)] },
        { step: 'transact', doc: 'a', steps: [mset('a', 'n', 1), mset('a', 'n', 2)] },
        { step: 'transact', doc: 'a', steps: [mset('a', 'gone', 1), { step: 'map.delete', doc: 'a', target: m, key: 'gone' }] },
        { step: 'map.insertType', doc: 'a', target: m, key: 'child', kind: 'array' },
        mset('a', 'child', 'replaced'),
        { step: 'map.delete', doc: 'a', target: m, key: 'k' },
        sync('a', 'b'),
        mset('b', 'k', 'from-b'),
        { step: 'map.clear', doc: 'b', target: m },
        sync('b', 'a')
      ]
    }
  },
  // ---------------------------------------------------------------- xml
  {
    area: 'xml',
    name: 'element-tree',
    covers: ['X-E1', 'X-E2', 'X-E4', 'X-E6', 'T-0.8'],
    observe: ['structs'],
    truncation: [{ replica: 'b', encoding: 'v1' }],
    script: {
      replicas: [replica('a', 161), replica('b', 162)],
      steps: [
        xel('a', x, 0, 'p'),
        xel('a', x, 1, 'img'),
        xtext('a', x, 0, 'lead'),
        xel('a', [x, 1], 0, 'span'),
        xtext('a', [x, 1], 1, 'inner'),
        xattr('a', [x, 1], 'class', 'c1'),
        xattr('a', [x, 1, 0], 'id', 7),
        sync('a', 'b'),
        { step: 'xml.delete', doc: 'b', target: x, at: 2 },
        xel('b', x, 2, 'br'),
        { step: 'xml.delete', doc: 'b', target: [x, 1], at: 0, len: 2 },
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'xml',
    name: 'attributes',
    covers: ['X-1', 'X-2', 'X-E2', 'X-E7', 'V-5'],
    script: {
      replicas: [replica('a', 171), replica('b', 172)],
      steps: [
        xel('a', x, 0, 'div'),
        xtext('a', x, 1, 'text'),
        sync('a', 'b'),
        xattr('a', [x, 0], 'height', '10'),
        xattr('b', [x, 0], 'height', '20'),
        xattr('a', [x, 0], 'obj', { a: [1, 2], b: null }),
        xattr('a', [x, 1], 'test', 42),
        { step: 'xml.removeAttribute', doc: 'a', target: [x, 0], name: 'missing' },
        ...both('a', 'b'),
        { step: 'xml.removeAttribute', doc: 'b', target: [x, 0], name: 'height' },
        xattr('a', [x, 1], 'test', 43),
        ...both('a', 'b')
      ]
    }
  },
  {
    area: 'xml',
    name: 'events',
    covers: ['X-E5', 'Y-1', 'Y-7', 'V-1', 'T-0.1'],
    script: {
      replicas: [replica('a', 181), replica('b', 182)],
      steps: [
        { step: 'observe', doc: 'a', target: x, name: 'x-a' },
        { step: 'observe', doc: 'b', target: x, name: 'x-b' },
        xel('a', x, 0, 'p'),
        { step: 'observe', doc: 'a', target: [x, 0], name: 'p-a' },
        xtext('a', [x, 0], 0, 'some text'),
        { step: 'observe', doc: 'a', target: [x, 0, 0], name: 'xt-a' },
        sync('a', 'b'),
        xattr('a', [x, 0], 'key', 'value'),
        tins('a', 4, '!', undefined, [x, 0, 0]),
        xattr('a', [x, 0, 0], 'k2', 1),
        sync('a', 'b'),
        { step: 'xml.removeAttribute', doc: 'a', target: [x, 0], name: 'key' },
        { step: 'xml.delete', doc: 'a', target: [x, 0], at: 0 },
        sync('a', 'b')
      ]
    }
  },
  {
    area: 'xml',
    name: 'text-in-fragment',
    covers: ['X-1', 'F-1', 'E-1', 'T-5', 'Y-2'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 191), replica('b', 192)],
      steps: [
        xtext('a', x, 0),
        { step: 'text.applyDelta', doc: 'a', target: [x, 0], delta: [{ insert: 'A', attributes: { em: {}, strong: {} } }, { insert: 'B', attributes: { em: {} } }, { insert: 'C', attributes: { em: {}, strong: {} } }] },
        { step: 'text.insertEmbed', doc: 'a', target: [x, 0], at: 1, value: { image: 'x.png' } },
        tins('a', 0, '\u{1F600}', { bold: true }, [x, 0]),
        sync('a', 'b'),
        tdel('b', 1, 1, [x, 0]),
        sync('b', 'a')
      ]
    }
  },
  // ---------------------------------------------------------------- events and transactions
  {
    area: 'events',
    name: 'remote-updates',
    covers: ['TX-14', 'TX-15', 'M-E7', 'A-E6'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 201), replica('b', 202)],
      steps: [
        { step: 'observe', doc: 'b', target: arr, name: 'arr-b' },
        { step: 'observe', doc: 'b', target: m, name: 'm-b' },
        { step: 'observe', doc: 'b', target: t, name: 't-b' },
        // a transaction without changes emits no update event; neither does deleting a missing key
        { step: 'transact', doc: 'a', steps: [] },
        { step: 'map.delete', doc: 'a', target: m, key: 'absent' },
        { step: 'map.clear', doc: 'a', target: m },
        { step: 'array.push', doc: 'a', target: arr, values: [1, 2] },
        mset('a', 'k', 'v'),
        tins('a', 0, 'abc'),
        sync('a', 'b'),
        sync('a', 'b'),
        { step: 'array.delete', doc: 'b', target: arr, at: 0 },
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'events',
    name: 'transaction-ordering',
    covers: ['TX-1', 'TX-5', 'TX-10', 'TX-18', 'TX-19', 'Q23'],
    script: {
      replicas: [replica('a', 211), replica('b', 212)],
      steps: [
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'map' },
        { step: 'observe', doc: 'a', target: arr, name: 'arr' },
        { step: 'observe', doc: 'a', target: [arr, 0], name: 'child' },
        { step: 'observe', doc: 'a', target: t, name: 'text' },
        { step: 'observe', doc: 'a', target: m, name: 'map' },
        {
          step: 'transact',
          doc: 'a',
          origin: 'outer',
          steps: [
            mset('a', 'one', 1, [arr, 0]),
            tins('a', 0, 'x'),
            {
              step: 'transact',
              doc: 'a',
              steps: [mset('a', 'two', 2), tins('a', 1, 'y'), mset('a', 'one', 11, [arr, 0])]
            },
            { step: 'array.push', doc: 'a', target: arr, values: [5] },
            tins('a', 2, 'z'),
            mset('a', 'three', 3, [arr, 0])
          ]
        },
        sync('a', 'b'),
        { step: 'observe', doc: 'b', target: [arr, 0], name: 'child-b' },
        { step: 'observe', doc: 'b', target: t, name: 'text-b' },
        {
          step: 'transact',
          doc: 'b',
          steps: [mset('b', 'one', 'remote', [arr, 0]), tdel('b', 0, 1)]
        },
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'xml',
    name: 'xml-text-root',
    covers: ['X-1', 'F-5'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 61), replica('b', 62)],
      steps: [
        tins('a', 0, 'AB', { em: {}, strong: {} }, xt),
        tins('a', 2, 'C', { em: {} }, xt),
        tfmt('a', 1, 1, { strong: null }, xt),
        { step: 'xml.setAttribute', doc: 'a', target: xt, name: 'k', value: 1 },
        sync('a', 'b'),
        tins('b', 0, 'z', undefined, xt),
        tdel('a', 1, 1, xt),
        ...both('a', 'b')
      ]
    }
  }
]
