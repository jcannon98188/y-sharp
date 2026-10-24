// Spec 9: y-protocols awareness, sync and auth (ADR 0014). Awareness runs on virtual time (`tick`); every byte string is
// produced by the oracle (lib0 encoders / y-protocols writers), never hand-derived.
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { replica, text } from '../lib/script.mjs'
import { Session } from '../lib/authoring.mjs'
import { Y, lib0 } from '../lib/oracle.mjs'
import { untag } from '../lib/tagged.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const { encoding } = lib0

/** An awareness update with exactly these `[clientId, clock, state]` entries (state is JSON-stringified, null allowed). */
const au = entries => {
  const e = encoding.createEncoder()
  encoding.writeVarUint(e, entries.length)
  for (const [id, clock, state] of entries) {
    encoding.writeVarUint(e, id)
    encoding.writeVarUint(e, clock)
    encoding.writeVarString(e, JSON.stringify(state))
  }
  return encoding.toUint8Array(e)
}
const raw = (...b) => new Uint8Array(b)
const cat = (...parts) => Uint8Array.from(parts.flatMap(p => Array.from(p)))

const newAw = (awareness, doc) => ({ step: 'awareness.new', awareness, doc })
const setLocal = (awareness, state) => ({ step: 'awareness.setLocal', awareness, state })
const apply = (awareness, bytes, origin, name) => ({ step: 'awareness.applyUpdate', awareness, bytes, ...(origin ? { origin } : {}), ...(name ? { name } : {}) })
const applyFrom = (awareness, from, origin) => ({ step: 'awareness.applyUpdate', awareness, from, ...(origin ? { origin } : {}) })
const encode = (name, awareness, clients, states) => ({ step: 'awareness.encode', name, awareness, clients, ...(states ? { states } : {}) })
const tick = ms => ({ step: 'tick', ms })

const equalityCases = JSON.parse(readFileSync(path.join(here, 'equality-cases.json'), 'utf8'))

const t = text('t')

// ---- sync helpers: documents with content, built through the oracle so lengths and vectors are real
const helloSession = () => new Session([replica('a', 1), replica('b', 2)])
  .push({ step: 'text.insert', doc: 'a', target: t, at: 0, value: 'hello' })
  .push({ step: 'text.insert', doc: 'b', target: t, at: 0, value: 'xy' })
const textA = { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'hello' }
const textB = { step: 'text.insert', doc: 'b', target: t, at: 0, value: 'xy' }

