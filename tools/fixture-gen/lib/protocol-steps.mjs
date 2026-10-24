// Protocol step kinds of ADR 0016 (Spec 9): awareness.*, sync.* and auth.*, replayed on the pinned y-protocols submodule.
// Time is virtual (Ctx.advance); the Awareness check interval is a virtual timer, so `tick` drives expiry and renewal.
// Results (bytes a writer produced, what a reader did) are recorded under the `results` observation; Awareness state,
// meta and the ordered event log under `awareness` and `events`.
import { syncProtocol, awarenessProtocol, authProtocol, lib0 } from './oracle.mjs'
import { tag, untag, ScriptError } from './tagged.mjs'
import { defineStep } from './steps.mjs'

const { encoding, decoding } = lib0

const registry = ctx => (ctx.awarenesses ??= new Map())
const awareness = (ctx, name) => {
  const a = registry(ctx).get(name)
  if (!a) throw new ScriptError(`unknown awareness '${name}'`)
  return a
}
const record = (ctx, name, api, bytes, extra) => ctx.results.set(name, { api, bytes, extra })
const priorBytes = (ctx, name) => {
  const r = ctx.results.get(name)
  if (!r?.bytes) throw new ScriptError(`no recorded bytes named '${name}'`)
  return r.bytes
}
const inputBytes = (ctx, s) => {
  if ((s.bytes === undefined) === (s.from === undefined)) throw new ScriptError(`${s.step}: exactly one of bytes and from is required`)
  return s.from !== undefined ? priorBytes(ctx, s.from) : untag(s.bytes)
}
const withPrefix = (ctx, s, write) => {
  const enc = encoding.createEncoder()
  if (s.append !== undefined) encoding.writeUint8Array(enc, priorBytes(ctx, s.append))
  write(enc)
  return encoding.toUint8Array(enc)
}
const attempt = f => {
  const realError = console.error
  console.error = () => {}
  try { return { value: f() } } catch (e) { return { error: e } } finally { console.error = realError }
}

defineStep({
  kind: 'awareness.new',
  required: ['awareness', 'doc'],
  run: (ctx, s) => {
    if (registry(ctx).has(s.awareness)) throw new ScriptError(`awareness '${s.awareness}' declared twice`)
    const realSet = globalThis.setInterval
    globalThis.setInterval = (fn, ms) => ctx.addTimer(fn, ms)
    let a
    try { a = new awarenessProtocol.Awareness(ctx.doc(s.doc)) } finally { globalThis.setInterval = realSet }
    registry(ctx).set(s.awareness, a)
    const log = event => (c, origin) => ctx.events.push({ observer: s.awareness, event, at: ctx.now, added: c.added, updated: c.updated, removed: c.removed, origin: tag(origin) })
    a.on('change', log('change'))
    a.on('update', log('update'))
    a.on('destroy', () => ctx.events.push({ observer: s.awareness, event: 'destroy', at: ctx.now }))
  }
})
defineStep({
  kind: 'awareness.setLocal',
  required: ['awareness', 'state'],
  values: ['state'],
  run: (ctx, s) => awareness(ctx, s.awareness).setLocalState(untag(s.state))
})
defineStep({
  kind: 'awareness.setLocalField',
  required: ['awareness', 'field', 'value'],
  values: ['value'],
  run: (ctx, s) => awareness(ctx, s.awareness).setLocalStateField(s.field, untag(s.value))
})
defineStep({
  kind: 'awareness.applyUpdate',
  required: ['awareness'],
  optional: ['bytes', 'from', 'origin', 'name'],
  values: ['bytes'],
  run: (ctx, s) => {
    const r = attempt(() => awarenessProtocol.applyAwarenessUpdate(awareness(ctx, s.awareness), inputBytes(ctx, s), s.origin ?? null))
    if (r.error && s.name === undefined) throw r.error
    if (s.name !== undefined) record(ctx, s.name, 'applyAwarenessUpdate', undefined, { threw: !!r.error })
  }
})
defineStep({
  kind: 'awareness.removeStates',
  required: ['awareness', 'clients'],
  optional: ['origin'],
  run: (ctx, s) => awarenessProtocol.removeAwarenessStates(awareness(ctx, s.awareness), s.clients, s.origin ?? null)
})
defineStep({
  kind: 'awareness.encode',
  required: ['name', 'awareness', 'clients'],
  optional: ['states'],
  values: ['states'],
  run: (ctx, s) => {
    const states = s.states === undefined ? undefined : new Map(untag(s.states).map(([id, st]) => [id, st]))
    const r = attempt(() => awarenessProtocol.encodeAwarenessUpdate(awareness(ctx, s.awareness), s.clients, states))
    record(ctx, s.name, 'encodeAwarenessUpdate', r.value, { threw: !!r.error })
  }
})
const modifiers = {
  identity: () => st => st,
  blank: () => () => null,
  setField: s => st => ({ ...st, [s.field]: untag(s.value) }),
  setFieldKeepNull: s => st => (st === null ? null : { ...st, [s.field]: untag(s.value) }),
  undefined: () => () => undefined
}
defineStep({
  kind: 'awareness.modify',
  required: ['name', 'mode'],
  optional: ['bytes', 'from', 'field', 'value'],
  values: ['bytes', 'value'],
  run: (ctx, s) => {
    const make = modifiers[s.mode]
    if (!make) throw new ScriptError(`awareness.modify: unknown mode '${s.mode}'`)
    const r = attempt(() => awarenessProtocol.modifyAwarenessUpdate(inputBytes(ctx, s), make(s)))
    record(ctx, s.name, 'modifyAwarenessUpdate', r.value, { threw: !!r.error })
  }
})
defineStep({
  kind: 'awareness.destroy',
  required: ['awareness'],
  run: (ctx, s) => {
    const a = awareness(ctx, s.awareness)
    const realClear = globalThis.clearInterval
    globalThis.clearInterval = t => ctx.clearTimer(t)
    try { a.destroy() } finally { globalThis.clearInterval = realClear }
  }
})

