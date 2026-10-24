// Module resolution hooks: yjs, lib0 and y-protocols resolve only to the pinned submodules (ADR 0011).
// No npm copy of any oracle package is ever installed, so a stray node_modules/yjs cannot be picked up.
import { pathToFileURL } from 'node:url'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..')
const lib0Index = pathToFileURL(path.join(root, 'lib0', 'index.js')).href
const yjsIndex = pathToFileURL(path.join(root, 'yjs', 'src', 'index.js')).href

export async function resolve (specifier, context, nextResolve) {
  if (specifier === 'yjs') return { url: yjsIndex, shortCircuit: true }
  if (specifier === 'lib0' || specifier.startsWith('lib0/')) {
    // lib0 resolves its own "lib0/x" exports through package self-reference
    return nextResolve(specifier, { ...context, parentURL: lib0Index })
  }
  return nextResolve(specifier, context)
}
