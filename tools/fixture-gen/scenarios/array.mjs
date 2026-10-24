import { array, replica } from '../lib/script.mjs'

const arr = array('arr')
const sync = (from, to) => ({ step: 'sync', from, to })

export default [
  {
    area: 'array',
    name: 'insert-push-delete',
    covers: ['A-1', 'A-4', 'A-5', 'A-16'],
    observe: ['structs', 'ds'],
    truncation: [{ replica: 'b', encoding: 'v1' }],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        { step: 'array.push', doc: 'a', target: arr, values: [1, 'two', true, null] },
        { step: 'array.insert', doc: 'a', target: arr, at: 1, values: [1.5, -0, 'x'] },
        { step: 'array.unshift', doc: 'a', target: arr, values: [{ k: [1, 2] }] },
        { step: 'array.delete', doc: 'a', target: arr, at: 2, len: 2 },
        sync('a', 'b'),
        { step: 'array.delete', doc: 'b', target: arr, at: 0 },
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'array',
    name: 'nested-types',
    covers: ['A-9', 'T-0.8', 'M-12'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 5), replica('b', 6)],
      steps: [
        { step: 'array.insertType', doc: 'a', target: arr, at: 0, kind: 'map' },
        { step: 'array.insertType', doc: 'a', target: arr, at: 1, kind: 'text', init: 'inner' },
        { step: 'map.set', doc: 'a', target: [arr, 0], key: 'n', value: 7 },
        { step: 'map.insertType', doc: 'a', target: [arr, 0], key: 'list', kind: 'array' },
        { step: 'array.push', doc: 'a', target: [arr, 0, 'list'], values: ['deep'] },
        { step: 'text.insert', doc: 'a', target: [arr, 1], at: 5, value: '!' },
        sync('a', 'b'),
        { step: 'array.delete', doc: 'b', target: arr, at: 0 },
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'array',
    name: 'concurrent-insert-order',
    covers: ['A-18'],
    observe: ['structs'],
    script: {
      replicas: [replica('a', 100), replica('b', 7), replica('c', 4000000000)],
      steps: [
        { step: 'array.push', doc: 'a', target: arr, values: ['x', 'y'] },
        sync('a', 'b'), sync('a', 'c'),
        { step: 'array.insert', doc: 'a', target: arr, at: 1, values: ['from-a'] },
        { step: 'array.insert', doc: 'b', target: arr, at: 1, values: ['from-b'] },
        { step: 'array.insert', doc: 'c', target: arr, at: 1, values: ['from-c'] },
        sync('a', 'b'), sync('b', 'a'), sync('c', 'a'), sync('a', 'c'), sync('c', 'b'), sync('b', 'c')
      ]
    }
  }
]
