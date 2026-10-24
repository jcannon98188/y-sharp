// Fixture generator (ADR 0011): scenarios -> ops script -> replay on the pinned oracle -> observations -> fixtures/.
//   npm run gen      regenerate fixtures/ (and remove stale files)
//   npm run check    regenerate in memory and fail on any difference from fixtures/ (what the drift job asserts via git diff)
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync, rmSync, statSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { repoRoot, versions, Y } from './lib/oracle.mjs'
import { interpret } from './lib/steps.mjs'
import './lib/steps-doc.mjs'
import './lib/protocol-steps.mjs'
import { normalize, rootsOf } from './lib/script.mjs'
import { observe } from './lib/observe.mjs'
import { truncationOutcomes, malformedCases } from './lib/negative.mjs'
import { checkScript, checkFixture, checkSchemaMatchesRegistry } from './lib/validate.mjs'
import { render, sha256 } from './lib/write.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const fixturesRoot = path.join(repoRoot, 'fixtures')
const pkg = JSON.parse(readFileSync(path.join(here, 'package.json'), 'utf8'))
const generator = `${pkg.name} ${pkg.version}`

// Node is pinned in .nvmrc; headers record only the major so a patch release cannot cause drift.
const nodePin = readFileSync(path.join(repoRoot, '.nvmrc'), 'utf8').trim().replace(/^v/, '').split('.')[0]
if (process.versions.node.split('.')[0] !== nodePin) {
  console.error(`fixture-gen: running Node ${process.versions.node}, but .nvmrc pins ${nodePin}. Use the pinned major (fixtures are byte-compared).`)
  process.exit(2)
}

checkSchemaMatchesRegistry()

const scenarioDir = path.join(here, 'scenarios')
const scenarios = []
for (const f of readdirSync(scenarioDir).filter(f => f.endsWith('.mjs')).sort()) {
  const mod = await import(pathToFileURL(path.join(scenarioDir, f)).href)
  const list = typeof mod.default === 'function' ? mod.default() : mod.default
  for (const s of list) scenarios.push({ ...s, source: f })
}

const header = (s, seed) => ({
  schema: 1,
  covers: [...s.covers],
  versions: { ...versions },
  node: nodePin,
  generator,
  seed
})

const build = s => {
  const what = `${s.area}/${s.name}`
  if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(s.name) || !/^[a-z0-9]+$/.test(s.area)) throw new Error(`${what}: area is [a-z0-9]+, name is kebab-case`)
  if (!s.covers?.length) throw new Error(`${what}: covers is required`)
  const seed = s.seed ?? 0
  let script, observations
  if (s.malformed) {
    script = { schema: 1, steps: [] }
    observations = { malformed: malformedCases(s.malformed) }
  } else {
    script = normalize(typeof s.script === 'function' ? s.script(seed) : s.script)
    checkScript(script, `${what} script`)
    const ctx = interpret(script)
    const roots = rootsOf(script)
    observations = observe(ctx, roots, s.observe ?? [])
    for (const t of s.truncation ?? []) {
      const doc = ctx.docs.get(t.replica)
      const bytes = t.encoding === 'v2' ? Y.encodeStateAsUpdateV2(doc) : Y.encodeStateAsUpdate(doc)
      observations.truncation ??= { api: 'Y.applyUpdate, Y.applyUpdateV2 on every proper prefix of encodeStateAsUpdate(replica)', value: [] }
      observations.truncation.value.push({ replica: t.replica, ...truncationOutcomes(bytes, t.encoding ?? 'v1', roots).value })
    }
  }
  const fixture = { header: header(s, seed), script, observations }
  checkFixture(fixture, what)
  return { rel: `${s.area}/${s.name}.json`, area: s.area, name: s.name, covers: s.covers, text: render(fixture) }
}

const outputs = scenarios.map(build)
const seen = new Set()
for (const o of outputs) {
  if (seen.has(o.rel)) throw new Error(`duplicate fixture ${o.rel}`)
  seen.add(o.rel)
}
outputs.sort((a, b) => (a.rel < b.rel ? -1 : 1))

const manifest = {
  schema: 1,
  generator,
  node: nodePin,
  versions: { ...versions },
  fixtures: outputs.map(o => ({ path: o.rel, area: o.area, name: o.name, covers: o.covers, sha256: sha256(o.text), bytes: Buffer.byteLength(o.text) }))
}
outputs.push({ rel: 'index.json', text: render(manifest) })

const walk = dir => existsSync(dir)
  ? readdirSync(dir).flatMap(f => {
    const p = path.join(dir, f)
    return statSync(p).isDirectory() ? walk(p) : [p]
  })
  : []
const onDisk = walk(fixturesRoot).filter(p => p.endsWith('.json')).map(p => path.relative(fixturesRoot, p).split(path.sep).join('/'))

if (process.argv.includes('--check')) {
  const problems = []
  for (const o of outputs) {
    const p = path.join(fixturesRoot, o.rel)
    if (!existsSync(p)) problems.push(`missing ${o.rel}`)
    else if (readFileSync(p, 'utf8') !== o.text) problems.push(`differs ${o.rel}`)
  }
  for (const r of onDisk) if (!outputs.some(o => o.rel === r)) problems.push(`stale ${r}`)
  if (problems.length) {
    console.error('fixture drift:\n  ' + problems.join('\n  '))
    process.exit(1)
  }
  console.log(`fixtures up to date (${outputs.length} files)`)
} else {
  for (const o of outputs) {
    const p = path.join(fixturesRoot, o.rel)
    mkdirSync(path.dirname(p), { recursive: true })
    writeFileSync(p, o.text)
  }
  for (const r of onDisk) if (!outputs.some(o => o.rel === r)) rmSync(path.join(fixturesRoot, r))
  console.log(`wrote ${outputs.length} files to fixtures/`)
}
