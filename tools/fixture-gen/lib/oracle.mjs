// The only module that imports the oracle. Everything resolves to the pinned submodules by path.
import { readFileSync } from 'node:fs'
import { pathToFileURL, fileURLToPath } from 'node:url'
import path from 'node:path'

export const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..')

const url = (...p) => pathToFileURL(path.join(repoRoot, ...p)).href

export const Y = await import(url('yjs', 'src', 'index.js'))
export const { encodeStateVectorV2 } = await import(url('yjs', 'src', 'utils', 'encoding.js'))
export const syncProtocol = await import(url('y-protocols', 'sync.js'))
export const awarenessProtocol = await import(url('y-protocols', 'awareness.js'))
export const lib0 = {
  prng: await import(url('lib0', 'prng.js')),
  encoding: await import(url('lib0', 'encoding.js')),
  decoding: await import(url('lib0', 'decoding.js'))
}

const version = pkg => JSON.parse(readFileSync(path.join(repoRoot, pkg, 'package.json'), 'utf8')).version

export const versions = {
  yjs: version('yjs'),
  lib0: version('lib0'),
  'y-protocols': version('y-protocols')
}
