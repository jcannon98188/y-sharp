// The Node interpreter of ADR 0016: the only code that drives Yjs. It replays the stored (tagged) script, so what
// the fixture records is exactly what the oracle executed. Areas add kinds with defineStep() without touching the core.
import { Y, virtualClock } from './oracle.mjs'
import { tag, untag, ScriptError } from './tagged.mjs'
import { yvalueOut, deltaOut } from './observe.mjs'

export const steps = new Map()

/**
 * `values` lists the fields authored as JS values and stored as tagged values.
 * @param {{kind:string, required?:string[], optional?:string[], values?:string[], children?:boolean, run:(ctx:Ctx, step:any)=>void}} def
 */
export const defineStep = def => {
  if (steps.has(def.kind)) throw new Error(`step kind ${def.kind} registered twice`)
  steps.set(def.kind, { required: [], optional: [], values: [], children: false, ...def })
}

export class Ctx {
  constructor () {
    this.docs = new Map()
    this.updates = new Map()
    this.events = []
    this.results = new Map()
    this.depth = new Map()
    this.now = 1704067200000 // 2024-01-01T00:00:00Z, like the C# VirtualTimeProvider; any positive epoch works (UndoManager treats 0 as "no change yet")
    this.cleanups = []
    this.afterTick = null
    this.timers = []
    this.timerSeq = 0
  }

  /** Virtual setInterval: fires from advance(), in due order, never from real time. */
  addTimer (fn, ms) {
    const t = { fn, ms, due: this.now + ms, seq: ++this.timerSeq }
    this.timers.push(t)
    return t
  }

  clearTimer (t) { this.timers = this.timers.filter(x => x !== t) }

  advance (ms) {
    const target = this.now + ms
    for (;;) {
      const next = this.timers.filter(t => t.due <= target).sort((a, b) => a.due - b.due || a.seq - b.seq)[0]
      if (!next) break
      this.now = next.due
      next.due += next.ms
      next.seq = ++this.timerSeq
      next.fn()
    }
    this.now = target
  }

  doc (name) {
    const d = this.docs.get(name)
    if (!d) throw new ScriptError(`unknown replica '${name}'`)
    return d
  }
}

const rootGetters = {
  text: (d, n) => d.getText(n),
  array: (d, n) => d.getArray(n),
  map: (d, n) => d.getMap(n),
  xmlFragment: (d, n) => d.getXmlFragment(n),
  xmlText: (d, n) => d.get(n, Y.XmlText)
}

const resolveRoot = (doc, r) => {
  const get = rootGetters[r?.kind]
  if (!get || typeof r.root !== 'string') throw new ScriptError('a root address needs string root and a known kind')
  return get(doc, r.root)
}

export const resolveTarget = (doc, target) => {
  if (!Array.isArray(target)) return resolveRoot(doc, target)
  if (target.length === 0) throw new ScriptError('empty path')
  let cur = resolveRoot(doc, target[0])
  for (const seg of target.slice(1)) {
    const next = typeof seg === 'string'
      ? (cur instanceof Y.Map ? cur.get(seg) : undefined)
      : (cur instanceof Y.Array || cur instanceof Y.XmlFragment ? cur.get(seg) : undefined)
    if (!(next instanceof Y.AbstractType)) throw new ScriptError(`path segment ${JSON.stringify(seg)} does not resolve to a shared type`)
    cur = next
  }
  return cur
}

const target = (ctx, s, cls) => {
  const t = resolveTarget(ctx.doc(s.doc), s.target)
  if (cls && !(t instanceof cls)) throw new ScriptError(`${s.step}: target is not a ${cls.name}`)
  return t
}

const attrs = s => (s.attributes === undefined ? undefined : untag(s.attributes))

const makeType = s => {
  const init = s.init === undefined ? undefined : untag(s.init)
  switch (s.kind) {
    case 'text': return new Y.Text(init)
    case 'array': { const a = new Y.Array(); if (init) a.push(init); return a }
    case 'map': return new Y.Map(init === undefined ? undefined : Object.entries(init))
    default: throw new ScriptError(`insertType: unknown kind '${s.kind}'`)
  }
}

const makeXml = s => {
  switch (s.kind) {
    case 'element': return new Y.XmlElement(s.name)
    case 'text': return new Y.XmlText(s.init === undefined ? undefined : untag(s.init))
    default: throw new ScriptError(`xml.insert: unknown kind '${s.kind}'`)
  }
}

const inTransaction = (ctx, docName, origin, body) => {
  if ((ctx.depth.get(docName) ?? 0) > 0) return body()
  ctx.depth.set(docName, 1)
  try { ctx.doc(docName).transact(body, origin) } finally { ctx.depth.set(docName, 0) }
}

export const run = (ctx, s) => {
  const def = steps.get(s.step)
  if (!def) throw new ScriptError(`unknown step kind '${s.step}'`)
  def.run(ctx, s)
}

