// Spec 5 (values and codecs): wire-level scenarios for ENC-* and the number and string quirks (Q01-Q19, Q28, Q30).
// Every scenario stores its literal steps; the oracle records update bytes (V1 and V2), state, state vectors and,
// where useful, the struct store and the delete set. Owned by Spec 5; the shared lib is not touched.
import { replica, text, array, map } from '../lib/script.mjs'
import { updatesOf, stateAfter } from '../lib/authoring.mjs'

const t = text('t')
const a = array('a')
const m = map('m')

const bytes = (...xs) => Uint8Array.from(xs)
const varuint = n => {
  const out = []
  let v = BigInt(n)
  while (v > 127n) { out.push(Number(v & 127n) | 128); v >>= 7n }
  out.push(Number(v))
  return out
}
/** V1 state vector with a single entry: the bytes `diffUpdate` takes as its target. */
const sv1 = (client, clock) => Uint8Array.from([1, ...varuint(client), ...varuint(clock)])

// ---------------------------------------------------------------- Any values (Q01-Q06, Q08, ENC-06, ENC-07, ENC-72)

const numberTable = [
  ['zero', 0], ['one', 1], ['minus-one', -1], ['half', 1.5], ['one-point-one', 1.1],
  ['float32-limit-plus-one', 16777217], ['int31-max', 2147483647], ['int31-min-plus-one', -2147483647],
  ['int32-min', -2147483648], ['two-pow-31', 2147483648], ['two-pow-31-plus-one', 2147483649],
  ['two-pow-53', 9007199254740992], ['one-e21', 1e21], ['point-one', 0.1], ['one-third', 1 / 3],
  ['negative-zero', -0], ['nan', NaN], ['infinity', Infinity], ['minus-infinity', -Infinity],
  ['max-double', 1.7976931348623157e308], ['min-subnormal', 5e-324]
]

const anyValues = {
  replicas: [replica('a', 1)],
  steps: [
    ...numberTable.map(([key, value]) => ({ step: 'map.set', doc: 'a', target: m, key, value })),
    { step: 'map.set', doc: 'a', target: m, key: 'bigint-max', value: 9223372036854775807n },
    { step: 'map.set', doc: 'a', target: m, key: 'bigint-min', value: -9223372036854775808n },
    { step: 'map.set', doc: 'a', target: m, key: 'bigint-zero', value: 0n },
    { step: 'map.set', doc: 'a', target: m, key: 'true', value: true },
    { step: 'map.set', doc: 'a', target: m, key: 'false', value: false },
    { step: 'map.set', doc: 'a', target: m, key: 'null', value: null },
    { step: 'map.set', doc: 'a', target: m, key: 'undefined', value: undefined },
    { step: 'map.set', doc: 'a', target: m, key: 'string', value: 'plain' },
    { step: 'map.set', doc: 'a', target: m, key: 'binary', value: bytes(1, 2, 3) },
    { step: 'map.set', doc: 'a', target: m, key: 'nested-bytes', value: [bytes(9, 8), 'x'] },
    { step: 'map.set', doc: 'a', target: m, key: 'nested', value: [1, 'two', [3, null], { k: [true, undefined] }, NaN, -0] },
    // Q08: JS orders array-index keys first, ascending; the authoring Map keeps the insertion order the script stores.
    { step: 'map.set', doc: 'a', target: m, key: 'key-order', value: new Map([['b', 1], ['2', 2], ['a', 3], ['1', 4], ['01', 5], ['4294967294', 6], ['4294967295', 7]]) },
    { step: 'map.set', doc: 'a', target: m, key: 'empty-object', value: {} },
    { step: 'array.push', doc: 'a', target: a, values: [1, 'x', -0, NaN, 2147483648, [bytes(7)]] }
  ]
}

// ---------------------------------------------------------------- V1 JSON vs V2 Any in embeds and formats (Q11, Q28, Q30, ENC-12, ENC-75)

