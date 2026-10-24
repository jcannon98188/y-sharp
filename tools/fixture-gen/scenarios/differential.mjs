// applyForeign (ADR 0012, DIFF-02): bytes another implementation produced, applied to a fresh replica. The literal update bytes are
// recorded in the script, exactly as the live harness sends them (y-sharp's bytes to Node, Node's bytes to y-sharp).
import { replica, text, map, array } from '../lib/script.mjs'
import { updatesOf, stateAfter } from '../lib/authoring.mjs'

const t = text('t')
const m = map('m')
const a = array('a')

const base = {
  replicas: [replica('a', 1), replica('b', 2)],
  steps: [
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'hello' },
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'text.insert', doc: 'b', target: t, at: 5, value: ' world' },
    { step: 'text.format', doc: 'a', target: t, at: 0, len: 3, attributes: { bold: true } },
    { step: 'map.set', doc: 'a', target: m, key: 'k', value: [1, 'two', null] },
    { step: 'array.push', doc: 'b', target: a, values: ['x', 2] },
    { step: 'text.delete', doc: 'a', target: t, at: 1, len: 1 }
  ]
}

export default () => {
  const ua = updatesOf(base, 'a')
  const ub = updatesOf(base, 'b')
  const full1 = stateAfter(base, 'a')
  const full2 = stateAfter(base, 'b', 'v2')
  return [
    {
      area: 'differential',
      name: 'apply-foreign',
      covers: ['DIFF-02', 'ENC-21'],
      script: {
        replicas: [replica('f', 9), replica('g', 10)],
        steps: [
          ...ua.map(bytes => ({ step: 'applyForeign', doc: 'f', bytes })),
          ...ub.map(bytes => ({ step: 'applyForeign', doc: 'f', bytes })),
          // duplicate delivery of foreign bytes is idempotent; V2 bytes carry the same state
          { step: 'applyForeign', doc: 'f', bytes: full1, name: 'duplicate-v1' },
          { step: 'applyForeign', doc: 'g', bytes: full2, encoding: 'v2', name: 'full-v2' },
          // an empty update is rejected by both implementations; the outcome is observable, not an error
          { step: 'applyForeign', doc: 'g', bytes: new Uint8Array(0), name: 'empty' },
          { step: 'text.insert', doc: 'f', target: t, at: 0, value: '>' }
        ]
      }
    }
  ]
}