export default [
  // ------------------------------------------------------------------ awareness
  {
    area: 'awareness',
    name: 'constructor-state',
    covers: ['AW-3', 'AW-4', 'AW-6'],
    script: { replicas: [replica('ra', 7)], steps: [newAw('a', 'ra')] }
  },
  {
    area: 'awareness',
    name: 'local-state-lifecycle',
    covers: ['AW-7', 'AW-8', 'AW-9', 'AW-10', 'AW-12', 'AW-26'],
    script: {
      replicas: [replica('ra', 1)],
      steps: [
        newAw('a', 'ra'),
        { step: 'awareness.setLocalField', awareness: 'a', field: 'early', value: 1 },
        setLocal('a', { x: 1 }),
        setLocal('a', { x: 1 }),
        setLocal('a', { x: 2, nested: { k: [1, 2] } }),
        { step: 'awareness.setLocalField', awareness: 'a', field: 'y', value: 'two' },
        setLocal('a', null),
        { step: 'awareness.setLocalField', awareness: 'a', field: 'ignored', value: true },
        setLocal('a', null),
        setLocal('a', {}),
        encode('afterRemoval', 'a', [1])
      ]
    }
  },
  {
    area: 'awareness',
    name: 'two-peer-test-awareness',
    covers: ['AW-6', 'AW-8', 'AW-9', 'AW-30', 'AW-42', 'AW-43', 'AW-48', 'AW-50'],
    script: {
      replicas: [replica('ra', 0), replica('rb', 1)],
      steps: [
        newAw('a', 'ra'), newAw('b', 'rb'),
        encode('e0', 'a', [0]), applyFrom('b', 'e0', 'custom'),
        setLocal('a', { x: 3 }), encode('e1', 'a', [0]), applyFrom('b', 'e1', 'custom'),
        setLocal('a', { x: 4 }), encode('e2', 'a', [0]), applyFrom('b', 'e2', 'custom'),
        setLocal('a', { x: 4 }), encode('e3', 'a', [0]), applyFrom('b', 'e3', 'custom'),
        setLocal('a', null), encode('e4', 'a', [0]), applyFrom('b', 'e4', 'custom')
      ]
    }
  },
  {
    area: 'awareness',
    name: 'apply-clock-rules',
    covers: ['AW-41', 'AW-42', 'AW-43', 'AW-44', 'AW-46', 'AW-47', 'AW-48', 'AW-49', 'AW-50', 'AW-52'],
    script: {
      replicas: [replica('rb', 9)],
      steps: [
        newAw('b', 'rb'),
        apply('b', au([[5, 0, { a: 1 }]]), 'r'),
        tick(10), apply('b', au([[5, 1, { a: 1 }]]), 'r'),
        apply('b', au([[5, 1, { a: 2 }]]), 'r'),
        apply('b', au([[5, 1, { a: 1 }]]), 'r'),
        tick(10), apply('b', au([[5, 2, { a: 1 }]]), 'r'),
        apply('b', au([[5, 3, { a: 3 }]]), 'r'),
        apply('b', au([[5, 2, { a: 9 }]]), 'r'),
        apply('b', au([[5, 3, null]]), 'r'),
        apply('b', au([[5, 3, null]]), 'r'),
        apply('b', au([[5, 4, null]]), 'r'),
        apply('b', au([[5, 5, { a: 5 }]]), 'r'),
        apply('b', au([[77, 3, null]]), 'r'),
        apply('b', au([[77, 3, { z: 1 }]]), 'r'),
        apply('b', au([[77, 4, { z: 1 }]]), 'r'),
        tick(5),
        apply('b', au([[10, 1, { n: 1 }], [11, 1, { n: 2 }], [5, 6, null], [12, 1, null]]), 'batch'),
        apply('b', au([]), 'empty'),
        apply('b', au([[10, 1, { n: 1 }]]))
      ]
    }
  },
  {
    area: 'awareness',
    name: 'local-client-rules',
    covers: ['AW-26', 'AW-45', 'AW-46'],
    script: {
      replicas: [replica('rb', 9)],
      steps: [
        newAw('b', 'rb'),
        setLocal('b', { me: 1 }),
        apply('b', au([[9, 1, null]]), 'r'),
        apply('b', au([[9, 5, null]]), 'r'),
        apply('b', au([[9, 7, { overwritten: true }]]), 'r'),
        setLocal('b', null),
        apply('b', au([[9, 20, null]]), 'r'),
        apply('b', au([[9, 21, { back: 1 }]]), 'r')
      ]
    }
  },
  {
    area: 'awareness',
    name: 'remove-states',
    covers: ['AW-25', 'AW-26', 'AW-27', 'AW-28', 'AW-29'],
    script: {
      replicas: [replica('rb', 9)],
      steps: [
        newAw('b', 'rb'),
        setLocal('b', { me: 1 }),
        apply('b', au([[1, 1, { a: 1 }], [2, 4, { a: 2 }], [3, 2, { a: 3 }]]), 'r'),
        tick(100),
        { step: 'awareness.removeStates', awareness: 'b', clients: [], origin: 'none' },
        { step: 'awareness.removeStates', awareness: 'b', clients: [99], origin: 'unknown' },
        { step: 'awareness.removeStates', awareness: 'b', clients: [2, 2, 99, 3], origin: 'dup' },
        { step: 'awareness.removeStates', awareness: 'b', clients: [9, 9, 1] },
        { step: 'awareness.removeStates', awareness: 'b', clients: [9] },
        encode('tomb', 'b', [1, 2, 3, 9]),
        apply('b', au([[2, 4, { a: 'same-clock' }]]), 'r'),
        apply('b', au([[2, 5, { a: 'newer' }]]), 'r')
      ]
    }
  },
  {
    area: 'awareness',
    name: 'remote-expiry',
    covers: ['AW-14', 'AW-16', 'AW-17', 'AW-18', 'AW-19', 'AW-49'],
    script: {
      replicas: [replica('rb', 9)],
      steps: [
        newAw('b', 'rb'),
        apply('b', au([[5, 1, { who: 'five' }]]), 'r'),
        tick(1),
        apply('b', au([[6, 1, { who: 'six' }]]), 'r'),
        tick(26999), // t = 27000: still alive
        tick(3000), // t = 30000: client 5 is exactly 30000 old and goes, client 6 is 29999 old and stays
        tick(2999), // t = 32999
        tick(1), // t = 33000: client 6 goes
        tick(30000), // removed clients are not removed again
        apply('b', au([[5, 1, { who: 'stale' }]]), 'r'),
        apply('b', au([[5, 2, { who: 'back' }]]), 'r'),
        tick(33000)
      ]
    }
  },
  {
    area: 'awareness',
    name: 'local-renewal',
    covers: ['AW-14', 'AW-15', 'AW-17', 'AW-19', 'AW-9'],
    script: {
      replicas: [replica('rb', 9)],
      steps: [
        newAw('b', 'rb'),
        tick(1),
        setLocal('b', { me: 1 }),
        tick(14998), // t = 14999
        tick(1), // t = 15000: interval fires but the state is only 14999 ms old
        tick(2999), // t = 17999
        tick(1), // t = 18000: renewed
        tick(15000),
        tick(30000)
      ]
    }
  },
  {
    area: 'awareness',
    name: 'local-renewal-boundary',
    covers: ['AW-15', 'AW-17'],
    script: {
      replicas: [replica('rb', 9)],
      steps: [
        newAw('b', 'rb'),
        setLocal('b', { me: 1 }), // lastUpdated = 0, so the tick at exactly 15000 renews
        tick(14999),
        tick(1)
      ]
    }
  },
  {
    area: 'awareness',
    name: 'destroy',
    covers: ['AW-22', 'AW-26'],
    script: {
      replicas: [replica('ra', 4)],
      steps: [
        newAw('a', 'ra'),
        setLocal('a', { here: true }),
        { step: 'awareness.destroy', awareness: 'a' },
        tick(60000)
      ]
    }
  },
  {
    area: 'awareness',
    name: 'client-ids-53-bit',
    covers: ['AW-34'],
    script: {
      replicas: [replica('rb', 1099511627776)],
      steps: [
        newAw('b', 'rb'),
        setLocal('b', { name: 'wide' }),
        apply('b', au([[9007199254740991, 7, { max: true }], [4294967296, 1, null]]), 'r'),
        encode('all', 'b', [1099511627776, 9007199254740991, 4294967296])
      ]
    }
  },
  {
    area: 'awareness',
    name: 'encode-wire-vectors',
    covers: ['AW-5', 'AW-30', 'AW-31', 'AW-32', 'AW-33', 'AW-34', 'AW-35'],
    script: {
      replicas: [replica('rb', 2147483648)],
      steps: [
        newAw('b', 'rb'),
        setLocal('b', { name: 'héllo ✓ 😀', n: 1.5 }),
        apply('b', au([[4294967295, 3, { big: true }], [300, 128, { id: 300 }], [16384, 2, null], [1, 1, { a: [1, { b: null }] }]]), 'r'),
        apply('b', au([[8, 2, null]]), 'r'),
        encode('all', 'b', [2147483648, 4294967295, 300, 16384, 1, 8]),
        encode('reordered', 'b', [1, 300, 2147483648]),
        encode('duplicates', 'b', [1, 1, 300, 1]),
        encode('empty', 'b', []),
        encode('missing', 'b', [1, 12345]),
        encode('substituted', 'b', [1, 300], [[1, { replaced: 'state' }]]),
        encode('tombstone', 'b', [16384, 8]),
        setLocal('b', { quote: 'a"b\\c', control: 'line\nbreak', surrogate: '😀', nan: NaN, neg: -0 }),
        encode('json-escapes', 'b', [2147483648])
      ]
    }
  },
  {
    area: 'awareness',
    name: 'modify-update',
    covers: ['AW-36', 'AW-37', 'AW-38', 'AW-39', 'AW-40'],
    script: {
      replicas: [replica('rb', 2)],
      steps: [
        newAw('b', 'rb'),
        setLocal('b', { name: 'héllo', k: 1 }),
        apply('b', au([[3, 2, { z: 1, a: 2 }], [4, 9, null]]), 'r'),
        encode('src', 'b', [2, 3, 4]),
        { step: 'awareness.modify', name: 'identity', mode: 'identity', from: 'src' },
        { step: 'awareness.modify', name: 'blank', mode: 'blank', from: 'src' },
        { step: 'awareness.modify', name: 'setField', mode: 'setField', field: 'by', value: 'server', from: 'src' },
        { step: 'awareness.modify', name: 'keepNull', mode: 'setFieldKeepNull', field: 'by', value: 'server', from: 'src' },
        { step: 'awareness.modify', name: 'undefined', mode: 'undefined', from: 'src' },
        { step: 'awareness.modify', name: 'trailing', mode: 'identity', bytes: cat(au([[3, 2, { z: 1, a: 2 }]]), raw(0xde, 0xad)) },
        { step: 'awareness.modify', name: 'empty', mode: 'identity', bytes: au([]) },
        { step: 'awareness.modify', name: 'truncated', mode: 'identity', bytes: au([[3, 2, { z: 1 }]]).subarray(0, 6) },
        { step: 'awareness.modify', name: 'spaced-input', mode: 'identity', bytes: cat(raw(1, 3, 2), (() => { const s = Buffer.from('{ "b" : 1 , "a" : 2 }'); return cat(raw(s.length), s) })()) }
      ]
    }
  },
  {
    area: 'awareness',
    name: 'apply-trailing-and-truncated',
    covers: ['AW-39', 'AW-41', 'AW-51'],
    script: {
      replicas: [replica('rb', 9), replica('rp', 8)],
      steps: [
        newAw('b', 'rb'), newAw('partial', 'rp'),
        apply('b', cat(au([[5, 1, { ok: 1 }]]), raw(1, 2, 3)), 'trailing', 'trailing'),
        // two valid entries then a cut in the middle of the third: JS applies the first two (no events), y-sharp applies none
        apply('partial', au([[11, 1, { a: 1 }], [12, 1, { a: 2 }], [13, 1, { a: 3 }]]).subarray(0, 18), 'cut', 'truncated'),
        apply('partial', cat(raw(2), au([[21, 1, { a: 1 }]]).subarray(1)), 'short', 'count-too-large'),
        apply('partial', cat(raw(2, 22, 1, 5), Buffer.from('{"a":')), 'badjson', 'invalid-json-cut'),
        apply('partial', cat(raw(2), au([[31, 1, { a: 1 }]]).subarray(1), raw(32, 1, 3), Buffer.from('nul')), 'badjson2', 'invalid-json-text'),
        apply('b', au([[6, 1, { after: 'errors' }]]), 'r')
      ]
    }
  },
  {
    area: 'awareness',
    name: 'structural-equality',
    covers: ['AW-9', 'AW-11', 'AW-50'],
    script: {
      replicas: [replica('ra', 1)],
      steps: [
        newAw('a', 'ra'),
        ...equalityCases.flatMap(c => [
          setLocal('a', untag(c.a)),
          setLocal('a', untag(c.b)),
          setLocal('a', {})
        ])
      ]
    }
  },

  // ------------------------------------------------------------------ sync
  {
    area: 'sync',
    name: 'empty-doc-messages',
    covers: ['SY-1', 'SY-2', 'SY-3', 'SY-4', 'SY-5', 'SY-6'],
    script: {
      replicas: [replica('a', 1)],
      steps: [
        { step: 'sync.step1', name: 'step1', doc: 'a' },
        { step: 'sync.step2', name: 'step2', doc: 'a' },
        { step: 'sync.step2', name: 'step2-empty-sv', doc: 'a', sv: raw(0) },
        { step: 'sync.update', name: 'update-junk', update: raw(1, 2, 3, 255) },
        { step: 'sync.update', name: 'update-empty', update: raw() },
        { step: 'sync.update', name: 'update-long', update: new Uint8Array(200).fill(7) },
        { step: 'sync.update', name: 'combined', update: raw(9), append: 'step1' },
        { step: 'sync.step1', name: 'combined2', doc: 'a', append: 'combined' }
      ]
    }
  },
  {
    area: 'sync',
    name: 'step1-step2-roundtrip',
    covers: ['SY-4', 'SY-7', 'SY-8', 'SY-9', 'SY-11', 'SY-12', 'SY-14', 'SY-16'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        textA, textB,
        { step: 'sync.step1', name: 'b-step1', doc: 'b' },
        { step: 'sync.read', name: 'a-reads-b-step1', doc: 'a', from: 'b-step1' },
        { step: 'sync.read', name: 'b-reads-reply', doc: 'b', from: 'a-reads-b-step1', origin: 'peer' },
        { step: 'sync.step1', name: 'a-step1', doc: 'a' },
        { step: 'sync.read', name: 'b-reads-a-step1', doc: 'b', from: 'a-step1' },
        { step: 'sync.read', name: 'a-reads-reply', doc: 'a', from: 'b-reads-a-step1' },
        { step: 'sync.step2', name: 'full', doc: 'a' },
        { step: 'sync.read', name: 'step2-again', doc: 'b', from: 'full' },
        { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'more' },
        { step: 'sync.step2', name: 'diff', doc: 'a', sv: Y.encodeStateVector(new Session([replica('a', 1), replica('b', 2)]).push(textA).push(textB).doc('b')) },
        { step: 'sync.read', name: 'update-msg', doc: 'b', from: 'diff' },
        { step: 'sync.update', name: 'as-update', update: Y.encodeStateAsUpdate(helloSession().doc('a')) },
        { step: 'sync.read', name: 'read-update', doc: 'b', from: 'as-update' }
      ]
    }
  },
  {
    area: 'sync',
    name: 'step2-with-state-vector',
    covers: ['SY-4', 'SY-16'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        textA, textB,
        { step: 'sync.step2', name: 'vs-empty', doc: 'a', sv: raw(0) },
        { step: 'sync.step2', name: 'vs-b', doc: 'a', sv: Y.encodeStateVector(helloSession().doc('b')) },
        { step: 'sync.step2', name: 'vs-ahead', doc: 'b', sv: Y.encodeStateVector(helloSession().doc('a')) },
        { step: 'sync.step2', name: 'vs-self', doc: 'a', sv: Y.encodeStateVector(helloSession().doc('a')) }
      ]
    }
  },
  {
    area: 'sync',
    name: 'read-errors',
    covers: ['SY-7', 'SY-8', 'SY-11', 'SY-19', 'SY-20'],
    script: {
      replicas: [replica('a', 1)],
      steps: [
        textA,
        { step: 'sync.read', name: 'unknown-type', doc: 'a', bytes: raw(3, 0) },
        { step: 'sync.read', name: 'unknown-type-large', doc: 'a', bytes: raw(0x80, 0x01, 0, 0) },
        { step: 'sync.read', name: 'step1-missing-payload', doc: 'a', bytes: raw(0) },
        { step: 'sync.read', name: 'step1-short-payload', doc: 'a', bytes: raw(0, 5, 1) },
        { step: 'sync.read', name: 'step1-bad-state-vector', doc: 'a', bytes: raw(0, 2, 0xff, 0xff) },
        { step: 'sync.read', name: 'step2-missing-payload', doc: 'a', bytes: raw(1) },
        { step: 'sync.read', name: 'update-missing-payload', doc: 'a', bytes: raw(2) },
        { step: 'sync.read', name: 'step2-short-payload', doc: 'a', bytes: raw(1, 9, 1, 2) },
        { step: 'sync.read', name: 'step2-corrupt-update', doc: 'a', bytes: raw(1, 3, 0xff, 0xff, 0xff) },
        { step: 'sync.read', name: 'update-corrupt-update', doc: 'a', bytes: raw(2, 3, 0xff, 0xff, 0xff) },
        { step: 'sync.read', name: 'empty-message', doc: 'a', bytes: raw() },
        { step: 'sync.read', name: 'step2-empty-payload', doc: 'a', bytes: raw(1, 0) }
      ]
    }
  },
  {
    area: 'sync',
    name: 'pending-and-idempotent',
    covers: ['SY-17', 'SY-18', 'SY-8'],
    script: (() => {
      const s = new Session([replica('a', 1)])
        .push({ step: 'text.insert', doc: 'a', target: t, at: 0, value: 'one' })
      const first = Y.encodeStateAsUpdate(s.doc('a'))
      const sv1 = Y.encodeStateVector(s.doc('a'))
      s.push({ step: 'text.insert', doc: 'a', target: t, at: 3, value: 'two' })
      const second = Y.encodeStateAsUpdate(s.doc('a'), sv1)
      return {
        replicas: [replica('a', 1), replica('b', 2)],
        steps: [
          { step: 'sync.update', name: 'm-second', update: second },
          { step: 'sync.update', name: 'm-first', update: first },
          { step: 'sync.read', name: 'second-first', doc: 'b', from: 'm-second', origin: 'o' },
          { step: 'sync.read', name: 'first-second', doc: 'b', from: 'm-first', origin: 'o' },
          { step: 'sync.read', name: 'first-again', doc: 'b', from: 'm-first', origin: 'o' },
          { step: 'sync.read', name: 'second-again', doc: 'b', from: 'm-second', origin: 'o' }
        ]
      }
    })()
  },
  {
    area: 'sync',
    name: 'peer-convergence',
    covers: ['SY-12', 'SY-13', 'SY-14'],
    script: {
      replicas: [replica('a', 1), replica('b', 2)],
      steps: [
        textA, textB,
        { step: 'sync.step1', name: 'a1', doc: 'a' }, { step: 'sync.step1', name: 'b1', doc: 'b' },
        // concurrent edits between the Step1s and the Step2s: Step2 is computed from the sender's current state
        { step: 'text.insert', doc: 'a', target: t, at: 5, value: '!' },
        { step: 'sync.read', name: 'a-gets-b1', doc: 'a', from: 'b1' },
        { step: 'sync.read', name: 'b-gets-a1', doc: 'b', from: 'a1' },
        { step: 'sync.read', name: 'b-gets-a2', doc: 'b', from: 'a-gets-b1' },
        { step: 'sync.read', name: 'a-gets-b2', doc: 'a', from: 'b-gets-a1' }
      ]
    }
  },

  // ------------------------------------------------------------------ auth
  {
    area: 'auth',
    name: 'permission-denied',
    covers: ['AU-1', 'AU-2'],
    script: {
      replicas: [replica('a', 1)],
      steps: [
        { step: 'auth.permissionDenied', name: 'no', reason: 'no' },
        { step: 'auth.permissionDenied', name: 'empty', reason: '' },
        { step: 'auth.permissionDenied', name: 'utf8', reason: 'zugriff verweigert: ä ✓ 😀' },
        { step: 'auth.permissionDenied', name: 'long', reason: 'x'.repeat(200) }
      ]
    }
  }
]
