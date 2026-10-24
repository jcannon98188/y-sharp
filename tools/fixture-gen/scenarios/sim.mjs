// Spec 10 (TASK-2.10): the 12 golden op-log fixtures of ADR 0017 and the deep-observer fixture of Q25.
//
// Golden op logs: the map, array, text (`textChanges`) and quill (`qChanges`) generators of the upstream randomized tests each run
// at seed 1 in three sizes. The fixture stores the expanded op log (the edits every client made) and the delivery schedule (which
// message reached which client when); replay needs no PRNG. The observations are the guard against wrong-but-convergent
// conflict order: update bytes, state, state vectors, the typed doc dump, the struct store and the delete set of every replica.
import { lib0, Y } from '../lib/oracle.mjs'
import { interpret } from '../lib/steps.mjs'
import { normalize, replica, map } from '../lib/script.mjs'
import { runSim } from '../lib/sim.mjs'

const { prng } = lib0

const SIZES = {
  map: { small: 40, medium: 300, large: 1800 },
  array: { small: 40, medium: 300, large: 1800 },
  text: { small: 30, medium: 100, large: 300 },
  quill: { small: 30, medium: 100, large: 300 }
}

/** Generates one run and checks the recorded script reproduces the live run: same update events, all replicas converged. */
const goldenScript = (kind, iterations) => seed => {
  const { script, session } = runSim({ gen: prng.create(seed), kind, iterations })
  const replay = interpret(normalize(script))
  const names = [...replay.docs.keys()]
  const states = new Set()
  for (const n of names) {
    const live = Y.encodeStateAsUpdate(session.doc(n))
    const again = Y.encodeStateAsUpdate(replay.docs.get(n))
    if (Buffer.compare(Buffer.from(live), Buffer.from(again)) !== 0) throw new Error(`${kind}-${iterations}: replay of replica ${n} differs from the live run`)
    if (JSON.stringify(session.ctx.updates.get(n)) !== JSON.stringify(replay.updates.get(n))) throw new Error(`${kind}-${iterations}: update events of replica ${n} differ between live run and replay`)
    states.add(Buffer.from(Y.encodeStateVector(replay.docs.get(n))).toString('hex'))
  }
  if (states.size !== 1) throw new Error(`${kind}-${iterations}: replicas did not converge`)
  return script
}

const golden = Object.entries(SIZES).flatMap(([kind, sizes]) =>
  Object.entries(sizes).map(([size, iterations]) => ({
    area: 'sim',
    name: `${kind}-${size}`,
    covers: ['SIM-10', 'SIM-11', 'SIM-12', 'SIM-13', 'SIM-14'],
    seed: 1,
    observe: ['structs', 'ds'],
    script: goldenScript(kind, iterations)
  })))

// Q25: JS sorts deep events by path length with a stable sort, so equal-depth events keep the order they were generated in.
// 24 children at one depth (an unstable sort tends to reorder only past ~20 ties) changed in reverse order, then one child
// created inside a child (one level deeper) and the root's own key (depth 0).
const root = map('root')
const child = i => `c${String(i).padStart(2, '0')}`
const children = Array.from({ length: 24 }, (_, i) => i)

const deepOrder = {
  replicas: [replica('a', 1), replica('b', 2)],
  steps: [
    ...children.map(i => ({ step: 'map.insertType', doc: 'a', target: root, key: child(i), kind: 'map' })),
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'observeDeep', doc: 'a', target: root, name: 'deepA' },
    { step: 'observeDeep', doc: 'b', target: root, name: 'deepB' },
    {
      step: 'transact',
      doc: 'a',
      steps: [
        ...[...children].reverse().map(i => ({ step: 'map.set', doc: 'a', target: [root, child(i)], key: 'k', value: i })),
        { step: 'map.insertType', doc: 'a', target: [root, child(5)], key: 'inner', kind: 'map' },
        { step: 'map.set', doc: 'a', target: root, key: 'own', value: 1 }
      ]
    },
    // the same changes arrive on b as one remote update: the event order there comes from the update's struct order
    { step: 'sync', from: 'a', to: 'b' },
    {
      step: 'transact',
      doc: 'b',
      steps: [3, 17, 0, 23, 9].map(i => ({ step: 'map.set', doc: 'b', target: [root, child(i)], key: 'k2', value: i }))
    },
    { step: 'sync', from: 'b', to: 'a' }
  ]
}

export default [
  ...golden,
  {
    area: 'observers',
    name: 'deep-event-order',
    covers: ['Q25', 'TX-1'],
    script: deepOrder
  }
]
