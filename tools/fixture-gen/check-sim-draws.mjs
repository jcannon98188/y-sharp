// Proves the hand-written simulation (lib/sim.mjs) draws exactly like the upstream randomized tests (ADR 0017: "keep draws").
// For every non-production testRepeat* test it runs the upstream test function itself with a recording generator and runs
// lib/sim.mjs with a second recording generator on the same seed, then requires the two draw sequences to be identical.
//   npm run check:draws        (not part of the fixture drift check; needs the pinned submodules only)
import { register } from 'node:module'
import { pathToFileURL, fileURLToPath } from 'node:url'
import path from 'node:path'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const protocols = pathToFileURL(path.join(root, 'y-protocols') + path.sep).href
register('data:text/javascript,' + encodeURIComponent(`
  export async function resolve (specifier, context, next) {
    if (specifier.startsWith('y-protocols/')) return { url: ${JSON.stringify(protocols)} + specifier.slice('y-protocols/'.length) + '.js', shortCircuit: true }
    return next(specifier, context)
  }`))
register('./lib/hooks.mjs', import.meta.url)

const { lib0 } = await import('./lib/oracle.mjs')
const { runSim, upstreamDefects } = await import('./lib/sim.mjs')
const { prng } = lib0

let fresh = 0
// a new module instance per run: upstream's payload counters (charCounter, _uniqueNumber) are module-level and leak between runs
const upstream = name => import(pathToFileURL(path.join(root, 'yjs', 'tests', name)).href + '?run=' + fresh++)

const recording = seed => {
  const inner = prng.create(seed)
  const draws = []
  return { draws, gen: { next () { const v = inner.next(); draws.push(v); return v } } }
}

// Upstream shares three `marks` objects across a whole test run and Yjs mutates them: insertText() sets every attribute that is
// active at the insertion point and absent from the given object to null on that object. Later uses of the mark then carry those
// nulls and format differently. Neither the op log (attributes are stored by value) nor the C# port (immutable attribute values)
// can alias like that, so the ports run on unshared copies. To still compare draws, this check re-creates the aliasing around
// the Y.Text entry points: each pristine mark maps to one persistent object, as in upstream.
const { Y } = await import('./lib/oracle.mjs')
const pristine = [{ bold: true }, { italic: true }, { italic: true, color: '#888' }]
const markIndex = a => {
  if (!a || typeof a !== 'object') return -1
  const keys = Object.keys(a).filter(k => a[k] !== null).sort().join(',')
  return ['bold', 'italic', 'color,italic'].indexOf(keys)
}
let sharedMarks = null
const share = a => {
  const i = markIndex(a)
  return i < 0 || !sharedMarks ? a : sharedMarks[i]
}
for (const [name, argAt] of [['insert', 2], ['format', 2]]) {
  const original = Y.Text.prototype[name]
  Y.Text.prototype[name] = function (...args) {
    args[argAt] = share(args[argAt])
    return original.apply(this, args)
  }
}
const applyDelta = Y.Text.prototype.applyDelta
Y.Text.prototype.applyDelta = function (delta, opts) {
  return applyDelta.call(this, delta.map(op => (op.attributes === undefined ? op : { ...op, attributes: share(op.attributes) })), opts)
}

const cases = [
  ['y-map.tests.js', 'map', { testRepeatGeneratingYmapTests10: 3, testRepeatGeneratingYmapTests40: 40, testRepeatGeneratingYmapTests300: 300, testRepeatGeneratingYmapTests1000: 1000, testRepeatGeneratingYmapTests1800: 1800 }],
  ['y-array.tests.js', 'array', { testRepeatGeneratingYarrayTests6: 6, testRepeatGeneratingYarrayTests40: 40, testRepeatGeneratingYarrayTests300: 300, testRepeatGeneratingYarrayTests1000: 1000, testRepeatGeneratingYarrayTests1800: 1800 }],
  ['y-text.tests.js', 'text', { testRepeatGenerateTextChanges5: 5, testRepeatGenerateTextChanges30: 30, testRepeatGenerateTextChanges90: 90, testRepeatGenerateTextChanges300: 300 }],
  ['y-text.tests.js', 'quill', { testRepeatGenerateQuillChanges1: 1, testRepeatGenerateQuillChanges2: 2, testRepeatGenerateQuillChanges3: 3, testRepeatGenerateQuillChanges30: 30, testRepeatGenerateQuillChanges100: 100, testRepeatGenerateQuillChanges300: 300 }]
]

const seeds = process.argv.slice(2).map(Number)
if (seeds.length === 0) seeds.push(1, 2, 3, 4, 5, 42, 1337)

const realError = console.error
let failures = 0
let checked = 0
for (const [file, kind, tests] of cases) {
  for (const [name, iterations] of Object.entries(tests)) {
    for (const seed of seeds) {
      const mod = await upstream(file)
      sharedMarks = null
      const up = recording(seed)
      console.error = () => {} // upstream logs "sync protocol doesnt support v2 protocol yet"
      try { mod[name]({ prng: up.gen }) } finally { console.error = realError }
      sharedMarks = pristine.map(m => ({ ...m }))
      const mine = recording(seed)
      upstreamDefects.codeBlockKey = 'format' // SIM-03: upstream's key, so the remaining draws can be compared
      try { runSim({ gen: mine.gen, kind, iterations }) } finally { upstreamDefects.codeBlockKey = 'attributes' }
      checked++
      const same = up.draws.length === mine.draws.length && up.draws.every((v, i) => v === mine.draws[i])
      if (!same) {
        failures++
        const at = up.draws.findIndex((v, i) => v !== mine.draws[i])
        console.error(`DRAWS DIFFER ${name} seed ${seed}: upstream ${up.draws.length} draws, port ${mine.draws.length}, first difference at draw ${at}`)
      }
    }
  }
}
console.log(failures === 0 ? `draw sequences identical for ${checked} upstream runs` : `${failures} of ${checked} runs differ`)
process.exit(failures === 0 ? 0 : 1)
