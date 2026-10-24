// Authoring helpers: scenarios write steps with plain JS values; normalize() stores them as tagged values.
import { steps } from './steps.mjs'
import { tag, ScriptError } from './tagged.mjs'

export const text = root => ({ root, kind: 'text' })
export const array = root => ({ root, kind: 'array' })
export const map = root => ({ root, kind: 'map' })
export const xmlFragment = root => ({ root, kind: 'xmlFragment' })
export const replica = (name, clientId, gc) => (gc === undefined ? { name, clientId } : { name, clientId, gc })

export const normalizeStep = s => {
  const def = steps.get(s.step)
  if (!def) throw new ScriptError(`unknown step kind '${s.step}'`)
  const out = { step: s.step }
  const allowed = new Set([...def.required, ...def.optional, ...(def.children ? ['steps'] : [])])
  for (const k of Object.keys(s)) if (k !== 'step' && !allowed.has(k)) throw new ScriptError(`${s.step}: unknown field '${k}'`)
  for (const k of [...def.required, ...def.optional]) {
    if (s[k] === undefined && !def.values.includes(k)) {
      if (def.required.includes(k)) throw new ScriptError(`${s.step}: missing field '${k}'`)
      continue
    }
    if (s[k] === undefined && !def.required.includes(k)) continue
    out[k] = def.values.includes(k) ? tag(s[k]) : s[k]
  }
  if (def.children) out.steps = s.steps.map(normalizeStep)
  return out
}

/** Authoring script `{replicas, steps}` to the stored `schema: 1` script. */
export const normalize = raw => {
  const script = { schema: 1 }
  if (raw.replicas?.length) script.replicas = raw.replicas
  script.steps = (raw.steps ?? []).map(normalizeStep)
  return script
}

/** Every `{root, kind}` address a script mentions, so each replica's dump covers the same roots. */
export const rootsOf = script => {
  const roots = new Map()
  const add = r => {
    if (roots.has(r.root) && roots.get(r.root) !== r.kind) throw new ScriptError(`root '${r.root}' used as both ${roots.get(r.root)} and ${r.kind}`)
    roots.set(r.root, r.kind)
  }
  const walk = list => {
    for (const s of list) {
      if (s.target) add(Array.isArray(s.target) ? s.target[0] : s.target)
      if (s.steps) walk(s.steps)
    }
  }
  walk(script.steps)
  return roots
}