const embedsAndFormats = {
  replicas: [replica('a', 1)],
  steps: [
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'ab' },
    { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: new Map([['n', NaN], ['i', -Infinity], ['z', -0]]) },
    { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: new Map([['big', 1e21], ['sum', 0.1 + 0.2], ['tiny', 5e-324], ['small', 1e-7], ['edge', 1e-6], ['p53', 9007199254740994]]) },
    { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: new Map([['b', 1], ['2', 2], ['a', 3], ['1', 4]]) },
    { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: 'a "quoted"\n\tstring   \u007f \u0001' },
    { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: [1, [2, [3]], null, false] },
    { step: 'text.insertEmbed', doc: 'a', target: t, at: 1, value: 1.5 },
    { step: 'text.format', doc: 'a', target: t, at: 0, len: 2, attributes: new Map([['bold', true], ['size', 0.30000000000000004], ['gone', NaN]]) },
    { step: 'text.format', doc: 'a', target: t, at: 0, len: 1, attributes: new Map([['bold', null]]) },
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'x', attributes: new Map([['style', new Map([['b', 1], ['2', 2], ['a', 3]])]]) }
  ]
}

// ---------------------------------------------------------------- strings (Q16-Q19, ENC-05, ENC-73)

const strings = {
  replicas: [replica('a', 1), replica('b', 2)],
  steps: [
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'a😀b' },
    { step: 'sync', from: 'a', to: 'b' },
    // Q17: deleting the low surrogate splits the pair; both halves become U+FFFD in the stored content.
    { step: 'text.delete', doc: 'b', target: t, at: 2, len: 1 },
    { step: 'text.insert', doc: 'a', target: t, at: 4, value: '﻿bom é € 🧑‍🚀' },
    { step: 'sync', from: 'b', to: 'a' },
    { step: 'sync', from: 'a', to: 'b' },
    // Q18: a lone surrogate cannot be UTF-8; the encoder writes U+FFFD in its place.
    { step: 'text.insert', doc: 'b', target: t, at: 0, value: '\ud800' },
    { step: 'text.insert', doc: 'b', target: t, at: 1, value: '\udc00' },
    { step: 'text.insertEmbed', doc: 'b', target: t, at: 0, value: '😀' },
    { step: 'map.set', doc: 'a', target: m, key: 'é€😀', value: '😀 emoji key and value' },
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'sync', from: 'b', to: 'a' }
  ]
}

// ---------------------------------------------------------------- ids, clocks and lengths (ENC-02, ENC-70, ENC-71, ENC-77..79, Q12, Q13, Q14)

const idsAndLengths = () => {
  const ids = [['p', 1], ['q', 127], ['r', 128], ['s', 16384], ['u', 2147483648], ['w', 4294967295]]
  const steps = []
  ids.forEach(([n]) => steps.push({ step: 'text.insert', doc: n, target: t, at: 0, value: n }))
  // a ring of syncs so every client sees every other
  for (let round = 0; round < 2; round++) {
    for (let i = 0; i < ids.length; i++) steps.push({ step: 'sync', from: ids[i][0], to: ids[(i + 1) % ids.length][0] })
  }
  steps.push({ step: 'text.insert', doc: 'p', target: t, at: 1, value: 'x'.repeat(200) })
  steps.push({ step: 'text.insert', doc: 'w', target: t, at: 0, value: 'y'.repeat(300) })
  steps.push({ step: 'text.delete', doc: 'p', target: t, at: 3, len: 17 })
  steps.push({ step: 'array.insert', doc: 'p', target: a, at: 0, values: [1, 2, 3] })
  // runs of equal lengths and equal clients (UintOptRle with and without a count, including a run of zero-diff values: Q13)
  for (let i = 0; i < 8; i++) {
    steps.push({ step: 'array.insert', doc: i % 2 ? 'p' : 'q', target: a, at: 0, values: [i] })
    steps.push({ step: 'sync', from: i % 2 ? 'p' : 'q', to: i % 2 ? 'q' : 'p' })
  }
  steps.push({ step: 'sync', from: 'p', to: 'w' })
  steps.push({ step: 'sync', from: 'w', to: 'p' })
  return { replicas: ids.map(([n, id]) => replica(n, id)), steps }
}

// ---------------------------------------------------------------- delete sets (ENC-16..20, ENC-80)

