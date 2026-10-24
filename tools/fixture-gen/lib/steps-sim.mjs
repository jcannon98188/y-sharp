// Step kinds of ADR 0016 that Spec 10 adds: `deliver {schedule}` (the delivery schedule of simulation op logs), `observeDeep`
// (the deep-observer recorder behind Q25) and `text.insertEmbedType` (a shared type as a text embed, which the Quill generator
// of the upstream randomized tests uses). They live in their own module so the core interpreter (steps.mjs) stays untouched.
import { Y } from './oracle.mjs'
import { untag, ScriptError } from './tagged.mjs'
import { defineStep, resolveTarget } from './steps.mjs'

/** Messages of a `deliver` schedule: state vectors captured by name and the replies computed from them, per run. */
const mailbox = ctx => (ctx.mail ??= { svs: new Map(), msgs: new Map() })

const format = s => {
  const e = s.encoding ?? 'v1'
  if (e !== 'v1' && e !== 'v2') throw new ScriptError(`unknown encoding '${e}'`)
  return e
}

const need = (map, name, what) => {
  const v = map.get(name)
  if (v === undefined) throw new ScriptError(`deliver: ${what} '${name}' was never captured`)
  return v
}

const fresh = (map, name, what) => {
  if (map.has(name)) throw new ScriptError(`deliver: ${what} '${name}' is captured twice`)
}

/**
 * Delivery schedule (ADR 0016): an ordered list of entries run in sequence on the live replicas.
 *   {do:'sv',    doc, name}              capture the state vector of `doc` now (a SyncStep1 message)
 *   {do:'reply', doc, sv, name}          capture encodeStateAsUpdate(doc, <captured sv>) now (a SyncStep2 reply)
 *   {do:'apply', doc, update}            apply a captured reply (`update` is its name) or the `index`-th update event of
 *                                        replica `from` (`update` is {from, index}) to `doc`
 * Applying the same entry twice duplicates a delivery, leaving one out drops it, listing them in another order reorders them.
 * `encoding` (default v1) selects the update format of replies and of the update events indexed by `{from, index}`.
 */
defineStep({
  kind: 'deliver',
  required: ['schedule'],
  optional: ['encoding'],
  run: (ctx, s) => {
    const enc = format(s)
    const v2 = enc === 'v2'
    const { svs, msgs } = mailbox(ctx)
    for (const e of s.schedule) {
      switch (e.do) {
        case 'sv':
          fresh(svs, e.name, 'state vector')
          svs.set(e.name, Y.encodeStateVector(ctx.doc(e.doc)))
          break
        case 'reply': {
          fresh(msgs, e.name, 'reply')
          const sv = need(svs, e.sv, 'state vector')
          msgs.set(e.name, (v2 ? Y.encodeStateAsUpdateV2 : Y.encodeStateAsUpdate)(ctx.doc(e.doc), sv))
          break
        }
        case 'apply': {
          let bytes
          if (typeof e.update === 'string') bytes = need(msgs, e.update, 'reply')
          else {
            const list = ctx.updates.get(e.update.from)
            if (!list) throw new ScriptError(`deliver: unknown replica '${e.update.from}'`)
            bytes = list[enc][e.update.index]
            if (bytes === undefined) throw new ScriptError(`deliver: replica '${e.update.from}' has no update event #${e.update.index}`)
          }
          (v2 ? Y.applyUpdateV2 : Y.applyUpdate)(ctx.doc(e.doc), bytes, 'deliver')
          break
        }
        default:
          throw new ScriptError(`deliver: unknown entry '${e.do}'`)
      }
    }
  }
})

/** Deep observer: records, per callback, the type and the path of each event in the order the callback received them (Q25). */
defineStep({
  kind: 'observeDeep',
  required: ['doc', 'target', 'name'],
  run: (ctx, s) => {
    resolveTarget(ctx.doc(s.doc), s.target).observeDeep(events => {
      ctx.events.push({ observer: s.name, deep: true, events: events.map(e => ({ type: e.constructor.name, path: e.path })) })
    })
  }
})

const embedType = s => {
  const init = s.init === undefined ? undefined : untag(s.init)
  switch (s.kind) {
    case 'text': return new Y.Text(init)
    case 'array': { const a = new Y.Array(); if (init) a.push(init); return a }
    case 'map': return new Y.Map(init === undefined ? undefined : Object.entries(init))
    default: throw new ScriptError(`text.insertEmbedType: unknown kind '${s.kind}'`)
  }
}

defineStep({
  kind: 'text.insertEmbedType',
  required: ['doc', 'target', 'at', 'kind'],
  optional: ['init', 'attributes'],
  values: ['init', 'attributes'],
  run: (ctx, s) => {
    const t = resolveTarget(ctx.doc(s.doc), s.target)
    if (!(t instanceof Y.Text)) throw new ScriptError('text.insertEmbedType: target is not a Y.Text')
    t.insertEmbed(s.at, embedType(s), s.attributes === undefined ? undefined : untag(s.attributes))
  }
})
