// Generates tests/YSharp.Tests/Simulation/plan-goldens.json: for each upstream generator, the exact inputs and outputs of its
// operation plans taken from real runs of lib/sim.mjs (which check-sim-draws.mjs proves draws like upstream).
// One entry = which generator function ran (`mod`), the payload counters before it, the raw draws it consumed, the answers the
// document view gave it, and the ops steps it produced (stored form). The C# plans (tests/YSharp.Tests/Simulation/SimPlans.cs)
// replay an entry on scripted draws with no document, so the generators are checked bit for bit without an implementation.
//   npm run gen:plans     writes the file        npm run check:plans    fails if the file differs
import { register } from 'node:module'
import { readFileSync, writeFileSync, existsSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

register('./lib/hooks.mjs', import.meta.url)
const { lib0 } = await import('./lib/oracle.mjs')
const { runSim } = await import('./lib/sim.mjs')
const { prng } = lib0

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const target = path.join(root, 'tests', 'YSharp.Tests', 'Simulation', 'plan-goldens.json')

const runs = { map: 120, array: 120, text: 120, quill: 120 }
const seeds = [1, 2, 3]

const out = {}
for (const [kind, iterations] of Object.entries(runs)) {
  out[kind] = []
  for (const seed of seeds) {
    const collect = []
    runSim({ gen: prng.create(seed), kind, iterations, collect })
    collect.forEach((c, i) => out[kind].push({ seed, step: i, ...c }))
  }
}

const text = '{\n' + Object.entries(out).map(([kind, list]) =>
  `  ${JSON.stringify(kind)}: [\n` + list.map(e => '    ' + JSON.stringify(e)).join(',\n') + '\n  ]').join(',\n') + '\n}\n'

if (process.argv.includes('--check')) {
  const current = existsSync(target) ? readFileSync(target, 'utf8') : null
  if (current !== text) {
    console.error('plan goldens drift: run npm run gen:plans')
    process.exit(1)
  }
  console.log('plan goldens up to date')
} else {
  writeFileSync(target, text)
  console.log(`wrote ${path.relative(root, target)} (${Object.values(out).reduce((n, l) => n + l.length, 0)} plans)`)
}