const deleteSets = {
  replicas: [replica('a', 1), replica('b', 2), replica('c', 3, false)],
  steps: [
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'abcdefghijklmnop' },
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'sync', from: 'a', to: 'c' },
    { step: 'text.delete', doc: 'b', target: t, at: 0, len: 2 },
    { step: 'text.delete', doc: 'b', target: t, at: 2, len: 2 },
    { step: 'text.delete', doc: 'b', target: t, at: 6, len: 1 },
    { step: 'text.insert', doc: 'b', target: t, at: 3, value: 'XYZ' },
    { step: 'text.delete', doc: 'b', target: t, at: 4, len: 1 },
    { step: 'text.delete', doc: 'c', target: t, at: 10, len: 3 },
    { step: 'text.delete', doc: 'a', target: t, at: 5, len: 1 },
    { step: 'map.set', doc: 'a', target: m, key: 'k', value: 1 },
    { step: 'map.set', doc: 'a', target: m, key: 'k', value: 2 },
    { step: 'map.delete', doc: 'a', target: m, key: 'k' },
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'sync', from: 'b', to: 'a' },
    { step: 'sync', from: 'c', to: 'a' },
    { step: 'sync', from: 'a', to: 'c' },
    { step: 'sync', from: 'a', to: 'b' }
  ]
}

// ---------------------------------------------------------------- struct layouts for every content kind (ENC-10..15, ENC-27, ENC-74, ENC-76, ENC-81, ENC-85)

const structLayouts = {
  replicas: [replica('a', 1), replica('b', 2)],
  steps: [
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'hello world' },
    { step: 'text.format', doc: 'a', target: t, at: 0, len: 5, attributes: { bold: true } },
    { step: 'text.insertEmbed', doc: 'a', target: t, at: 5, value: { img: 'x.png' } },
    { step: 'map.set', doc: 'a', target: m, key: 'bin', value: bytes(0, 1, 2, 255) },
    { step: 'map.set', doc: 'a', target: m, key: 'num', value: 3.5 },
    { step: 'map.set', doc: 'a', target: m, key: 'num', value: 4 },
    { step: 'map.insertType', doc: 'a', target: m, key: 'child', kind: 'map', init: { x: 1 } },
    { step: 'array.push', doc: 'a', target: a, values: ['p', 'q', { o: 1 }] },
    { step: 'array.insertType', doc: 'a', target: a, at: 1, kind: 'text', init: 'inner' },
    { step: 'array.insertType', doc: 'a', target: a, at: 0, kind: 'array', init: [1, 2] },
    { step: 'sync', from: 'a', to: 'b' },
    // concurrent edits at the same position give origin and rightOrigin pairs
    { step: 'text.insert', doc: 'a', target: t, at: 3, value: 'AA' },
    { step: 'text.insert', doc: 'b', target: t, at: 3, value: 'BB' },
    { step: 'array.insert', doc: 'a', target: a, at: 2, values: ['fromA'] },
    { step: 'array.insert', doc: 'b', target: a, at: 2, values: ['fromB'] },
    { step: 'map.set', doc: 'a', target: m, key: 'conflict', value: 'a' },
    { step: 'map.set', doc: 'b', target: m, key: 'conflict', value: 'b' },
    { step: 'text.delete', doc: 'b', target: t, at: 0, len: 2 },
    { step: 'array.delete', doc: 'b', target: a, at: 0, len: 1 },
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'sync', from: 'b', to: 'a' }
  ]
}

// ---------------------------------------------------------------- merge, diff, convert edge cases (ENC-23..25, ENC-81..84)

const mergeBase = {
  replicas: [replica('a', 1), replica('b', 2)],
  steps: [
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'a' },
    { step: 'text.insert', doc: 'a', target: t, at: 1, value: 'b' },
    { step: 'sync', from: 'a', to: 'b' },
    { step: 'text.insert', doc: 'b', target: t, at: 2, value: 'c😀' },
    { step: 'text.delete', doc: 'a', target: t, at: 0, len: 1 },
    { step: 'array.insert', doc: 'a', target: a, at: 0, values: [1, 2] },
    { step: 'array.delete', doc: 'a', target: a, at: 1 },
    { step: 'text.insert', doc: 'a', target: t, at: 0, value: 'z' }
  ]
}

const gcBase = {
  replicas: [replica('g', 7), replica('h', 8, false)],
  steps: [
    { step: 'text.insert', doc: 'g', target: t, at: 0, value: 'abcdef' },
    { step: 'text.delete', doc: 'g', target: t, at: 1, len: 2 },
    { step: 'text.delete', doc: 'g', target: t, at: 2, len: 1 },
    { step: 'text.format', doc: 'g', target: t, at: 0, len: 1, attributes: { i: true } },
    { step: 'text.insertEmbed', doc: 'g', target: t, at: 1, value: { e: 1 } },
    { step: 'map.set', doc: 'g', target: m, key: 'k', value: 'one' },
    { step: 'map.set', doc: 'g', target: m, key: 'k', value: 'two' },
    { step: 'sync', from: 'g', to: 'h' }
  ]
}