export const runSteps = (ctx, list) => { for (const s of list) run(ctx, s) }

const encoding = s => {
  const e = s.encoding ?? 'v1'
  if (e !== 'v1' && e !== 'v2') throw new ScriptError(`unknown encoding '${e}'`)
  return e
}

defineStep({
  kind: 'replica',
  required: ['name', 'clientId'],
  optional: ['gc'],
  run: (ctx, s) => {
    if (ctx.docs.has(s.name)) throw new ScriptError(`replica '${s.name}' declared twice`)
    const doc = new Y.Doc({ gc: s.gc ?? true })
    doc.clientID = s.clientId
    const rec = { v1: [], v2: [] }
    doc.on('update', u => rec.v1.push(u))
    doc.on('updateV2', u => rec.v2.push(u))
    ctx.docs.set(s.name, doc)
    ctx.updates.set(s.name, rec)
  }
})
defineStep({
  kind: 'transact',
  required: ['doc'],
  optional: ['origin'],
  children: true,
  run: (ctx, s) => inTransaction(ctx, s.doc, s.origin, () => runSteps(ctx, s.steps))
})
defineStep({ kind: 'tick', required: ['ms'], run: (ctx, s) => { ctx.advance(s.ms); ctx.afterTick?.() } })
defineStep({
  kind: 'sync',
  required: ['from', 'to'],
  run: (ctx, s) => {
    const from = ctx.doc(s.from)
    const to = ctx.doc(s.to)
    Y.applyUpdate(to, Y.encodeStateAsUpdate(from, Y.encodeStateVector(to)), 'sync')
  }
})
defineStep({
  kind: 'applyUpdate',
  required: ['doc', 'bytes'],
  optional: ['encoding'],
  values: ['bytes'],
  run: (ctx, s) => (encoding(s) === 'v1' ? Y.applyUpdate : Y.applyUpdateV2)(ctx.doc(s.doc), untag(s.bytes), null)
})
defineStep({
  kind: 'observe',
  required: ['doc', 'target', 'name'],
  run: (ctx, s) => {
    const t = target(ctx, s)
    t.observe(e => {
      const rec = { observer: s.name, type: e.constructor.name }
      if (t instanceof Y.Text || t instanceof Y.Array) rec.delta = deltaOut(e.delta)
      if (t instanceof Y.Map || t instanceof Y.XmlFragment || t instanceof Y.XmlText) {
        rec.keys = Array.from(e.changes.keys, ([k, c]) => [k, { action: c.action, ...(c.action === 'add' ? {} : { oldValue: yvalueOut(c.oldValue) }) }])
      }
      if (t instanceof Y.XmlFragment) {
        rec.childListChanged = e.childListChanged
        rec.attributesChanged = Array.from(e.attributesChanged).sort()
        rec.delta = deltaOut(e.changes.delta)
      }
      ctx.events.push(rec)
    })
  }
})

defineStep({
  kind: 'text.insert',
  required: ['doc', 'target', 'at', 'value'],
  optional: ['attributes'],
  values: ['value', 'attributes'],
  run: (ctx, s) => target(ctx, s, Y.Text).insert(s.at, untag(s.value), attrs(s))
})
defineStep({
  kind: 'text.insertEmbed',
  required: ['doc', 'target', 'at', 'value'],
  optional: ['attributes'],
  values: ['value', 'attributes'],
  run: (ctx, s) => target(ctx, s, Y.Text).insertEmbed(s.at, untag(s.value), attrs(s))
})
defineStep({
  kind: 'text.delete',
  required: ['doc', 'target', 'at', 'len'],
  run: (ctx, s) => target(ctx, s, Y.Text).delete(s.at, s.len)
})
defineStep({
  kind: 'text.format',
  required: ['doc', 'target', 'at', 'len', 'attributes'],
  values: ['attributes'],
  run: (ctx, s) => target(ctx, s, Y.Text).format(s.at, s.len, attrs(s))
})
defineStep({
  kind: 'text.applyDelta',
  required: ['doc', 'target', 'delta'],
  values: ['delta'],
  run: (ctx, s) => target(ctx, s, Y.Text).applyDelta(untag(s.delta))
})

defineStep({
  kind: 'array.insert',
  required: ['doc', 'target', 'at', 'values'],
  values: ['values'],
  run: (ctx, s) => target(ctx, s, Y.Array).insert(s.at, untag(s.values))
})
defineStep({
  kind: 'array.push',
  required: ['doc', 'target', 'values'],
  values: ['values'],
  run: (ctx, s) => target(ctx, s, Y.Array).push(untag(s.values))
})
defineStep({
  kind: 'array.unshift',
  required: ['doc', 'target', 'values'],
  values: ['values'],
  run: (ctx, s) => target(ctx, s, Y.Array).unshift(untag(s.values))
})
defineStep({
  kind: 'array.delete',
  required: ['doc', 'target', 'at'],
  optional: ['len'],
  run: (ctx, s) => target(ctx, s, Y.Array).delete(s.at, s.len ?? 1)
})
defineStep({
  kind: 'array.insertType',
  required: ['doc', 'target', 'at', 'kind'],
  optional: ['init'],
  values: ['init'],
  run: (ctx, s) => target(ctx, s, Y.Array).insert(s.at, [makeType(s)])
})

