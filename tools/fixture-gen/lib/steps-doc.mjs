// Doc-level step kinds of ADR 0016 (Spec 8): gc.run, subdoc.*, snapshot.*, relpos.*, undo.*, pud.*.
// They live in their own module so the core interpreter (steps.mjs) stays untouched by this area; generate.mjs imports it.
// Results are recorded under the `results` observation as {api, value}; fired events under `events`.
import { Y } from './oracle.mjs'
import { tag, ScriptError } from './tagged.mjs'
import { defineStep, resolveTarget } from './steps.mjs'
import { yvalueOut, deltaOut } from './observe.mjs'

/** Per-run state of this area, hung on the interpreter context. */
const state = ctx => (ctx.df ??= { snapshots: new Map(), relpos: new Map(), undo: new Map(), pud: new Map(), timers: [], seq: 0, patched: false })

const target = (ctx, s) => resolveTarget(ctx.doc(s.doc), s.target)

/** `setTimeout` is virtual while a script runs: timers fire from `tick` in (due, insertion) order (PUD-03/04 seam). */
const installTimers = ctx => {
  const st = state(ctx)
  if (st.patched) return
  st.patched = true
  const realSetTimeout = globalThis.setTimeout
  globalThis.setTimeout = (fn, ms = 0) => { st.timers.push({ due: ctx.now + ms, seq: st.seq++, fn }) }
  ctx.cleanups.push(() => { globalThis.setTimeout = realSetTimeout })
  const prev = ctx.afterTick
  ctx.afterTick = () => {
    prev?.()
    for (;;) {
      const due = st.timers.filter(t => t.due <= ctx.now).sort((a, b) => a.due - b.due || a.seq - b.seq)[0]
      if (!due) break
      st.timers.splice(st.timers.indexOf(due), 1)
      due.fn()
    }
  }
}

const record = (ctx, name, api, value) => {
  if (ctx.results.has(name)) throw new ScriptError(`result '${name}' is recorded twice`)
  ctx.results.set(name, { api, value })
}

const subdocsOf = (ctx, s) => {
  const found = Array.from(ctx.doc(s.doc).subdocs).filter(d => d.guid === s.guid)
  const d = found[s.index ?? 0]
  if (!d) throw new ScriptError(`${s.step}: no subdoc ${s.guid}#${s.index ?? 0}`)
  return d
}

const guids = set => Array.from(set, d => d.guid)

// ---------------------------------------------------------------- gc

defineStep({
  kind: 'gc.run',
  required: ['doc'],
  optional: ['gc'],
  run: (ctx, s) => {
    const doc = ctx.doc(s.doc)
    if (s.gc !== undefined) doc.gc = s.gc
    Y.tryGc(Y.createDeleteSetFromStructStore(doc.store), doc.store, doc.gcFilter)
  }
})

// ---------------------------------------------------------------- subdocs

defineStep({
  kind: 'subdoc.add',
  required: ['doc', 'target', 'guid'],
  optional: ['at', 'key', 'opts'],
  run: (ctx, s) => {
    const sub = new Y.Doc({ guid: s.guid, ...(s.opts ?? {}) })
    const t = target(ctx, s)
    if (s.key !== undefined) {
      if (!(t instanceof Y.Map)) throw new ScriptError('subdoc.add: key needs a map target')
      t.set(s.key, sub)
    } else {
      if (!(t instanceof Y.Array)) throw new ScriptError('subdoc.add: at needs an array target')
      t.insert(s.at ?? t.length, [sub])
    }
  }
})
defineStep({ kind: 'subdoc.load', required: ['doc', 'guid'], optional: ['index'], run: (ctx, s) => subdocsOf(ctx, s).load() })
defineStep({ kind: 'subdoc.destroy', required: ['doc', 'guid'], optional: ['index'], run: (ctx, s) => subdocsOf(ctx, s).destroy() })
defineStep({
  kind: 'subdoc.observe',
  required: ['doc', 'name'],
  run: (ctx, s) => {
    ctx.doc(s.doc).on('subdocs', e => ctx.events.push({
      observer: s.name, type: 'subdocs', added: guids(e.added), removed: guids(e.removed), loaded: guids(e.loaded)
    }))
  }
})
defineStep({
  kind: 'subdoc.inspect',
  required: ['doc', 'name'],
  run: (ctx, s) => {
    const doc = ctx.doc(s.doc)
    record(ctx, s.name, 'Doc.subdocs, Doc.getSubdocGuids, Doc fields', {
      guids: Array.from(doc.getSubdocGuids()),
      subdocs: Array.from(doc.subdocs, d => ({
        guid: d.guid,
        shouldLoad: d.shouldLoad,
        autoLoad: d.autoLoad,
        isLoaded: d.isLoaded,
        gc: d.gc,
        collectionid: tag(d.collectionid),
        meta: tag(d.meta),
        clientIdIsParents: d.clientID === doc.clientID
      }))
    })
  }
})

