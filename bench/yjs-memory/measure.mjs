// Measures managed heap per item for Yjs on the three ADR 0009 slow-tier workloads and writes yjs-bytes-per-item.json.
// The workloads mirror tests/YSharp.Tests/Allocation/MemoryWorkloads.cs exactly (same operations, same order).
// Usage: node --expose-gc bench/yjs-memory/measure.mjs [oracleRoot]   (oracleRoot defaults to this repo; it must hold the yjs and lib0 submodules)
import { writeFileSync, readFileSync } from 'node:fs'
import { pathToFileURL, fileURLToPath } from 'node:url'
import { register } from 'node:module'
import path from 'node:path'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(process.argv[2] ?? path.join(here, '..', '..'))
if (typeof gc !== 'function') throw new Error('run with node --expose-gc')

// yjs imports 'lib0/...' through its package; map those specifiers to the lib0 submodule.
const lib0Base = pathToFileURL(path.join(root, 'lib0') + path.sep).href
register('data:text/javascript,' + encodeURIComponent(`
export async function resolve (s, c, n) {
  if (s.startsWith('lib0/')) {
    const rel = s.slice(5)
    return n(${JSON.stringify(lib0Base)} + (rel.endsWith('.js') ? rel : rel + '.js'), c)
  }
  return n(s, c)
}`))
const Y = await import(pathToFileURL(path.join(root, 'yjs', 'src', 'index.js')).href)
const version = JSON.parse(readFileSync(path.join(root, 'yjs', 'package.json'), 'utf8')).version

const heap = () => { gc(); gc(); return process.memoryUsage().heapUsed }
const BATCH = 1000

function textWorkload (n) {
  const doc = new Y.Doc(); const t = doc.getText('t')
  for (let i = 0; i < n; i += BATCH) {
    doc.transact(() => {
      for (let j = i; j < Math.min(n, i + BATCH); j++) {
        const back = (Math.imul(j, 0x9E3779B1) >>> 0) & 15
        t.insert(Math.max(0, t.length - back), String.fromCharCode(97 + (j % 26)))
      }
    })
  }
  return doc
}
function arrayWorkload (n) {
  const doc = new Y.Doc(); const a = doc.getArray('a')
  for (let i = 0; i < n; i += BATCH) doc.transact(() => { for (let j = i; j < Math.min(n, i + BATCH); j++) a.push([j]) })
  return doc
}
function mapWorkload (n) {
  const doc = new Y.Doc(); const m = doc.getMap('m')
  for (let i = 0; i < n; i += BATCH) doc.transact(() => { for (let j = i; j < Math.min(n, i + BATCH); j++) m.set('k' + j, j) })
  return doc
}

const workloads = { 'text-insert': [1_000_000, textWorkload], 'array-push': [1_000_000, arrayWorkload], 'map-set': [100_000, mapWorkload] }

// Each workload runs in its own process so one workload's garbage never skews another's baseline.
const only = process.argv[3]
if (only) {
  const [items, run] = workloads[only]
  const before = heap()
  const doc = run(items)
  const after = heap()
  console.log(JSON.stringify({ items, bytesPerItem: Math.round(((after - before) / items) * 100) / 100, alive: doc.share.size }))
} else {
  const { execFileSync } = await import('node:child_process')
  const out = { schema: 1, yjs: version, node: process.version, unit: 'bytes per item', workloads: {} }
  for (const name of Object.keys(workloads)) {
    const r = JSON.parse(execFileSync(process.execPath, ['--expose-gc', fileURLToPath(import.meta.url), root, name], { encoding: 'utf8' }))
    out.workloads[name] = { items: r.items, bytesPerItem: r.bytesPerItem }
  }
  writeFileSync(path.join(here, 'yjs-bytes-per-item.json'), JSON.stringify(out, null, 2) + '\n')
  console.log(JSON.stringify(out, null, 2))
}
