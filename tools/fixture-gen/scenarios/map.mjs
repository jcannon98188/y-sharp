import { map, replica } from '../lib/script.mjs'

const m = map('m')
const sync = (from, to) => ({ step: 'sync', from, to })
const set = (doc, key, value) => ({ step: 'map.set', doc, target: m, key, value })

export default [
  {
    area: 'map',
    name: 'set-delete-clear',
    covers: ['M-1', 'M-4', 'M-5'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        set('a', 'x', 1), set('a', 'y', 'two'), set('a', 'x', 3),
        { step: 'map.delete', doc: 'a', target: m, key: 'y' },
        { step: 'map.delete', doc: 'a', target: m, key: 'missing' },
        sync('a', 'b'),
        { step: 'map.clear', doc: 'b', target: m },
        set('b', 'z', null),
        sync('b', 'a')
      ]
    }
  },
  {
    area: 'map',
    name: 'value-kinds',
    covers: ['M-8', 'M-11', 'ENC-07'],
    script: {
      replicas: [replica('a', 3), replica('b', 4)],
      steps: [
        set('a', '10', 1), set('a', '2', 2), set('a', 'b', 3), set('a', 'a', 4), set('a', '', 5),
        set('a', 'neg-zero', -0), set('a', 'nan', NaN), set('a', 'inf', Infinity), set('a', 'ninf', -Infinity),
        set('a', 'undef', undefined), set('a', 'big', 2n ** 62n), set('a', 'float', 0.1), set('a', 'int31', 2147483647),
        set('a', 'bytes', new Uint8Array([1, 2, 3])), set('a', 'lone', '\udc00x'), set('a', 'nested', new Map([['10', [1, 2]], ['a', null]])),
        sync('a', 'b')
      ]
    }
  },
  {
    area: 'map',
    name: 'concurrent-set',
    covers: ['M-9'],
    observe: ['structs', 'ds'],
    script: {
      replicas: [replica('a', 8), replica('b', 9)],
      steps: [
        set('a', 'k', 'from-a'), set('b', 'k', 'from-b'), set('b', 'only-b', 1),
        sync('a', 'b'), sync('b', 'a'),
        { step: 'observe', doc: 'a', target: m, name: 'ma' },
        set('a', 'k', 'again'),
        { step: 'map.delete', doc: 'a', target: m, key: 'only-b' }
      ]
    }
  }
]
