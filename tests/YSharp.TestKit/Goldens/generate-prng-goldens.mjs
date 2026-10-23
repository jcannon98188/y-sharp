// Generates prng-goldens.json from the pinned lib0 submodule (ADR 0015).
// Run: node tests/YSharp.TestKit/Goldens/generate-prng-goldens.mjs
// Raw next() values are stored as IEEE-754 bit patterns (hex) so they compare exactly.
import { readFileSync, writeFileSync } from 'node:fs'
import { fileURLToPath, pathToFileURL } from 'node:url'
import path from 'node:path'

const here = path.dirname(fileURLToPath(import.meta.url))
const lib0 = path.resolve(here, '../../../lib0')
const prng = await import(pathToFileURL(path.join(lib0, 'prng.js')).href)
const { Xoroshiro128plus } = await import(pathToFileURL(path.join(lib0, 'prng/Xoroshiro128plus.js')).href)
const { Xorshift32 } = await import(pathToFileURL(path.join(lib0, 'prng/Xorshift32.js')).href)

const bits = d => {
  const b = new DataView(new ArrayBuffer(8))
  b.setFloat64(0, d)
  return b.getBigUint64(0).toString(16).padStart(16, '0')
}
const hex = u8 => Array.from(u8, x => x.toString(16).padStart(2, '0')).join('')
const units = s => Array.from({ length: s.length }, (_, i) => s.charCodeAt(i))

const seeds = [0, 1, 2, 3, 42, 1337, 12345, 65535, 3735928559, 4294967295]

// The call script is replayed in order on one generator, so draw order is part of the golden.
const script = [
  ['bool'], ['bool'], ['bool'], ['bool'],
  ['int32', 0, 100], ['int32', 0, 100], ['int32', 0, 100], ['int32', 0, 2],
  ['int32', -5, 5], ['int32', -5, 5], ['int32', -1000000, -1],
  ['uint32', 0, 4294967295], ['uint32', 0, 4294967295], ['uint32', -3, 3],
  ['int53', 0, 9007199254740990], ['int53', -100, 100],
  ['uint53', -10, 10], ['uint53', -10, 10],
  ['real53'], ['real53'],
  ['char'], ['char'], ['char'], ['char'],
  ['letter'], ['letter'], ['letter'],
  ['word'], ['word', 3, 6], ['word', 0, 0],
  ['utf16Rune'], ['utf16Rune'], ['utf16Rune'],
  ['utf16String'], ['utf16String', 5],
  ['oneOf', [10, 20, 30, 40, 50]], ['oneOf', [10, 20, 30, 40, 50]], ['oneOf', [7]],
  ['uint8Array', 16],
  ['int31', 0, 1], ['int31', 0, 1]
]

const run = (gen, [name, ...args]) => {
  const r = prng[name](gen, ...args)
  if (typeof r === 'string') return { kind: 'string', units: units(r) }
  if (r instanceof Uint8Array) return { kind: 'bytes', hex: hex(r) }
  if (typeof r === 'boolean') return { kind: 'bool', value: r }
  return { kind: 'number', bits: bits(r), value: r }
}

const out = {
  generatedFrom: { lib0: JSON.parse(readFileSync(path.join(lib0, 'package.json'), 'utf8')).version },
  nextCount: 200,
  seeds: seeds.map(seed => {
    const x32 = new Xorshift32(seed)
    const xo = new Xoroshiro128plus(seed)
    const state = Array.from(xo.state)
    const gen = prng.create(seed)
    return {
      seed,
      state,
      xorshift32: Array.from({ length: 200 }, () => bits(x32.next())),
      xoroshiro: (() => { const g = new Xoroshiro128plus(seed); return Array.from({ length: 200 }, () => bits(g.next())) })(),
      script: script.map(call => ({ call, result: run(gen, call) }))
    }
  })
}
writeFileSync(path.join(here, 'prng-goldens.json'), JSON.stringify(out, null, 1) + '\n')
console.log('wrote prng-goldens.json')