// ---------------------------------------------------------------- snapshots

defineStep({
  kind: 'snapshot.take',
  required: ['doc', 'name'],
  run: (ctx, s) => {
    const snap = Y.snapshot(ctx.doc(s.doc))
    state(ctx).snapshots.set(s.name, snap)
    record(ctx, s.name, 'Y.snapshot, Y.encodeSnapshot, Y.encodeSnapshotV2', { v1: tag(Y.encodeSnapshot(snap)), v2: tag(Y.encodeSnapshotV2(snap)) })
  }
})
defineStep({
  kind: 'snapshot.restore',
  required: ['doc', 'snapshot', 'as', 'clientId'],
  run: (ctx, s) => {
    const snap = state(ctx).snapshots.get(s.snapshot)
    if (!snap) throw new ScriptError(`snapshot.restore: unknown snapshot '${s.snapshot}'`)
    if (ctx.docs.has(s.as)) throw new ScriptError(`replica '${s.as}' declared twice`)
    const restored = new Y.Doc()
    restored.clientID = s.clientId
    const rec = { v1: [], v2: [] }
    restored.on('update', u => rec.v1.push(u))
    restored.on('updateV2', u => rec.v2.push(u))
    Y.createDocFromSnapshot(ctx.doc(s.doc), snap, restored)
    ctx.docs.set(s.as, restored)
    ctx.updates.set(s.as, rec)
  }
})
defineStep({
  kind: 'snapshot.read',
  required: ['doc', 'target', 'snapshot', 'name'],
  optional: ['prev'],
  run: (ctx, s) => {
    const st = state(ctx)
    const snap = st.snapshots.get(s.snapshot)
    const prev = s.prev === undefined ? undefined : st.snapshots.get(s.prev)
    if (!snap || (s.prev !== undefined && !prev)) throw new ScriptError('snapshot.read: unknown snapshot')
    const t = target(ctx, s)
    if (!(t instanceof Y.Text)) throw new ScriptError('snapshot.read: target is not a text')
    record(ctx, s.name, 'Y.Text.toDelta(snapshot, prevSnapshot)', deltaOut(t.toDelta(snap, prev)))
  }
})

// ---------------------------------------------------------------- relative positions

const typeAnchor = (doc, type) => {
  if (type._item === null) {
    for (const [k, v] of doc.share) if (v === type) return k
    return null
  }
  return [type._item.id.client, type._item.id.clock]
}

defineStep({
  kind: 'relpos.create',
  required: ['doc', 'target', 'name', 'at'],
  optional: ['assoc'],
  run: (ctx, s) => {
    const rpos = Y.createRelativePositionFromTypeIndex(target(ctx, s), s.at, s.assoc ?? 0)
    const enc = Y.encodeRelativePosition(rpos)
    state(ctx).relpos.set(s.name, enc)
    record(ctx, s.name, 'Y.createRelativePositionFromTypeIndex, Y.encodeRelativePosition, Y.relativePositionToJSON', {
      bytes: tag(enc), json: JSON.parse(JSON.stringify(Y.relativePositionToJSON(rpos)))
    })
  }
})
defineStep({
  kind: 'relpos.resolve',
  required: ['doc', 'relpos', 'name'],
  optional: ['followUndone'],
  run: (ctx, s) => {
    const enc = state(ctx).relpos.get(s.relpos)
    if (!enc) throw new ScriptError(`relpos.resolve: unknown relative position '${s.relpos}'`)
    const doc = ctx.doc(s.doc)
    const abs = Y.createAbsolutePositionFromRelativePosition(Y.decodeRelativePosition(enc), doc, s.followUndone ?? true)
    record(ctx, s.name, 'Y.createAbsolutePositionFromRelativePosition', abs === null ? null : { type: typeAnchor(doc, abs.type), index: abs.index, assoc: abs.assoc })
  }
})

