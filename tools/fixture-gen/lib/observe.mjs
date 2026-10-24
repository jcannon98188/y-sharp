// Observations (ADR 0011/0016): what the oracle did, as tagged values and typed dumps. Each observation names its
// producing API because V1 and V2 bytes differ by API. Dumps are read by TestKit; the schema is normative.
import { createHash } from 'node:crypto'
import { Y, encodeStateVectorV2 } from './oracle.mjs'
import { tag } from './tagged.mjs'

/** A value as it appears in a dump: a tagged value, or a typed dump for a shared type. */
export const yvalueOut = v => {
  if (v instanceof Y.AbstractType) return dumpType(v)
  if (Array.isArray(v)) return v.map(yvalueOut)
  return tag(v)
}

const attrsOut = a => (a === undefined ? undefined : tag(a))

/** Delta of a text or array event; embeds and nested types appear as dumps. */
export const deltaOut = delta => delta.map(d => {
  const o = {}
  if ('insert' in d) o.insert = yvalueOut(d.insert)
  if ('retain' in d) o.retain = d.retain
  if ('delete' in d) o.delete = d.delete
  if (d.attributes !== undefined) o.attributes = attrsOut(d.attributes)
  return o
})

const liveEntries = t => Array.from(t._map.entries()).filter(([, item]) => !item.deleted)
  .map(([k, item]) => [k, item.content.getContent()[item.length - 1]])

const attrPairs = t => liveEntries(t).map(([k, v]) => [k, yvalueOut(v)])

export const dumpType = t => {
  if (t instanceof Y.XmlText) return { kind: 'xmlText', attrs: attrPairs(t), delta: deltaOut(t.toDelta()) }
  if (t instanceof Y.Text) return { kind: 'text', delta: deltaOut(t.toDelta()) }
  if (t instanceof Y.Array) return { kind: 'array', items: t.toArray().map(yvalueOut) }
  if (t instanceof Y.Map) return { kind: 'map', pairs: Array.from(t.entries(), ([k, v]) => [k, yvalueOut(v)]) }
  if (t instanceof Y.XmlElement) return { kind: 'xmlElement', name: t.nodeName, attrs: attrPairs(t), children: t.toArray().map(dumpType) }
  if (t instanceof Y.XmlFragment) return { kind: 'xmlFragment', attrs: [], children: t.toArray().map(dumpType) }
  return { kind: 'unsupported', type: t.constructor.name }
}

const rootDump = (doc, name, kind) => dumpType({
  text: () => doc.getText(name),
  array: () => doc.getArray(name),
  map: () => doc.getMap(name),
  xmlFragment: () => doc.getXmlFragment(name)
}[kind]())

/** Typed tree of every root in sorted root-name order, as ordered pairs. */
export const dumpDoc = (doc, roots) => Array.from(roots).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
  .map(([name, kind]) => [name, rootDump(doc, name, kind)])

export const hashOf = json => createHash('sha256').update(JSON.stringify(json)).digest('hex')

const idOut = id => (id ? [id.client, id.clock] : undefined)

const contentOut = c => {
  const ref = c.getRef()
  switch (ref) {
    case 0: return { ref, value: c.len }
    case 2: return { ref, value: tag(c.arr) }
    case 3: return { ref, value: tag(c.content) }
    case 4: return { ref, value: tag(c.str) }
    case 5: return { ref, value: tag(c.embed) }
    case 6: return { ref, value: [c.key, tag(c.value)] }
    case 7: return { ref, value: c.type.constructor.name }
    case 8: return { ref, value: tag(c.arr) }
    case 9: return { ref, value: [c.doc.guid, tag(c.opts)] }
    default: return { ref, value: null }
  }
}

const parentOut = (doc, item) => {
  const p = item.parent
  if (p === null) return null
  if (p._item) return idOut(p._item.id)
  for (const [k, v] of doc.share) if (v === p) return k
  return null
}

