// `applyForeign` (ADR 0012, 0016): apply bytes that another implementation produced to a replica. In the live differential harness the
// bytes come from y-sharp (or from the oracle, replayed on y-sharp's side), so a script carrying this step is the same literal script
// on both interpreters. Without `name` a rejected update is a script error, like applyUpdate; with `name` the outcome is recorded under
// the `results` observation as `{accepted}` so the harness can classify mutated updates (both accept, both reject, or a divergence).
// The origin is the string "foreign", which observers can tell apart from local edits and from `applyUpdate` (null origin).
// A named result also carries `readPastEnd: true` when an accepted update was only decodable because a lib0 reader returned its default
// past the end of its buffer (a column or the rest buffer; ENC-60..68). y-sharp rejects those (ADR 0005), and the harness's
// lenient-column-end normaliser claims exactly that case. A rejected update carries `zeroLengthDeleteRange: true` when Yjs threw only
// because it re-encoded a parked zero-length delete range (UpdateEncoderV2.writeDsLen, ENC-51), which y-sharp ignores by ADR 0003.
// An accepted update carries `unsortedPendingDeleteSet: true` when Yjs parked delete ranges as pending state and a range of one client
// starts before the previous one ends: the V2 re-encode then writes a negative clock difference, which lib0 turns into garbage (ENC-86).
// An accepted update carries `selfDependentPending: true` when Yjs parked a struct that depends on its own id or a later clock of its own
// client (origin, right origin or parent id): it can never integrate, and y-sharp rejects the update as Malformed (Q15, ADR 0005).
// An accepted update carries `parentNotAType: true` when one of its structs names a parent id whose item holds content that is not a type:
// Yjs reads `content.type` as undefined and integrates the struct as a GC, where y-sharp rejects that struct (STR-71, ADR 0005).
// The flags are absent otherwise, so other results keep their shape.
import { Y, lib0 } from './oracle.mjs'
import { untag, ScriptError } from './tagged.mjs'
import { defineStep } from './steps.mjs'

defineStep({
  kind: 'applyForeign',
  required: ['doc', 'bytes'],
  optional: ['encoding', 'name'],
  values: ['bytes'],
  run: (ctx, s) => {
    const enc = s.encoding ?? 'v1'
    if (enc !== 'v1' && enc !== 'v2') throw new ScriptError(`unknown encoding '${enc}'`)
    const apply = enc === 'v1' ? Y.applyUpdate : Y.applyUpdateV2
    const doc = ctx.doc(s.doc)
    if (s.name === undefined) return apply(doc, untag(s.bytes), 'foreign')
    if (ctx.results.has(s.name)) throw new ScriptError(`applyForeign: result '${s.name}' is recorded twice`)
    // Y.applyUpdate(V2) inlined, so the decoders can be inspected afterwards.
    const decoder = lib0.decoding.createDecoder(untag(s.bytes))
    let structDecoder = null
    let accepted = true
    let zeroLengthDeleteRange = false
    const nonTypeParent = parentNotAType(doc, untag(s.bytes), enc)
    const order = { unsorted: false }
    try {
      structDecoder = enc === 'v1' ? new Y.UpdateDecoderV1(decoder) : new Y.UpdateDecoderV2(decoder)
      watchDeleteSetOrder(structDecoder, order)
      Y.readUpdateV2(decoder, doc, 'foreign', structDecoder)
    } catch (e) {
      accepted = false
      if (e instanceof Error && e.message === 'Unexpected case' && /\bwriteDsLen\b/.test(e.stack ?? '')) zeroLengthDeleteRange = true
    }
    const value = accepted && readPastEnd(structDecoder)
      ? { accepted, readPastEnd: true }
      : zeroLengthDeleteRange ? { accepted, zeroLengthDeleteRange: true }
        : accepted && order.unsorted && doc.store.pendingDs !== null ? { accepted, unsortedPendingDeleteSet: true }
          : accepted && selfDependentPending(doc) ? { accepted, selfDependentPending: true }
            : accepted && nonTypeParent ? { accepted, parentNotAType: true } : { accepted }
    ctx.results.set(s.name, { api: enc === 'v1' ? 'Y.applyUpdate (foreign bytes)' : 'Y.applyUpdateV2 (foreign bytes)', value })
  }
})

// The parked structs are a V2 update Yjs wrote itself, so the lazy decoder reads them faithfully.
const selfDependentPending = (doc) => {
  const pending = doc.store.pendingStructs
  if (pending === null) return false
  const ahead = (dep, id) => dep instanceof Y.ID && dep.client === id.client && dep.clock >= id.clock
  return Y.decodeUpdateV2(pending.update).structs.some(s => s instanceof Y.Item && (ahead(s.origin, s.id) || ahead(s.rightOrigin, s.id) || ahead(s.parent, s.id)))
}

// Read before the update applies: a parent is looked up among the update's own structs, then in the document. A parent that is a GC, or
// ContentDeleted, has no type in Yjs either and y-sharp makes the same GC, so only other content counts. A malformed update decodes to nothing.
const parentNotAType = (doc, bytes, enc) => {
  let structs
  try {
    structs = (enc === 'v1' ? Y.decodeUpdate(bytes) : Y.decodeUpdateV2(bytes)).structs
  } catch {
    return false
  }
  const find = (id) => {
    const own = structs.find(s => s.id.client === id.client && s.id.clock <= id.clock && id.clock < s.id.clock + s.length)
    if (own !== undefined) return own
    const list = doc.store.clients.get(id.client)
    if (list === undefined || id.clock >= Y.getState(doc.store, id.client)) return undefined
    return list[Y.findIndexSS(list, id.clock)]
  }
  return structs.some(s => {
    if (!(s instanceof Y.Item) || !(s.parent instanceof Y.ID)) return false
    const parent = find(s.parent)
    return parent instanceof Y.Item && !(parent.content instanceof Y.ContentType) && !(parent.content instanceof Y.ContentDeleted)
  })
}

// readAndApplyDeleteSet reads each client's ranges after resetDsCurVal; a range starting before the previous one ends is out of order.
const watchDeleteSetOrder = (structDecoder, order) => {
  const reset = structDecoder.resetDsCurVal.bind(structDecoder)
  const readClock = structDecoder.readDsClock.bind(structDecoder)
  const readLen = structDecoder.readDsLen.bind(structDecoder)
  let end = 0
  let clock = 0
  structDecoder.resetDsCurVal = () => { end = 0; reset() }
  structDecoder.readDsClock = () => { clock = readClock(); if (clock < end) order.unsorted = true; return clock }
  structDecoder.readDsLen = () => { const len = readLen(); end = clock + len; return len }
}

// lib0 readers do not check bounds: past the end readUint8 yields undefined (0 in a varint) and a StringDecoder slices an empty string,
// leaving pos/spos beyond the buffer. Any reader reachable from the update decoder in that state read past its end.
const readPastEnd = (root) => {
  const seen = new Set()
  const visit = (o) => {
    if (o === null || typeof o !== 'object' || seen.has(o) || ArrayBuffer.isView(o)) return false
    seen.add(o)
    if (o.arr instanceof Uint8Array && typeof o.pos === 'number' && o.pos > o.arr.length) return true
    if (typeof o.str === 'string' && typeof o.spos === 'number' && o.spos > o.str.length) return true
    return Object.values(o).some(visit)
  }
  return visit(root)
}