const mergeEdgeCases = () => {
  const ua = updatesOf(mergeBase, 'a')
  const ub = updatesOf(mergeBase, 'b')
  const ua2 = updatesOf(mergeBase, 'a', 'v2')
  const ub2 = updatesOf(mergeBase, 'b', 'v2')
  const fullB = stateAfter(mergeBase, 'b')
  const fullB2 = stateAfter(mergeBase, 'b', 'v2')
  const ug = updatesOf(gcBase, 'g')
  const ug2 = updatesOf(gcBase, 'g', 'v2')
  return {
    replicas: [replica('r', 99)],
    steps: [
      // Skip structs: updates from client a with a gap between them
      { step: 'mergeUpdates', name: 'merge-gap-first-and-last', updates: [ua[0], ua[ua.length - 1]] },
      { step: 'mergeUpdates', name: 'merge-gap-v2', updates: [ua2[0], ua2[ua2.length - 1]], encoding: 'v2' },
      // ENC-82: a single input comes back as is
      { step: 'mergeUpdates', name: 'merge-single', updates: [ua[1]] },
      { step: 'mergeUpdates', name: 'merge-single-v2', updates: [ub2[0]], encoding: 'v2' },
      // overlapping and duplicated inputs
      { step: 'mergeUpdates', name: 'merge-overlap', updates: [...ua, ...ub, ...ua, fullB] },
      { step: 'mergeUpdates', name: 'merge-overlap-v2', updates: [...ua2, ...ub2, fullB2], encoding: 'v2' },
      { step: 'mergeUpdates', name: 'merge-deletes-and-gc', updates: ug },
      { step: 'mergeUpdates', name: 'merge-deletes-and-gc-v2', updates: ug2, encoding: 'v2' },
      // ENC-83: a state vector in the middle of a struct (after 'c', and between the halves of an astral pair)
      { step: 'diffUpdate', name: 'diff-mid-struct', update: fullB, sv: sv1(2, 1) },
      { step: 'diffUpdate', name: 'diff-between-surrogates', update: fullB, sv: sv1(2, 2) },
      { step: 'diffUpdate', name: 'diff-mid-struct-v2', update: fullB2, sv: sv1(2, 2), encoding: 'v2' },
      { step: 'diffUpdate', name: 'diff-empty-sv', update: fullB, sv: bytes(0) },
      { step: 'diffUpdate', name: 'diff-sv-beyond', update: fullB, sv: sv1(2, 50) },
      { step: 'diffUpdate', name: 'diff-unknown-client-sv', update: fullB, sv: sv1(1234, 5) },
      // ENC-25: conversion carries deletes, GC, formats and embeds across the formats
      { step: 'convertUpdate', name: 'convert-gc-v1-to-v2', update: stateAfter(gcBase, 'g'), from: 'v1' },
      { step: 'convertUpdate', name: 'convert-gc-v2-to-v1', update: stateAfter(gcBase, 'g', 'v2'), from: 'v2' },
      { step: 'convertUpdate', name: 'convert-nogc-v1-to-v2', update: stateAfter(gcBase, 'h'), from: 'v1' },
      { step: 'convertUpdate', name: 'convert-merged-gap', update: ua[ua.length - 1], from: 'v1' },
      { step: 'applyUpdate', doc: 'r', bytes: fullB },
      { step: 'text.insert', doc: 'r', target: t, at: 0, value: '!' }
    ]
  }
}

// ---------------------------------------------------------------- hostile extras (ENC-36, 44, 46..51, 60..69)

const hexOf = s => s.replace(/\s+/g, '')
const root = '01 01 74' // parentInfo 1 (root key), key "t"
const textStruct = (info, rest) => `01 01 01 00 ${info} ${rest} 00`
const roots = [['t', 'text']]
const c = (name, hex, extra = {}) => ({ name, bytes: hexOf(hex), roots, ...extra })
const v2 = (cols, rest) => {
  // flag, keyClock, client, leftClock, rightClock, info, string, parentInfo, typeRef, len, rest
  const order = ['keyClock', 'client', 'leftClock', 'rightClock', 'info', 'string', 'parentInfo', 'typeRef', 'len']
  // an empty column is length 00; the string column always holds at least the joined-string length prefix
  const col = k => {
    const hex = cols[k] ?? (k === 'string' ? '00' : '')
    return hex ? `${(hex.replace(/\s+/g, '').length / 2).toString(16).padStart(2, '0')} ${hex}` : '00'
  }
  return `00 ${order.map(col).join(' ')} ${rest}`
}
const v2case = (name, cols, rest, extra = {}) => c(name, v2(cols, rest), { encoding: 'v2', ...extra })