// ---------------------------------------------------------------- undo

defineStep({
  kind: 'undo.create',
  required: ['doc', 'name', 'scope'],
  optional: ['captureTimeout', 'trackedOrigins', 'ignoreRemoteMapChanges'],
  run: (ctx, s) => {
    const doc = ctx.doc(s.doc)
    if (state(ctx).undo.has(s.name)) throw new ScriptError(`undo manager '${s.name}' created twice`)
    const opts = {}
    if (s.captureTimeout !== undefined) opts.captureTimeout = s.captureTimeout
    if (s.trackedOrigins !== undefined) opts.trackedOrigins = new Set(s.trackedOrigins)
    if (s.ignoreRemoteMapChanges !== undefined) opts.ignoreRemoteMapChanges = s.ignoreRemoteMapChanges
    const scope = s.scope === 'doc' ? doc : s.scope.map(t => resolveTarget(doc, t))
    const um = new Y.UndoManager(scope, opts)
    for (const type of ['stack-item-added', 'stack-item-updated', 'stack-item-popped']) {
      um.on(type, e => ctx.events.push({ observer: s.name, type, kind: e.type }))
    }
    um.on('stack-cleared', e => ctx.events.push({ observer: s.name, type: 'stack-cleared', undoStackCleared: e.undoStackCleared, redoStackCleared: e.redoStackCleared }))
    state(ctx).undo.set(s.name, um)
  }
})
const manager = (ctx, s) => {
  const um = state(ctx).undo.get(s.name)
  if (!um) throw new ScriptError(`${s.step}: unknown undo manager '${s.name}'`)
  return um
}
defineStep({ kind: 'undo.stopCapturing', required: ['name'], run: (ctx, s) => manager(ctx, s).stopCapturing() })
for (const dir of ['undo', 'redo']) {
  defineStep({
    kind: `undo.${dir}`,
    required: ['name'],
    optional: ['as'],
    run: (ctx, s) => {
      const um = manager(ctx, s)
      const popped = um[dir]() !== null
      if (s.as !== undefined) record(ctx, s.as, `UndoManager.${dir}, undoStack.length, redoStack.length`, { popped, undoStack: um.undoStack.length, redoStack: um.redoStack.length })
    }
  })
}

// ---------------------------------------------------------------- permanent user data

defineStep({
  kind: 'pud.setUserDescription',
  required: ['doc', 'clientId', 'description'],
  run: (ctx, s) => {
    installTimers(ctx)
    const doc = ctx.doc(s.doc)
    const st = state(ctx)
    if (!st.pud.has(s.doc)) st.pud.set(s.doc, new Y.PermanentUserData(doc))
    st.pud.get(s.doc).setUserMapping(doc, s.clientId, s.description)
  }
})
defineStep({
  kind: 'pud.observe',
  required: ['doc', 'name'],
  optional: ['clientIds', 'deletedIds'],
  run: (ctx, s) => {
    installTimers(ctx)
    const doc = ctx.doc(s.doc)
    const st = state(ctx)
    if (!st.pud.has(s.doc)) st.pud.set(s.doc, new Y.PermanentUserData(doc))
    const pud = st.pud.get(s.doc)
    record(ctx, s.name, 'PermanentUserData.getUserByClientId, getUserByDeletedId, doc.getMap("users")', {
      users: yvalueOut(doc.getMap('users')),
      byClientId: (s.clientIds ?? []).map(id => [id, pud.getUserByClientId(id)]),
      byDeletedId: (s.deletedIds ?? []).map(([client, clock]) => [[client, clock], pud.getUserByDeletedId(Y.createID(client, clock))])
    })
  }
})
