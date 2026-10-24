// Fixed hostile corpus, one fixture per class (ADR 0013, TASK-2.11). Every case stores the bytes, the outcomes y-sharp may
// produce (`expect`: ok, Malformed, LimitExceeded, Rejected, UnknownMessage) and the oracle's recorded outcome. The gating suite
// replays every file in fixtures/hostile; adding a case is the whole regression test. Hand-written inputs; mutations are
// deterministic (every single-bit flip and so on of the two "hi" seeds), so the files never drift.
import { replica, text } from '../lib/script.mjs'
import { stateAfter } from '../lib/authoring.mjs'

const hi = { replicas: [replica('a', 1)], steps: [{ step: 'text.insert', doc: 'a', target: text('t'), at: 0, value: 'hi' }] }
const validV1 = stateAfter(hi, 'a')
const validV2 = stateAfter(hi, 'a', 'v2')
const hiV1Hex = Buffer.from(validV1).toString('hex')

// ---- byte helpers (hex strings; independent of lib0 so the cases do not depend on the oracle's encoder)
const vu = n => {
  let v = BigInt(n)
  const out = []
  while (v > 127n) { out.push(Number((v & 127n) | 128n)); v >>= 7n }
  out.push(Number(v))
  return Buffer.from(out).toString('hex')
}
const vs = s => { const b = Buffer.from(s, 'utf8'); return vu(b.length) + b.toString('hex') }
const hx = s => s.replace(/\s+/g, '')
const rep = (s, n) => s.repeat(n)

const blk = (client, clock, structs, n = structs.length) => vu(n) + vu(client) + vu(clock) + structs.join('')
const upd = (blocks, ds = '00', nClients = blocks.length) => vu(nClients) + blocks.join('') + ds
const T = '0101' + '74' // parentInfo 1, root key "t" (varuint length 1, then 't')
const textS = (s, extra = '') => '04' + T + vs(s) + extra
const textO = (client, clock, s) => '84' + vu(client) + vu(clock) + vs(s)
const gc = n => '00' + vu(n)
const skip = n => '0a' + vu(n)
const item = (info, tail) => info + T + tail
const ds = (...clients) => vu(clients.length) + clients.map(([c, ranges]) => vu(c) + vu(ranges.length) + ranges.map(([k, l]) => vu(k) + vu(l)).join('')).join('')
const au = (...entries) => vu(entries.length) + entries.map(([c, k, j]) => vu(c) + vu(k) + vs(j)).join('')
const sync = (type, payloadHex) => vu(type) + vu(payloadHex.length / 2) + payloadHex

const REJ = ['Malformed', 'LimitExceeded']
const ANY = ['ok', 'Malformed', 'LimitExceeded', 'Rejected']
const roots = [['t', 'text']]
const c = (name, bytes, extra = {}) => ({ name, bytes: typeof bytes === 'string' ? hx(bytes) : bytes, roots, expect: ANY, ...extra })

// ---- HOS-TRUNC: every proper prefix of the two "hi" seeds
const truncCases = []
for (let cut = 0; cut < validV1.length; cut++) truncCases.push(c(`v1-cut-${cut}`, validV1.slice(0, cut), { expect: ['Malformed'] }))
for (let cut = 0; cut < validV2.length; cut++) truncCases.push(c(`v2-cut-${cut}`, validV2.slice(0, cut), { encoding: 'v2', expect: ['Malformed'] }))

// ---- HOS-MUT: single-edit mutations of the seeds, deterministic and exhaustive
const mutCases = []
const flip = (bytes, i, bit) => { const b = Uint8Array.from(bytes); b[i] ^= 1 << bit; return b }
for (const [label, seed, encoding] of [['v1', validV1, 'v1'], ['v2', validV2, 'v2']]) {
  const extra = encoding === 'v2' ? { encoding } : {}
  for (let i = 0; i < seed.length; i++) {
    for (let bit = 0; bit < 8; bit++) mutCases.push(c(`${label}-flip-${i}-${bit}`, flip(seed, i, bit), extra))
    for (const v of [0x00, 0xff]) { const b = Uint8Array.from(seed); b[i] = v; mutCases.push(c(`${label}-set-${i}-${v.toString(16).padStart(2, '0')}`, b, extra)) }
    mutCases.push(c(`${label}-insert-${i}-ff`, Buffer.concat([seed.slice(0, i), Buffer.from([0xff]), seed.slice(i)]), extra))
    mutCases.push(c(`${label}-delete-${i}`, Buffer.concat([seed.slice(0, i), seed.slice(i + 1)]), extra))
  }
}
mutCases.push(c('v1-splice-head-of-v1-tail-of-v2', Buffer.concat([validV1.slice(0, 6), validV2.slice(8)])))
mutCases.push(c('v1-splice-doubled', Buffer.concat([validV1, validV1])))