// JS accepts or partially applies where y-sharp rejects atomically (ADR 0005 strict decoding; Q15).
const lenient = { classification: 'diverge', divergence: 'Q15' }
const hostileExtra = [
  c('clock-2-pow-53-minus-1', '01 01 01 ff ff ff ff ff ff ff 0f 04 01 01 74 01 61 00'),
  c('json-content-undefined-text', textStruct('02', `${root} 01 09 75 6e 64 65 66 69 6e 65 64`)),
  c('any-tag-7d-then-continuation-eof', '01 01 01 00 08 01 01 74 01 7d 80'),
  c('any-tag-7d-last-byte', '01 01 01 00 08 01 01 74 01 7d', lenient),
  c('float32-truncated', textStruct('08', `${root} 01 7c 3f c0`)),
  c('float64-truncated', textStruct('08', `${root} 01 7b 3f f0 00`)),
  c('binary-length-beyond-buffer', textStruct('03', `${root} 05 01 02`)),
  c('parent-info-2', textStruct('04', '02 01 00 01 61')),
  c('parent-info-5', textStruct('04', '05 01 00 01 61')),
  c('delete-set-zero-deletes-for-client', '00 01 05 00'),
  // ENC-51: a delete range of length 0, which Yjs never writes. JS throws Unexpected case re-encoding it for an unknown client (and accepts
  // it for a known one); y-sharp rejects it everywhere (TASK-17.2)
  c('delete-set-len-0', '00 01 05 01 00 00', { classification: 'diverge', divergence: 'ENC-51', expect: ['Malformed'] }),
  c('delete-set-len-0-client-0', '00 01 00 01 00 00', { classification: 'diverge', divergence: 'ENC-51', expect: ['Malformed'] }),
  c('delete-set-len-0-after-a-range', '00 01 05 02 00 01 03 00', { classification: 'diverge', divergence: 'ENC-51', expect: ['Malformed'] }),
  c('delete-set-two-clients-one-truncated', '00 02 05 01 00 01 06'),
  // both accept; JS re-encodes the parked ranges with a negative V2 clock difference and re-exports (98, 99), (296, 1)
  c('delete-set-pending-out-of-order', '00 01 77 02 62 63 28 01', { classification: 'diverge', divergence: 'ENC-86', expect: ['ok'] }),
  // ENC-87: 'x' with the format b = 5n, then b = undefined. V2 carries a format value as Any; JS stores both and then cannot encode the
  // document in V1 (JSON.stringify throws on a BigInt and gives no text for undefined); y-sharp rejects them as V1 JSON cannot hold them
  c('v2-format-value-bigint', '00 02 00 02 02 41 01 02 00 02 00 05 06 00 84 00 86 07 04 74 62 78 62 41 02 01 01 00 00 01 03 00 7a 00 00 00 00 00 00 00 05 7e 00', { encoding: 'v2', classification: 'diverge', divergence: 'ENC-87', expect: ['Malformed'] }),
  c('v2-format-value-undefined', '00 02 00 02 02 41 01 02 00 02 00 05 06 00 84 00 86 07 04 74 62 78 62 41 02 01 01 00 00 01 03 00 7f 7e 00', { encoding: 'v2', classification: 'diverge', divergence: 'ENC-87', expect: ['Malformed'] }),
  // ENC-88: an item of length 0 ("" text, Deleted 0, an empty Any list) in root 't'. JS stores it, but it owns no clock and the state encode
  // drops it; y-sharp rejects it (found by SharpFuzz, TASK-4.43)
  c('item-zero-length-string', '01 01 01 00 04 01 01 74 00 00', { classification: 'diverge', divergence: 'ENC-88', expect: ['Malformed'] }),
  c('item-zero-length-deleted', '01 01 01 00 01 01 01 74 00 00', { classification: 'diverge', divergence: 'ENC-88', expect: ['Malformed'] }),
  c('item-zero-length-any', '01 01 01 00 08 01 01 74 00 00', { classification: 'diverge', divergence: 'ENC-88', expect: ['Malformed'] }),
  // Q07: Date, Map and ArrayBuffer reach the wire as the empty object; the bytes decode as an empty object
  c('any-empty-object-from-flattened-date', '01 01 01 00 28 01 01 6d 01 6b 01 76 00 00', { roots: [['m', 'map']] }),
  c('eof-after-struct-header', '01 01 01 00'),
  c('eof-in-num-structs', '01'),
  c('content-ref-0-gc-with-length', '01 01 01 00 00 03 00'),
  c('content-ref-10-skip-with-flags', '01 01 01 00 0a 02 00'),
  c('content-ref-10-skip-with-origin-flag', '01 01 01 00 8a 02 00'),
  c('content-ref-9-doc-truncated', textStruct('09', `${root} 01 61`)),
  // V2: exhausted columns repeat or return zero instead of failing (the strictness divergence of ADR 0005)
  v2case('v2-all-columns-empty', {}, '01 01 00 00'),
  v2case('v2-info-and-parent-info-only', { info: '04', parentInfo: '01' }, '01 01 00 00', lenient),
  v2case('v2-origin-read-from-empty-columns', { info: '84' }, '01 01 00 00'),
  v2case('v2-info-run-repeats-for-second-struct', { info: '04', parentInfo: '01' }, '01 02 00 00', lenient),
  c('v2-column-length-past-end', '00 00 00 00 00 ff 01', { encoding: 'v2' }),
  // the string column holds the joined text "a" and a length run of 5, longer than the text
  v2case('v2-string-length-beyond-joined', { info: '04', parentInfo: '01', string: '01 61 05' }, '01 01 00 00', lenient)
]

