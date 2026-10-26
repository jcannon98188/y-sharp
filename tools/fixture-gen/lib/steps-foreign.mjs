// `applyForeign` (ADR 0012, 0016): apply bytes that another implementation produced to a replica. In the live differential harness the
// bytes come from y-sharp (or from the oracle, replayed on y-sharp's side), so a script carrying this step is the same literal script
// on both interpreters. Without `name` a rejected update is a script error, like applyUpdate; with `name` the outcome is recorded under
// the `results` observation as `{accepted}` so the harness can classify mutated updates (both accept, both reject, or a divergence).
// The origin is the string "foreign", which observers can tell apart from local edits and from `applyUpdate` (null origin).
// A named result also carries `readPastEnd: true` when an accepted update was only decodable because a lib0 reader returned its default
// past the end of its buffer (a column or the rest buffer; ENC-60..68). y-sharp rejects those (ADR 0005), and the harness's
// lenient-column-end normaliser claims exactly that case. The flag is absent otherwise, so other results keep their shape.
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
    try {
      structDecoder = enc === 'v1' ? new Y.UpdateDecoderV1(decoder) : new Y.UpdateDecoderV2(decoder)
      Y.readUpdateV2(decoder, doc, 'foreign', structDecoder)
    } catch (e) { accepted = false }
    const value = accepted && readPastEnd(structDecoder) ? { accepted, readPastEnd: true } : { accepted }
    ctx.results.set(s.name, { api: enc === 'v1' ? 'Y.applyUpdate (foreign bytes)' : 'Y.applyUpdateV2 (foreign bytes)', value })
  }
})

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