// ---- HOS-LEN: oversized counts and lengths
const struct1 = body => upd([vu(1) + vu(1) + vu(0) + body])
const lenCases = [
  c('clients-2-pow-53', vu(2n ** 53n), { expect: REJ }),
  c('clients-2-pow-64-minus-1', rep('ff', 9) + '01', { expect: REJ }),
  c('clients-ten-byte-overflow', rep('ff', 9) + '7f', { expect: REJ }),
  c('structs-2-pow-53', '01' + vu(2n ** 53n) + '0100', { expect: REJ }),
  c('structs-2-pow-64-minus-1', '01' + rep('ff', 9) + '01' + '0100', { expect: REJ }),
  c('string-length-2-pow-30', struct1('04' + T + vu(2 ** 30)), { expect: REJ }),
  c('string-length-2-pow-53', struct1('04' + T + vu(2n ** 53n)), { expect: REJ }),
  c('json-count-2-pow-32-minus-1', struct1('02' + T + vu(2 ** 32 - 1)), { expect: REJ }),
  c('any-count-2-pow-32-minus-1', struct1('08' + T + vu(2 ** 32 - 1)), { expect: REJ }),
  c('any-array-length-2-pow-32', struct1('08' + T + '01' + '75' + vu(2 ** 32)), { expect: REJ }),
  c('any-object-keys-2-pow-32', struct1('08' + T + '01' + '76' + vu(2 ** 32)), { expect: REJ }),
  c('any-bytes-length-2-pow-31', struct1('08' + T + '01' + '74' + vu(2 ** 31)), { expect: REJ }),
  c('binary-length-2-pow-31', struct1('03' + T + vu(2 ** 31)), { expect: REJ }),
  c('deleted-length-2-pow-53', struct1('01' + T + vu(2n ** 53n)), { expect: ANY }),
  c('delete-set-clients-2-pow-32', upd([], vu(2 ** 32)), { expect: REJ }),
  c('delete-set-ranges-2-pow-53', upd([], '01' + '05' + vu(2n ** 53n)), { expect: REJ }),
  c('v2-column-length-2-pow-32', '00' + vu(2 ** 32), { encoding: 'v2', expect: REJ }),
  c('v2-column-length-2-pow-64-minus-1', '00' + rep('ff', 9) + '01', { encoding: 'v2', expect: REJ }),
  c('v2-string-column-length-2-pow-30', '00' + '00' + '00' + '00' + '00' + '00' + vu(5) + vu(2 ** 30) + '00', { encoding: 'v2', expect: REJ }),
  c('state-vector-clients-2-pow-32', vu(2 ** 32), { api: 'sv', expect: REJ }),
  c('state-vector-clients-2-pow-64-minus-1', rep('ff', 9) + '01', { api: 'sv', expect: REJ }),
  c('state-vector-client-clock-ten-byte-max', '01' + '01' + rep('ff', 9) + '01', { api: 'sv', expect: ANY.concat([]) })
]

