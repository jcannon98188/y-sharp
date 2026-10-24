// Negative observations (ADR 0011): per-offset truncation outcomes and stored malformed inputs, both recorded from the oracle.
import { Y } from './oracle.mjs'
import { tag, hex, unhex } from './tagged.mjs'
import { dumpDoc, hashOf } from './observe.mjs'

const apply = (bytes, encoding) => {
  const doc = new Y.Doc()
  try {
    (encoding === 'v2' ? Y.applyUpdateV2 : Y.applyUpdate)(doc, bytes)
    return { doc }
  } catch (e) {
    return { doc, error: e?.constructor?.name ?? typeof e }
  }
}

const apiOf = enc => (enc === 'v2' ? 'Y.applyUpdateV2' : 'Y.applyUpdate')

/**
 * Cuts `full` at every proper-prefix offset and records the oracle's outcome, run-length encoded.
 * ok = applies and reaches the same doc as the full update; lenient-accepts = applies to a different doc (hash stored);
 * throws = the oracle raised (error class stored). Partial application on failure is real, so a throw also stores no doc.
 */
export const truncationOutcomes = (full, encoding, roots) => {
  const fullDoc = apply(full, encoding).doc
  const fullHash = hashOf(dumpDoc(fullDoc, roots))
  const runs = []
  for (let cut = 0; cut < full.length; cut++) {
    const r = apply(full.slice(0, cut), encoding)
    let entry
    if (r.error) entry = { outcome: 'throws', error: r.error }
    else {
      const h = hashOf(dumpDoc(r.doc, roots))
      entry = h === fullHash ? { outcome: 'ok' } : { outcome: 'lenient-accepts', doc: h }
    }
    const last = runs[runs.length - 1]
    if (last && last.outcome === entry.outcome && last.error === entry.error && last.doc === entry.doc && last.to === cut - 1) last.to = cut
    else runs.push({ from: cut, to: cut, ...entry })
  }
  return { api: apiOf(encoding), value: { encoding, length: full.length, fullDoc: fullHash, cuts: runs } }
}

/**
 * Malformed inputs. A case: {name, encoding, bytes, roots?, classification?, divergence?, jsCrash?, note?}.
 * The oracle's outcome is recorded (never asserted); classification ties it to ADR 0010: match (y-sharp must also reject
 * or accept), diverge (needs a registered divergence id) or js-crash (the oracle cannot be run on it safely).
 */
export const malformedCases = cases => {
  const value = cases.map(c => {
    const encoding = c.encoding ?? 'v1'
    const bytes = typeof c.bytes === 'string' ? unhex(c.bytes) : c.bytes
    const out = { name: c.name, encoding, bytes: tag(bytes) }
    if (c.note) out.note = c.note
    if (c.jsCrash) {
      Object.assign(out, { outcome: 'js-crash', reason: c.jsCrash, classification: 'js-crash' })
    } else {
      const roots = c.roots ?? []
      const r = apply(bytes, encoding)
      out.outcome = r.error ? 'throws' : 'ok'
      if (r.error) out.error = r.error
      out.pendingStructs = r.doc.store.pendingStructs !== null
      out.pendingDs = r.doc.store.pendingDs !== null
      out.sv = tag(Y.encodeStateVector(r.doc))
      out.doc = dumpDoc(r.doc, new Map(roots))
      out.classification = c.classification ?? 'match'
    }
    if (c.divergence) out.divergence = c.divergence
    if (out.classification === 'diverge' && !c.divergence) throw new Error(`malformed case '${c.name}': diverge needs a divergence id`)
    return out
  })
  return { api: 'Y.applyUpdate, Y.applyUpdateV2 on a fresh Doc', value }
}

export { hex }
