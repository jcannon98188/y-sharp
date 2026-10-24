// Hand-written copy of the upstream randomized-test machinery (ADR 0017): the TestConnector / init / applyRandomTests of
// yjs/tests/testHelper.js and the four operation generators (mapTransactions, arrayTransactions, textChanges, qChanges), on the
// pinned lib0 `prng`. Instead of mutating documents directly it emits ops steps and runs them through the Node interpreter, so
// the recorded op log (edits plus delivery schedule) is exactly what the oracle executed (ADR 0011: replay needs no PRNG).
//
// The draw sequence matches upstream draw for draw (tools/fixture-gen/check-sim-draws.mjs proves it against the upstream tests).
// The defects of research section 6.7 are fixed and recorded as simulation divergences SIM-01..05 (ADR 0017):
//   SIM-03  the code-block operation uses the key `attributes` (upstream passes `format`, which applyDelta ignores)
//   SIM-04  the coin-flip draw of `init` selects V1 or V2 for the whole run and the run uses it
//   SIM-05  the payload counters belong to one run (upstream leaks them between tests)
// SIM-01 and SIM-02 concern the checks of `compare`/`checkResult`, which live on the C# side.
import { lib0, Y } from './oracle.mjs'
import { Session } from './authoring.mjs'
import { replica, normalize } from './script.mjs'
import './steps-sim.mjs'

const { prng } = lib0

const mapT = { root: 'map', kind: 'map' }
const arrayT = { root: 'array', kind: 'array' }
const textT = { root: 'text', kind: 'text' }

/** Only check-sim-draws.mjs flips this: it reproduces upstream's `format` key to show SIM-03 is the one divergence of the quill draws. */
export const upstreamDefects = { codeBlockKey: 'attributes' }

/** Payload counters of one run (SIM-05): `_uniqueNumber` of y-array.tests.js and `charCounter` of y-text.tests.js. */
export const newCounters = () => ({ unique: 0, char: 0 })

// ---------------------------------------------------------------------------------------------------------------- plans
// A plan draws from the generator, reads what it needs of the document through `view`, and returns the ops steps to run.
// view.length()            array/text `.length`
// view.stringLength()      `text.toString().length` (embeds do not count)
// view.nestedArrayLength(i) length of the Y.Array at array index i, or -1 when the element there is not a Y.Array

const KEYS = ['one', 'two']

/** @type {Array<(gen:any, doc:string, view:any, c:any) => any[]>} */
export const mapMods = [
  // set
  (gen, doc) => {
    const key = prng.oneOf(gen, KEYS)
    const value = prng.utf16String(gen)
    return [{ step: 'map.set', doc, target: mapT, key, value }]
  },
  // setType
  (gen, doc) => {
    const key = prng.oneOf(gen, KEYS)
    const kind = prng.oneOf(gen, ['array', 'map'])
    const deep = [{ root: 'map', kind: 'map' }, key]
    return [
      { step: 'map.insertType', doc, target: mapT, key, kind },
      kind === 'array'
        ? { step: 'array.insert', doc, target: deep, at: 0, values: [1, 2, 3, 4] }
        : { step: 'map.set', doc, target: deep, key: 'deepkey', value: 'deepvalue' }
    ]
  },
  // _delete
  (gen, doc) => {
    const key = prng.oneOf(gen, KEYS)
    return [{ step: 'map.delete', doc, target: mapT, key }]
  }
]