// ---- HOS-CLOCK
const clockCases = [
  c('first-clock-above-0', upd([blk(1, 5, [textS('x')])]), { expect: ['ok'] }),
  c('duplicate-client-last-wins', upd([blk(1, 0, [textS('a')]), blk(1, 0, [textS('bb')])]), { expect: ['ok'] }),
  c('duplicate-client-with-gap', upd([blk(1, 0, [textS('a')]), blk(1, 5, [textS('b')])]), { expect: ['ok'] }),
  c('overlapping-blocks', upd([blk(1, 0, [textS('abc')]), blk(1, 1, [textS('xy')])])),
  c('clock-2-pow-31-minus-1', upd([blk(1, 2 ** 31 - 1, [textS('x')])]), { expect: ['ok'] }),
  c('clock-2-pow-31', upd([blk(1, 2 ** 31, [textS('x')])]), { expect: ['ok'] }),
  c('clock-2-pow-32', upd([blk(1, 2 ** 32, [textS('x')])]), { expect: ['ok'] }),
  c('clock-2-pow-53-minus-1', upd([blk(1, 2n ** 53n - 1n, [textS('x')])]), { expect: ['ok'] }),
  c('clock-2-pow-53', upd([blk(1, 2n ** 53n, [textS('x')])])),
  c('clock-2-pow-64-minus-1', upd([blk(1, 2n ** 64n - 1n, [textS('x')])])),
  c('clock-plus-length-overflows-2-pow-53', upd([blk(1, 2n ** 53n - 1n, [textS('ab')])])),
  c('clock-plus-gc-length-overflows', upd([blk(1, 2n ** 53n - 1n, [gc(2n ** 53n)])])),
  c('zero-length-string', upd([blk(1, 0, [textS('')])])),
  c('zero-length-gc', upd([blk(1, 0, [gc(0)])])),
  c('zero-length-skip', upd([blk(1, 0, [skip(0)])])),
  c('zero-length-deleted', upd([blk(1, 0, [item('01', vu(0))])])),
  c('client-2-pow-31', upd([blk(2 ** 31, 0, [textS('x')])]), { expect: ['ok'] }),
  c('client-2-pow-32-minus-1', upd([blk(2 ** 32 - 1, 0, [textS('x')])]), { expect: ['ok'] }),
  c('client-2-pow-32', upd([blk(2 ** 32, 0, [textS('x')])]), { expect: REJ }),
  c('client-2-pow-53', upd([blk(2n ** 53n, 0, [textS('x')])]), { expect: REJ }),
  c('skip-then-string-gap', upd([blk(1, 0, [skip(3), textS('x')])]), { expect: ANY })
]

// ---- HOS-REF
const chain = n => { // struct i has origin (1, i+1): every reference points forward within the same update
  const out = []
  for (let i = 0; i < n; i++) out.push(textO(1, i + 1, 'x'))
  return out
}
const refCases = [
  c('origin-missing-client', upd([blk(1, 0, [textO(99, 0, 'x')])]), { expect: ['ok', 'Rejected'] }),
  c('origin-self', upd([blk(1, 0, [textO(1, 0, 'x')])]), { expect: ['ok', 'Rejected', 'Malformed'] }),
  c('origin-future-same-client', upd([blk(1, 0, [textO(1, 5, 'x')])]), { expect: ['ok', 'Rejected', 'Malformed'] }),
  c('origin-cycle-two-clients', upd([blk(1, 0, [textO(2, 0, 'a')]), blk(2, 0, [textO(1, 0, 'b')])]), { expect: ['ok', 'Rejected'] }),
  c('origin-cycle-three-clients', upd([blk(1, 0, [textO(2, 0, 'a')]), blk(2, 0, [textO(3, 0, 'b')]), blk(3, 0, [textO(1, 0, 'c')])]), { expect: ['ok', 'Rejected'] }),
  c('right-origin-self', upd([blk(1, 0, [`44${vu(1)}${vu(0)}${T.slice(0, 0)}` + vs('x')])]), { expect: ANY }),
  c('parent-id-missing', upd([blk(1, 0, ['04' + '00' + vu(7) + vu(0) + vs('x')])]), { expect: ['ok', 'Rejected'] }),
  c('parent-is-self', upd([blk(1, 0, ['07' + '00' + vu(1) + vu(0) + '00'])]), { expect: ['ok', 'Rejected'] }),
  c('parent-cycle-two-type-items', upd([blk(1, 0, ['07' + '00' + vu(2) + vu(0) + '00']), blk(2, 0, ['07' + '00' + vu(1) + vu(0) + '00'])]), { expect: ['ok', 'Rejected'] }),
  c('parent-is-a-string-item', upd([blk(2, 0, ['04' + '00' + vu(1) + vu(0) + vs('x')])]), { before: [hiV1Hex], expect: ['Rejected'] }),
  c('parent-is-a-gc-struct', upd([blk(2, 0, ['04' + '00' + vu(1) + vu(0) + vs('x')])]), { before: [upd([blk(1, 0, [gc(1)])])], expect: ['ok', 'Rejected'] }),
  c('parent-is-a-skip-struct', upd([blk(2, 0, ['04' + '00' + vu(1) + vu(0) + vs('x')])]), { before: [upd([blk(1, 0, [skip(1)])])], expect: ['ok', 'Rejected'] }),
  c('forward-reference-chain-200', upd([blk(1, 0, chain(200))]), { expect: ['ok', 'Rejected'] }),
  c('dependents-of-a-rejected-struct', upd([blk(2, 0, ['04' + '00' + vu(1) + vu(0) + vs('x'), textO(2, 0, 'y')]), blk(3, 0, [textS('ok')])]), { before: [hiV1Hex], expect: ['Rejected'] })
]

