// ADR 0011 tagged values, in the wire shape TestKit's TaggedValue reads (tests/YSharp.TestKit/Values/TaggedValue.cs).
//   plain: null, booleans, finite numbers other than -0, strings without lone surrogates, arrays
//   tagged ("$" member): undefined, nan, inf, -inf, -0, bigint{v}, bytes{hex}, str{units}, obj{pairs}
// Objects are ordered pair lists; a JS Map keeps insertion order exactly, a plain object follows JS key order.

export class ScriptError extends Error {}

const hasLoneSurrogate = s => {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i)
    if (c >= 0xd800 && c <= 0xdbff) {
      const n = s.charCodeAt(i + 1)
      if (n >= 0xdc00 && n <= 0xdfff) { i++; continue }
      return true
    }
    if (c >= 0xdc00 && c <= 0xdfff) return true
  }
  return false
}

export const hex = u8 => Buffer.from(u8.buffer, u8.byteOffset, u8.byteLength).toString('hex')
export const unhex = s => new Uint8Array(Buffer.from(s, 'hex'))

export const tag = v => {
  if (v === undefined) return { $: 'undefined' }
  if (v === null || typeof v === 'boolean') return v
  if (typeof v === 'number') {
    if (Number.isNaN(v)) return { $: 'nan' }
    if (v === Infinity) return { $: 'inf' }
    if (v === -Infinity) return { $: '-inf' }
    if (Object.is(v, -0)) return { $: '-0' }
    return v
  }
  if (typeof v === 'bigint') return { $: 'bigint', v: v.toString() }
  if (typeof v === 'string') {
    return hasLoneSurrogate(v) ? { $: 'str', units: Array.from({ length: v.length }, (_, i) => v.charCodeAt(i)) } : v
  }
  if (v instanceof Uint8Array) return { $: 'bytes', hex: hex(v) }
  if (Array.isArray(v)) return v.map(tag)
  if (v instanceof Map) return { $: 'obj', pairs: Array.from(v, ([k, x]) => [String(k), tag(x)]) }
  if (typeof v === 'object') return { $: 'obj', pairs: Object.entries(v).map(([k, x]) => [k, tag(x)]) }
  throw new ScriptError(`cannot tag a ${typeof v}`)
}

export const untag = t => {
  if (t === null || typeof t === 'boolean' || typeof t === 'number' || typeof t === 'string') return t
  if (Array.isArray(t)) return t.map(untag)
  switch (t.$) {
    case 'undefined': return undefined
    case 'nan': return NaN
    case 'inf': return Infinity
    case '-inf': return -Infinity
    case '-0': return -0
    case 'bigint': return BigInt(t.v)
    case 'bytes': return unhex(t.hex)
    case 'str': return String.fromCharCode(...t.units)
    case 'obj': return Object.fromEntries(t.pairs.map(([k, v]) => [k, untag(v)]))
    default: throw new ScriptError(`unknown tag ${JSON.stringify(t)}`)
  }
}