/** Canonical struct-store dump: clients in descending id order, structs in clock order. */
export const dumpStructs = doc => Array.from(doc.store.clients.entries()).sort(([a], [b]) => b - a).map(([client, list]) => [client, list.map(s => {
  const base = { id: [s.id.client, s.id.clock], len: s.length }
  if (s instanceof Y.GC) return { ...base, kind: 'gc' }
  if (s instanceof Y.Skip) return { ...base, kind: 'skip' }
  const o = { ...base, kind: 'item' }
  if (s.origin) o.origin = idOut(s.origin)
  if (s.rightOrigin) o.rightOrigin = idOut(s.rightOrigin)
  o.parent = parentOut(doc, s)
  if (s.parentSub !== null) o.parentSub = s.parentSub
  o.content = contentOut(s.content)
  o.deleted = s.deleted
  o.keep = s.keep
  return o
})])

/** Delete set as sorted, merged `[client, [[clock, len], ...]]`. */
export const dumpDs = doc => {
  const ds = Y.createDeleteSetFromStructStore(doc.store)
  return Array.from(ds.clients.entries()).sort(([a], [b]) => a - b).map(([client, items]) => {
    const sorted = items.map(d => [d.clock, d.len]).sort((x, y) => x[0] - y[0])
    const merged = []
    for (const [c, l] of sorted) {
      const last = merged[merged.length - 1]
      if (last && last[0] + last[1] >= c) last[1] = Math.max(last[1], c + l - last[0])
      else merged.push([c, l])
    }
    return [client, merged]
  })
}

const bytes = u => tag(u)

/** Builds the named observations for a finished run. `extra` selects the opt-in ones ('structs', 'ds'). */
export const observe = (ctx, roots, extra = []) => {
  const names = Array.from(ctx.docs.keys())
  const perReplica = f => Object.fromEntries(names.map(n => [n, f(ctx.docs.get(n), n)]))
  const obs = {}
  obs.updates = {
    api: "Doc.on('update'), Doc.on('updateV2')",
    value: perReplica((_, n) => ({ v1: ctx.updates.get(n).v1.map(bytes), v2: ctx.updates.get(n).v2.map(bytes) }))
  }
  const state = (enc, d) => ({
    full: bytes(enc.update(d)),
    diff: Object.fromEntries(names.filter(o => ctx.docs.get(o) !== d).map(o => [o, bytes(enc.update(d, enc.sv(ctx.docs.get(o))))]))
  })
  obs.state = {
    api: 'Y.encodeStateAsUpdate(doc, sv), Y.encodeStateAsUpdateV2(doc, sv); sv is a V1 state vector for both',
    value: perReplica(d => ({
      v1: state({ update: Y.encodeStateAsUpdate, sv: Y.encodeStateVector }, d),
      v2: state({ update: Y.encodeStateAsUpdateV2, sv: Y.encodeStateVector }, d)
    }))
  }
  obs.sv = {
    api: 'Y.encodeStateVector, Y.encodeStateVectorV2',
    value: perReplica(d => ({ v1: bytes(Y.encodeStateVector(d)), v2: bytes(encodeStateVectorV2(d)) }))
  }
  obs.doc = { api: 'toDelta, toArray, entries, getAttributes walk of every root', value: perReplica(d => dumpDoc(d, roots)) }
  if (ctx.events.length) obs.events = { api: 'AbstractType.observe', value: ctx.events }
  if (ctx.results.size) {
    obs.results = {
      api: Array.from(new Set(Array.from(ctx.results.values(), r => r.api))).join(', '),
      value: Object.fromEntries(Array.from(ctx.results, ([n, r]) => [n, { api: r.api, bytes: bytes(r.bytes) }]))
    }
  }
  if (extra.includes('structs')) obs.structs = { api: 'doc.store.clients', value: perReplica(d => dumpStructs(d)) }
  if (extra.includes('ds')) obs.ds = { api: 'Y.createDeleteSetFromStructStore', value: perReplica(d => dumpDs(d)) }
  return obs
}