export const arrayMods = [
  // insert
  (gen, doc, view, c) => {
    const uniqueNumber = c.unique++
    const len = prng.int32(gen, 1, 4)
    const content = []
    for (let i = 0; i < len; i++) content.push(uniqueNumber)
    const pos = prng.int32(gen, 0, view.length())
    return [{ step: 'array.insert', doc, target: arrayT, at: pos, values: content }]
  },
  // insertTypeArray
  (gen, doc, view) => {
    const pos = prng.int32(gen, 0, view.length())
    return [
      { step: 'array.insertType', doc, target: arrayT, at: pos, kind: 'array' },
      { step: 'array.insert', doc, target: [arrayT, pos], at: 0, values: [1, 2, 3, 4] }
    ]
  },
  // insertTypeMap
  (gen, doc, view) => {
    const pos = prng.int32(gen, 0, view.length())
    return [
      { step: 'array.insertType', doc, target: arrayT, at: pos, kind: 'map' },
      ...[42, 43, 44].map(value => ({ step: 'map.set', doc, target: [arrayT, pos], key: 'someprop', value }))
    ]
  },
  // insertTypeNull
  (gen, doc, view) => {
    const pos = prng.int32(gen, 0, view.length())
    return [{ step: 'array.insert', doc, target: arrayT, at: pos, values: [null] }]
  },
  // _delete
  (gen, doc, view) => {
    const length = view.length()
    if (length > 0) {
      let somePos = prng.int32(gen, 0, length - 1)
      let delLength = prng.int32(gen, 1, Math.min(2, length - somePos))
      if (prng.bool(gen)) {
        const nested = view.nestedArrayLength(somePos)
        if (nested > 0) {
          const outer = somePos
          somePos = prng.int32(gen, 0, nested - 1)
          delLength = prng.int32(gen, 0, Math.min(2, nested - somePos))
          return [{ step: 'array.delete', doc, target: [arrayT, outer], at: somePos, len: delLength }]
        }
      } else {
        return [{ step: 'array.delete', doc, target: arrayT, at: somePos, len: delLength }]
      }
    }
    return []
  }
]

export const textMods = [
  // insert text
  (gen, doc, view, c) => {
    const insertPos = prng.int32(gen, 0, view.length())
    const value = c.char++ + prng.word(gen)
    return [{ step: 'text.insert', doc, target: textT, at: insertPos, value }]
  },
  // delete text
  (gen, doc, view) => {
    const contentLen = view.stringLength()
    const insertPos = prng.int32(gen, 0, contentLen)
    const overwrite = Math.min(prng.int32(gen, 0, contentLen - insertPos), 2)
    return [{ step: 'text.delete', doc, target: textT, at: insertPos, len: overwrite }]
  }
]

const marks = [{ bold: true }, { italic: true }, { italic: true, color: '#888' }]
const marksChoices = [undefined, ...marks]
const IMAGE = 'https://user-images.githubusercontent.com/5553757/48975307-61efb100-f06d-11e8-9177-ee895e5916e5.png'

export const quillMods = [
  // insert text
  (gen, doc, view, c) => {
    const insertPos = prng.int32(gen, 0, view.length())
    const attrs = prng.oneOf(gen, marksChoices)
    const value = c.char++ + prng.word(gen)
    return [{ step: 'text.insert', doc, target: textT, at: insertPos, value, ...(attrs === undefined ? {} : { attributes: attrs }) }]
  },
  // insert embed
  (gen, doc, view) => {
    const insertPos = prng.int32(gen, 0, view.length())
    if (prng.bool(gen)) {
      return [{ step: 'text.insertEmbed', doc, target: textT, at: insertPos, value: { image: IMAGE } }]
    }
    const key = prng.word(gen)
    const value = prng.word(gen)
    return [{ step: 'text.insertEmbedType', doc, target: textT, at: insertPos, kind: 'map', init: new Map([[key, value]]) }]
  },
  // delete text
  (gen, doc, view) => {
    const contentLen = view.stringLength()
    const insertPos = prng.int32(gen, 0, contentLen)
    const overwrite = Math.min(prng.int32(gen, 0, contentLen - insertPos), 2)
    return [{ step: 'text.delete', doc, target: textT, at: insertPos, len: overwrite }]
  },
  // format text
  (gen, doc, view) => {
    const contentLen = view.stringLength()
    const insertPos = prng.int32(gen, 0, contentLen)
    const overwrite = Math.min(prng.int32(gen, 0, contentLen - insertPos), 2)
    const format = prng.oneOf(gen, marks)
    return [{ step: 'text.format', doc, target: textT, at: insertPos, len: overwrite, attributes: format }]
  },
  // insert codeblock (SIM-03: `attributes`, not upstream's ignored `format`)
  (gen, doc, view, c) => {
    const insertPos = prng.int32(gen, 0, view.stringLength())
    const text = c.char++ + prng.word(gen)
    const ops = []
    if (insertPos > 0) ops.push({ retain: insertPos })
    ops.push({ insert: text }, { insert: '\n', [upstreamDefects.codeBlockKey]: { 'code-block': true } })
    return [{ step: 'text.applyDelta', doc, target: textT, delta: ops }]
  },
  // complex delta op
  (gen, doc, view) => {
    const contentLen = view.stringLength()
    let currentPos = Math.max(0, prng.int32(gen, 0, contentLen - 1))
    const ops = currentPos > 0 ? [{ retain: currentPos }] : []
    for (let i = 0; i < 7 && currentPos < contentLen; i++) {
      prng.oneOf(gen, [
        () => { // format
          const retain = Math.min(prng.int32(gen, 0, contentLen - currentPos), 5)
          const format = prng.oneOf(gen, marks)
          ops.push({ retain, attributes: format })
          currentPos += retain
        },
        () => { // insert
          const attrs = prng.oneOf(gen, marksChoices)
          const text = prng.word(gen, 1, 3)
          ops.push({ insert: text, ...(attrs === undefined ? {} : { attributes: attrs }) })
        },
        () => { // delete
          const delLen = Math.min(prng.int32(gen, 0, contentLen - currentPos), 10)
          ops.push({ delete: delLen })
          currentPos += delLen
        }
      ])()
    }
    return [{ step: 'text.applyDelta', doc, target: textT, delta: ops }]
  }
]