defineStep({
  kind: 'map.set',
  required: ['doc', 'target', 'key', 'value'],
  values: ['value'],
  run: (ctx, s) => target(ctx, s, Y.Map).set(s.key, untag(s.value))
})
defineStep({
  kind: 'map.delete',
  required: ['doc', 'target', 'key'],
  run: (ctx, s) => target(ctx, s, Y.Map).delete(s.key)
})
defineStep({
  kind: 'map.clear',
  required: ['doc', 'target'],
  run: (ctx, s) => target(ctx, s, Y.Map).clear()
})
defineStep({
  kind: 'map.insertType',
  required: ['doc', 'target', 'key', 'kind'],
  optional: ['init'],
  values: ['init'],
  run: (ctx, s) => target(ctx, s, Y.Map).set(s.key, makeType(s))
})

defineStep({
  kind: 'xml.insert',
  required: ['doc', 'target', 'at', 'kind'],
  optional: ['name', 'init'],
  values: ['init'],
  run: (ctx, s) => target(ctx, s, Y.XmlFragment).insert(s.at, [makeXml(s)])
})
defineStep({
  kind: 'xml.delete',
  required: ['doc', 'target', 'at'],
  optional: ['len'],
  run: (ctx, s) => target(ctx, s, Y.XmlFragment).delete(s.at, s.len ?? 1)
})
defineStep({
  kind: 'xml.setAttribute',
  required: ['doc', 'target', 'name', 'value'],
  values: ['value'],
  run: (ctx, s) => {
    const t = target(ctx, s)
    if (!(t instanceof Y.XmlElement || t instanceof Y.XmlText)) throw new ScriptError('xml.setAttribute: target is not an element or an xml text')
    t.setAttribute(s.name, untag(s.value))
  }
})
defineStep({
  kind: 'xml.removeAttribute',
  required: ['doc', 'target', 'name'],
  run: (ctx, s) => {
    const t = target(ctx, s)
    if (!(t instanceof Y.XmlElement || t instanceof Y.XmlText)) throw new ScriptError('xml.removeAttribute: target is not an element or an xml text')
    t.removeAttribute(s.name)
  }
})

// Pure update functions (ADR 0016): the result is recorded under the `results` observation, keyed by `name`.
const pure = (ctx, s, api, bytes) => ctx.results.set(s.name, { api, bytes })
defineStep({
  kind: 'mergeUpdates',
  required: ['name', 'updates'],
  optional: ['encoding'],
  values: ['updates'],
  run: (ctx, s) => {
    const v1 = encoding(s) === 'v1'
    pure(ctx, s, v1 ? 'Y.mergeUpdates' : 'Y.mergeUpdatesV2', (v1 ? Y.mergeUpdates : Y.mergeUpdatesV2)(untag(s.updates)))
  }
})
defineStep({
  kind: 'diffUpdate',
  required: ['name', 'update', 'sv'],
  optional: ['encoding'],
  values: ['update', 'sv'],
  run: (ctx, s) => {
    const v1 = encoding(s) === 'v1'
    pure(ctx, s, v1 ? 'Y.diffUpdate' : 'Y.diffUpdateV2', (v1 ? Y.diffUpdate : Y.diffUpdateV2)(untag(s.update), untag(s.sv)))
  }
})
defineStep({
  kind: 'convertUpdate',
  required: ['name', 'update', 'from'],
  values: ['update'],
  run: (ctx, s) => {
    if (s.from !== 'v1' && s.from !== 'v2') throw new ScriptError(`unknown encoding '${s.from}'`)
    const v1 = s.from === 'v1'
    pure(ctx, s, v1 ? 'Y.convertUpdateFormatV1ToV2' : 'Y.convertUpdateFormatV2ToV1', (v1 ? Y.convertUpdateFormatV1ToV2 : Y.convertUpdateFormatV2ToV1)(untag(s.update)))
  }
})

/** Replays a stored script. Date.now is virtual (advanced by `tick`) so time-dependent features are deterministic. */
export const interpret = script => {
  const ctx = new Ctx()
  // Date.now is virtual while a script runs (lib0 reads it through the shim installed in oracle.mjs)
  virtualClock.now = () => ctx.now
  try {
    for (const r of script.replicas ?? []) run(ctx, { step: 'replica', ...r })
    runSteps(ctx, script.steps ?? [])
  } finally {
    virtualClock.now = null
    for (const f of ctx.cleanups) f()
  }
  return ctx
}
