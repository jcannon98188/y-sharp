// Helpers for scenarios whose steps depend on oracle state (random families, update bytes used as later inputs).
// They replay through the same interpreter; the resulting steps are stored literally, so replay never needs the PRNG.
import { Ctx, run, interpret } from './steps.mjs'
import { normalize, normalizeStep } from './script.mjs'
import { Y } from './oracle.mjs'

/** Builds a script step by step against a live oracle run (for lengths, state vectors, update bytes). */
export class Session {
  constructor (replicas) {
    this.replicas = replicas
    this.ctx = new Ctx()
    this.steps = []
    for (const r of replicas) run(this.ctx, { step: 'replica', ...r })
  }

  push (raw) {
    run(this.ctx, normalizeStep(raw))
    this.steps.push(raw)
    return this
  }

  doc (name) { return this.ctx.doc(name) }
  get script () { return { replicas: this.replicas, steps: this.steps } }
}

/** Full update bytes (V1 or V2) the named replica holds after running `raw`. */
export const stateAfter = (raw, replica, encoding = 'v1') => {
  const doc = interpret(normalize(raw)).docs.get(replica)
  return encoding === 'v2' ? Y.encodeStateAsUpdateV2(doc) : Y.encodeStateAsUpdate(doc)
}

/** Update-event bytes the named replica emitted, in order. */
export const updatesOf = (raw, replica, encoding = 'v1') => interpret(normalize(raw)).updates.get(replica)[encoding]