// ---- HOS-SEM
const semCases = [
  c('content-ref-12', struct1(item('0c', '')), { expect: REJ }),
  c('content-ref-15', struct1(item('0f', '')), { expect: REJ }),
  c('content-ref-31', struct1(item('1f', '')), { expect: REJ }),
  c('type-ref-7', struct1(item('07', vu(7))), { expect: REJ }),
  c('type-ref-200', struct1(item('07', vu(200))), { expect: REJ }),
  c('string-low-surrogate-bytes', struct1(item('04', '03edb080')), { expect: REJ }),
  c('string-above-0x10ffff', struct1(item('04', '04f4908080')), { expect: REJ }),
  c('string-truncated-three-byte-sequence', struct1(item('04', '02e282')), { expect: REJ }),
  c('string-0xff-byte', struct1(item('04', '01ff')), { expect: REJ }),
  c('embed-invalid-json', struct1(item('05', vs('{{'))), { expect: REJ }),
  c('format-invalid-json', struct1(item('06', vs('b') + vs('nope'))), { expect: REJ }),
  c('doc-content-missing-options', struct1(item('09', vs('guid'))), { expect: REJ }),
  c('root-key-empty', upd([blk(1, 0, ['04' + '01' + '00' + vs('x')])]), { expect: ['ok', 'Malformed'] }),
  c('delete-set-unsorted-clients-and-ranges', upd([], ds([1, [[5, 1], [0, 1]]], [9, [[0, 1]]])), { expect: ['ok'] }),
  c('delete-set-overlapping-ranges', upd([], ds([1, [[0, 2], [1, 2]]])), { before: [hiV1Hex], expect: ['ok'] }),
  c('delete-over-unknown-client', upd([], ds([99, [[0, 5]]])), { expect: ['ok'] }),
  c('delete-zero-length-range', upd([], ds([5, [[0, 0]]])), { expect: ['ok', 'Malformed'] }),
  c('delete-range-beyond-known-state', upd([], ds([1, [[0, 5]]])), { before: [hiV1Hex], expect: ['ok'] })
]

// ---- HOS-AMP
const manyClients = n => { const b = []; for (let i = 1; i <= n; i++) b.push(blk(i, 0, [textS('x')])); return b }
const manyRanges = n => ds([7, Array.from({ length: n }, (_, i) => [i * 2, 1])])
const ampCases = [
  c('delete-range-2-pow-40-clocks', upd([], ds([1, [[0, 2 ** 40]]])), { before: [hiV1Hex], expect: ['ok', 'LimitExceeded', 'Malformed'] }),
  c('delete-range-2-pow-53-minus-1-clocks', upd([], ds([1, [[0, 2n ** 53n - 1n]]])), { before: [hiV1Hex], expect: ['ok', 'LimitExceeded', 'Malformed'] }),
  c('delete-set-5000-ranges-unknown-client', upd([], manyRanges(5000)), { expect: ['ok', 'LimitExceeded'] }),
  c('20000-one-clock-gc-structs', upd([blk(1, 0, Array.from({ length: 20000 }, () => gc(1)))]), { expect: ['ok', 'LimitExceeded', 'Malformed'] }),
  c('20000-one-clock-skip-structs', upd([blk(1, 0, Array.from({ length: 20000 }, () => skip(1)))]), { expect: ['ok', 'LimitExceeded', 'Malformed'] }),
  c('5000-clients-one-struct-each', upd(manyClients(5000)), { expect: ['ok', 'LimitExceeded'] }),
  c('5000-structs-waiting-on-missing-origins', upd([blk(1, 0, Array.from({ length: 5000 }, (_, i) => textO(2 ** 20 + i, 0, 'x')))]), { expect: ['ok', 'LimitExceeded', 'Rejected'] }),
  c('one-string-struct-with-5000-clock-skip-prefix', upd([blk(1, 0, [skip(5000), textS('x')])]), { expect: ANY })
]

