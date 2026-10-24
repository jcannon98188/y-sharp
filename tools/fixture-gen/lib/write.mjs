// Deterministic serialisation: fixed key order as built, 2-space indent, short leaves inline, LF, trailing newline.
import { createHash } from 'node:crypto'

const MAX_INLINE = 100

export const stringify = (v, indent = '') => {
  const compact = JSON.stringify(v)
  if (compact === undefined) throw new Error('undefined in fixture output')
  if (compact.length <= MAX_INLINE || v === null || typeof v !== 'object') return compact
  const inner = indent + '  '
  if (Array.isArray(v)) return '[\n' + v.map(x => inner + stringify(x, inner)).join(',\n') + '\n' + indent + ']'
  return '{\n' + Object.entries(v).map(([k, x]) => inner + JSON.stringify(k) + ': ' + stringify(x, inner)).join(',\n') + '\n' + indent + '}'
}

export const render = v => stringify(v) + '\n'
export const sha256 = s => createHash('sha256').update(s).digest('hex')