export const generators = { map: mapMods, array: arrayMods, text: textMods, quill: quillMods }

// ---------------------------------------------------------------------------------------------------------------- network

const names = n => Array.from({ length: n }, (_, i) => String.fromCharCode(97 + i))

/** The replica names of a run: a, b, c, ... with client ids 0..n-1 (testHelper `init`). */
export const replicasOf = users => names(users).map((n, i) => replica(n, i))

/**
 * One simulation run: `init`, `iterations` times the `applyRandomTests` loop, then the reconnect-and-flush part of `compare`.
 * @returns {{script:{replicas:any[],steps:any[]}, encoding:'v1'|'v2', session:Session}}
 */
export const runSim = ({ gen, kind, iterations, users = 5, collect }) => {
  const mods = generators[kind]
  const nm = names(users)
  // SIM-04: the one draw of init selects the encoding of the whole run
  const enc = prng.bool(gen) ? 'v2' : 'v1'
  const session = new Session(replicasOf(users))
  const counters = newCounters()
  let svSeq = 0
  let replySeq = 0

  const insts = nm.map(name => ({ name, receiving: new Map() }))
  const allConns = new Set()
  const onlineConns = new Set()

  const emit = entry => {
    session.push({ step: 'deliver', schedule: [entry], ...(enc === 'v2' ? { encoding: 'v2' } : {}) })
    publish()
  }
  const captureSv = inst => {
    const name = `s${svSeq++}`
    emit({ do: 'sv', doc: inst.name, name })
    return name
  }

  const receive = (inst, message, remote) => {
    if (!inst.receiving.has(remote)) inst.receiving.set(remote, [])
    inst.receiving.get(remote).push(message)
  }
  const broadcast = (inst, message) => {
    if (onlineConns.has(inst)) for (const remote of onlineConns) if (remote !== inst) receive(remote, message, inst)
  }
  const connect = inst => {
    if (onlineConns.has(inst)) return
    onlineConns.add(inst)
    broadcast(inst, { kind: 'step1', sv: captureSv(inst) })
    for (const remote of onlineConns) if (remote !== inst) receive(inst, { kind: 'step1', sv: captureSv(remote) }, remote)
  }
  const disconnect = inst => {
    inst.receiving = new Map()
    onlineConns.delete(inst)
  }
  const create = inst => {
    allConns.add(inst)
    connect(inst)
  }

  const flushRandomMessage = () => {
    const conns = Array.from(onlineConns).filter(c => c.receiving.size > 0)
    if (conns.length === 0) return false
    const receiver = prng.oneOf(gen, conns)
    const [sender, messages] = prng.oneOf(gen, Array.from(receiver.receiving))
    const m = messages.shift()
    if (messages.length === 0) receiver.receiving.delete(sender)
    if (m.kind === 'step1') {
      const name = `r${replySeq++}`
      emit({ do: 'reply', doc: receiver.name, sv: m.sv, name })
      receive(sender, { kind: 'step2', msg: name }, receiver)
    } else if (m.kind === 'step2') {
      emit({ do: 'apply', doc: receiver.name, update: m.msg })
    } else {
      emit({ do: 'apply', doc: receiver.name, update: { from: sender.name, index: m.index } })
    }
    return true
  }
  const flushAllMessages = () => {
    let didSomething = false
    while (flushRandomMessage()) didSomething = true
    return didSomething
  }
  const disconnectRandom = () => {
    if (onlineConns.size === 0) return false
    disconnect(prng.oneOf(gen, Array.from(onlineConns)))
    return true
  }
  const reconnectRandom = () => {
    const reconnectable = Array.from(allConns).filter(c => !onlineConns.has(c))
    if (reconnectable.length === 0) return false
    connect(prng.oneOf(gen, reconnectable))
    return true
  }

  // A local transaction (origin not the delivery) publishes its update to every online client. That includes the transactions
  // y-text runs by itself after applying a remote change (format cleanup), so publishing hangs off the update event, not the step.
  const pending = []
  for (const inst of insts) {
    session.doc(inst.name).on(enc === 'v2' ? 'updateV2' : 'update', (_u, origin) => {
      if (origin !== 'deliver') pending.push({ inst, index: session.ctx.updates.get(inst.name)[enc].length - 1 })
    })
  }
  const publish = () => {
    for (const { inst, index } of pending.splice(0)) broadcast(inst, { kind: 'update', index })
  }
  const edit = (_inst, steps) => {
    for (const step of steps) {
      session.push(step)
      publish()
    }
  }

  const viewOf = inst => {
    const doc = session.doc(inst.name)
    return {
      length: () => (kind === 'array' ? doc.getArray('array').length : doc.getText('text').length),
      stringLength: () => doc.getText('text').toString().length,
      nestedArrayLength: i => {
        const v = doc.getArray('array').get(i)
        return v instanceof Y.Array ? v.length : -1
      }
    }
  }

  // init: the users are created in order (each connects as it is created), then syncAll
  for (const inst of insts) create(inst)
  for (const inst of allConns) connect(inst)
  flushAllMessages()

  // applyRandomTests
  for (let i = 0; i < iterations; i++) {
    if (prng.int32(gen, 0, 100) <= 2) {
      if (prng.bool(gen)) disconnectRandom()
      else reconnectRandom()
    } else if (prng.int32(gen, 0, 100) <= 1) {
      flushAllMessages()
    } else if (prng.int32(gen, 0, 100) <= 50) {
      flushRandomMessage()
    }
    const user = insts[prng.int32(gen, 0, insts.length - 1)]
    const modIndex = prng.int31(gen, 0, mods.length - 1) // prng.oneOf(gen, mods)
    if (collect) edit(user, tapped(collect, gen, modIndex, mods[modIndex], user.name, viewOf(user), counters))
    else edit(user, mods[modIndex](gen, user.name, viewOf(user), counters))
  }

  // compare: reconnect and flush everything
  for (const inst of insts) connect(inst)
  flushAllMessages()

  return { script: { replicas: session.script.replicas, steps: mergeDeliveries(session.script.steps) }, encoding: enc, session }
}