// ---- HOS-PROTO (the oracle has no entry point for these; outcomes are y-sharp's contract, ADR 0014)
const hiLen = hiV1Hex.length / 2
const yo = upd([blk(2, 0, [textS('yo')])])
const ERRS = ['UnknownMessage', 'Malformed']
const sc = (name, bytes, extra) => c(name, bytes, { api: 'sync', ...extra })
const ac = (name, bytes, extra) => c(name, bytes, { api: 'awareness', ...extra })
const protoCases = [
  sc('sync-unknown-type-3', '0300', { expect: ERRS }),
  sc('sync-unknown-type-255', 'ff0100', { expect: ERRS }),
  sc('sync-type-2-pow-64-minus-1', rep('ff', 9) + '01' + '00', { expect: ERRS }),
  sc('sync-step2-length-overrun', '01050102', { expect: ['Malformed'] }),
  sc('sync-step2-length-2-pow-32', '01' + vu(2 ** 32), { expect: REJ }),
  sc('sync-step1-truncated-state-vector', '000501', { expect: ['Malformed'] }),
  sc('sync-update-with-empty-payload', '0200', { expect: ['Malformed'] }),
  sc('sync-update-valid', sync(2, hiV1Hex), { expect: ['ok'] }),
  sc('sync-concatenated-two-valid-frames', sync(2, hiV1Hex) + sync(2, yo), { expect: ['ok'] }),
  sc('sync-valid-frame-then-unknown-type', sync(2, hiV1Hex) + '0900', { expect: ERRS }),
  sc('sync-valid-frame-then-truncated-frame', sync(2, hiV1Hex) + '0208', { expect: ['Malformed'] }),
  sc('sync-step2-with-hostile-update', sync(1, 'ffffffff0f'), { expect: REJ }),
  sc('sync-step2-with-valid-update-and-trailing-byte', '01' + vu(hiLen + 1) + hiV1Hex + 'ff', { expect: ['ok'] }),
  ac('awareness-state-not-json', au([7, 1, 'not json']), { expect: ['Malformed'] }),
  ac('awareness-state-null', au([7, 1, 'null']), { expect: ['ok'] }),
  ac('awareness-state-number', au([7, 1, '42']), { expect: ['ok', 'Malformed'] }),
  ac('awareness-count-2-pow-32', vu(2 ** 32), { expect: REJ }),
  ac('awareness-string-length-overrun', '01' + '07' + '01' + 'ff01' + '7b', { expect: ['Malformed'] }),
  ac('awareness-invalid-utf8-state', '01' + '07' + '01' + '02' + 'fffe', { expect: ['Malformed'] }),
  ac('awareness-clock-regression', au([7, 5, '{"a":1}']), { after: [au([7, 3, '{"a":2}'])], expect: ['ok'] }),
  ac('awareness-same-client-twice-in-one-update', au([7, 1, '{"a":1}'], [7, 2, '{"a":2}']), { expect: ['ok'] }),
  ac('awareness-client-2-pow-53', au([2n ** 53n, 1, '{}']), { expect: ['ok', 'Malformed', 'LimitExceeded'] }),
  ac('awareness-nested-json-2000-deep', au([7, 1, rep('[', 2000) + rep(']', 2000)]), { expect: ['ok', 'Malformed', 'LimitExceeded'] }),
  ac('awareness-truncated-after-first-entry', au([7, 1, '{}']).replace(/^01/, '02'), { expect: ['Malformed'] })
]

const covers = (...ids) => [...ids, 'ERR-21']

export default [
  { area: 'hostile', name: 'hos-trunc', covers: covers('HOS-TRUNC', 'ERR-03'), malformed: truncCases },
  { area: 'hostile', name: 'hos-mut', covers: covers('HOS-MUT'), malformed: mutCases },
  { area: 'hostile', name: 'hos-len', covers: covers('HOS-LEN', 'ERR-09'), malformed: lenCases },
  { area: 'hostile', name: 'hos-clock', covers: covers('HOS-CLOCK'), malformed: clockCases },
  { area: 'hostile', name: 'hos-ref', covers: covers('HOS-REF', 'ERR-06', 'ERR-07'), malformed: refCases },
  { area: 'hostile', name: 'hos-sem', covers: covers('HOS-SEM', 'ERR-04'), malformed: semCases },
  { area: 'hostile', name: 'hos-amp', covers: covers('HOS-AMP'), malformed: ampCases },
  { area: 'hostile', name: 'hos-proto', covers: covers('HOS-PROTO'), malformed: protoCases }
]
