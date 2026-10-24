// Update-manipulation functions and format conversion (ENC-23..25). Input bytes are recorded literally in the script.
import { replica, text, map } from '../lib/script.mjs'
import { updatesOf, stateAfter } from '../lib/authoring.mjs'
import { Y } from '../lib/oracle.mjs'
import { interpret } from '../lib/steps.mjs'
import { normalize } from '../lib/script.mjs'

const t = text('t')
const m = map('m')

const base = {
  replicas: [replica('a', 1), replica('b', 2)],
  steps: [
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'abc' },
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'text.insert', doc: 'b', target: t, at: 3, value: 'XY' },
    { step: 'map.set', doc: 'a', target: m, key: 'k', value: [1, 'two'] },
    { step: 'text.delete', doc: 'a', target: t, at: 0, len: 1 }
  ]
}

export default () => {
  const ua = updatesOf(base, 'a')
  const ub = updatesOf(base, 'b')
  const ua2 = updatesOf(base, 'a', 'v2')
  const ub2 = updatesOf(base, 'b', 'v2')
  const fullA = stateAfter(base, 'a')
  const fullA2 = stateAfter(base, 'a', 'v2')
  const svB = Y.encodeStateVector(interpret(normalize(base)).docs.get('b'))
  return [
    {
      area: 'codecs',
      name: 'merge-diff-convert',
      covers: ['ENC-23', 'ENC-24', 'ENC-25', 'ENC-21'],
      script: {
        replicas: [replica('r', 9)],
        steps: [
          { step: 'mergeUpdates', name: 'merge-v1', updates: [...ua, ...ub] },
          { step: 'mergeUpdates', name: 'merge-v1-reversed', updates: [...ub, ...ua].reverse() },
          { step: 'mergeUpdates', name: 'merge-v2', updates: [...ua2, ...ub2], encoding: 'v2' },
          { step: 'diffUpdate', name: 'diff-v1', update: fullA, sv: svB },
          { step: 'convertUpdate', name: 'v1-to-v2', update: fullA, from: 'v1' },
          { step: 'convertUpdate', name: 'v2-to-v1', update: fullA2, from: 'v2' },
          ...ua.map(bytes => ({ step: 'applyUpdate', doc: 'r', bytes })),
          ...ub2.map(bytes => ({ step: 'applyUpdate', doc: 'r', bytes, encoding: 'v2' })),
          { step: 'text.insert', doc: 'r', target: t, at: 0, value: '!' }
        ]
      }
    }
  ]
}
