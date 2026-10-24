// Seeded families: the PRNG only picks the ops while generating. The expanded op log is what gets stored and replayed,
// so the C# side never needs the PRNG to replay these (it is ported separately for upstream-seed reproduction).
import { lib0 } from '../lib/oracle.mjs'
import { replica, text } from '../lib/script.mjs'
import { Session } from '../lib/authoring.mjs'

const t = text('t')
const { prng } = lib0

const randomText = (seed, clients, opCount) => {
  const gen = prng.create(seed)
  const names = Array.from({ length: clients }, (_, i) => String.fromCharCode(97 + i))
  const s = new Session(names.map((n, i) => replica(n, 1000 + i)))
  for (let i = 0; i < opCount; i++) {
    const doc = prng.oneOf(gen, names)
    const len = s.doc(doc).getText('t').length
    const roll = prng.int32(gen, 0, 9)
    if (roll < 5 || len === 0) {
      s.push({ step: 'text.insert', doc, target: t, at: prng.int32(gen, 0, len), value: prng.word(gen, 1, 6) })
    } else if (roll < 8) {
      const at = prng.int32(gen, 0, len - 1)
      s.push({ step: 'text.delete', doc, target: t, at, len: prng.int32(gen, 1, Math.min(3, len - at)) })
    } else {
      const from = prng.oneOf(gen, names)
      const to = prng.oneOf(gen, names)
      if (from !== to) s.push({ step: 'sync', from, to })
    }
  }
  for (const a of names) for (const b of names) if (a !== b) s.push({ step: 'sync', from: a, to: b })
  for (const a of names) for (const b of names) if (a !== b) s.push({ step: 'sync', from: a, to: b })
  return s.script
}

export default [
  {
    area: 'random',
    name: 'text-three-clients-seed-1337',
    covers: ['T-1', 'T-2', 'A-18'],
    seed: 1337,
    observe: ['ds'],
    script: seed => randomText(seed, 3, 40)
  }
]