defineStep({
  kind: 'sync.step1',
  required: ['name', 'doc'],
  optional: ['append'],
  run: (ctx, s) => record(ctx, s.name, 'writeSyncStep1', withPrefix(ctx, s, enc => syncProtocol.writeSyncStep1(enc, ctx.doc(s.doc))))
})
defineStep({
  kind: 'sync.step2',
  required: ['name', 'doc'],
  optional: ['sv', 'append'],
  values: ['sv'],
  run: (ctx, s) => record(ctx, s.name, 'writeSyncStep2', withPrefix(ctx, s, enc => syncProtocol.writeSyncStep2(enc, ctx.doc(s.doc), s.sv === undefined ? undefined : untag(s.sv))))
})
defineStep({
  kind: 'sync.update',
  required: ['name', 'update'],
  optional: ['append'],
  values: ['update'],
  run: (ctx, s) => record(ctx, s.name, 'writeUpdate', withPrefix(ctx, s, enc => syncProtocol.writeUpdate(enc, untag(s.update))))
})
defineStep({
  kind: 'sync.read',
  required: ['name', 'doc'],
  optional: ['bytes', 'from', 'origin'],
  values: ['bytes'],
  run: (ctx, s) => {
    const input = inputBytes(ctx, s)
    const dec = decoding.createDecoder(input)
    const reply = encoding.createEncoder()
    let handlerCalled = false
    const r = attempt(() => syncProtocol.readSyncMessage(dec, reply, ctx.doc(s.doc), s.origin ?? null, () => { handlerCalled = true }))
    record(ctx, s.name, 'readSyncMessage', encoding.toUint8Array(reply), {
      type: r.error ? null : r.value,
      threw: !!r.error,
      handlerCalled,
      consumed: dec.pos
    })
  }
})

defineStep({
  kind: 'auth.permissionDenied',
  required: ['name', 'reason'],
  run: (ctx, s) => record(ctx, s.name, 'writePermissionDenied', withPrefix(ctx, s, enc => authProtocol.writePermissionDenied(enc, s.reason)))
})