export default () => [
  {
    area: 'encoding',
    name: 'any-values',
    covers: ['ENC-06', 'ENC-07', 'ENC-72', 'Q01', 'Q02', 'Q03', 'Q04', 'Q05', 'Q06', 'Q08'],
    script: anyValues
  },
  {
    area: 'encoding',
    name: 'embeds-and-formats',
    covers: ['ENC-12', 'ENC-75', 'Q08', 'Q11', 'Q28', 'Q30'],
    script: embedsAndFormats
  },
  {
    area: 'encoding',
    name: 'strings-utf16',
    covers: ['ENC-05', 'ENC-73', 'Q16', 'Q17', 'Q18', 'Q19'],
    script: strings,
    truncation: [{ replica: 'a', encoding: 'v1' }, { replica: 'a', encoding: 'v2' }]
  },
  {
    area: 'encoding',
    name: 'ids-clocks-lengths',
    covers: ['ENC-02', 'ENC-03', 'ENC-04', 'ENC-08', 'ENC-09', 'ENC-70', 'ENC-71', 'ENC-74', 'ENC-77', 'ENC-78', 'ENC-79', 'Q12', 'Q13', 'Q14'],
    script: idsAndLengths
  },
  {
    area: 'encoding',
    name: 'delete-sets',
    covers: ['ENC-16', 'ENC-17', 'ENC-18', 'ENC-19', 'ENC-20', 'ENC-80'],
    script: deleteSets,
    observe: ['structs', 'ds']
  },
  {
    area: 'encoding',
    name: 'struct-layouts',
    covers: ['ENC-01', 'ENC-10', 'ENC-11', 'ENC-13', 'ENC-14', 'ENC-15', 'ENC-27', 'ENC-28', 'ENC-76', 'ENC-81', 'ENC-85', 'Q14'],
    script: structLayouts,
    observe: ['structs', 'ds'],
    truncation: [{ replica: 'a', encoding: 'v1' }, { replica: 'a', encoding: 'v2' }]
  },
  {
    area: 'encoding',
    name: 'merge-diff-convert-edges',
    covers: ['ENC-23', 'ENC-24', 'ENC-25', 'ENC-81', 'ENC-82', 'ENC-83', 'ENC-84'],
    script: mergeEdgeCases
  },
  {
    area: 'hostile',
    name: 'malformed-codec-extras',
    covers: ['ENC-36', 'ENC-44', 'ENC-46', 'ENC-47', 'ENC-48', 'ENC-49', 'ENC-50', 'ENC-51', 'ENC-60', 'ENC-61', 'ENC-62', 'ENC-63', 'ENC-64', 'ENC-65', 'ENC-66', 'ENC-67', 'ENC-68', 'ENC-69', 'ENC-22', 'ENC-56', 'ENC-86', 'ENC-87', 'ENC-88', 'Q07'],
    malformed: hostileExtra
  }
]