/**
 * Runs one plan while recording what it saw: the draws it consumed, the answers the document view gave, the steps it produced
 * (in stored form) and the payload counters before it ran. tools/fixture-gen/sim-plan-goldens.mjs turns these into goldens that
 * the C# plans replay without any document.
 */
const tapped = (collect, gen, modIndex, plan, doc, view, counters) => {
  const draws = []
  const calls = []
  const before = { ...counters }
  const tap = { next () { const v = gen.next(); draws.push(v); return v } }
  const spy = Object.fromEntries(Object.keys(view).map(name => [name, (...args) => {
    const result = view[name](...args)
    calls.push({ name, args, result })
    return result
  }]))
  const steps = plan(tap, doc, spy, counters)
  collect.push({ mod: modIndex, counters: before, draws, calls, steps: normalize({ steps }).steps })
  return steps
}

/** Adjacent `deliver` steps of one encoding become one step; the schedule is the concatenation. */
export const mergeDeliveries = steps => {
  const out = []
  for (const s of steps) {
    const prev = out[out.length - 1]
    if (s.step === 'deliver' && prev?.step === 'deliver' && prev.encoding === s.encoding) prev.schedule = [...prev.schedule, ...s.schedule]
    else out.push(s.step === 'deliver' ? { ...s, schedule: [...s.schedule] } : s)
  }
  return out
}
