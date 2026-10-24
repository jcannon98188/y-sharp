// Stored malformed inputs (ENC-29..ENC-58, docs/research/encoding.md section 8.2). The generator records what the oracle
// does; classification ties each case to ADR 0010 (match | diverge with a divergence id | js-crash). Hex is spaced for reading.
import { replica, text } from '../lib/script.mjs'
import { stateAfter } from '../lib/authoring.mjs'

const t = text('t')
const roots = [['t', 'text']]
const hexOf = s => s.replace(/\s+/g, '')
const concat = (...parts) => Buffer.concat(parts.map(p => (typeof p === 'string' ? Buffer.from(hexOf(p), 'hex') : Buffer.from(p))))

const hi = { replicas: [replica('a', 1)], steps: [{ step: 'text.insert', doc: 'a', target: t, at: 0, value: 'hi' }] }
const validV1 = stateAfter(hi, 'a')
const validV2 = stateAfter(hi, 'a', 'v2')

// One client (id 1), one struct at clock 0, a ContentString-style struct into root text "t", empty delete set.
const textStruct = (info, parentAndContent) => `01 01 01 00 ${info} ${parentAndContent} 00`
const root = '01 01 74' // parentInfo 1 (root key), key "t"

const c = (name, bytes, extra = {}) => ({ name, bytes: typeof bytes === 'string' ? hexOf(bytes) : bytes, roots, ...extra })

export default [
  {
    area: 'hostile',
    name: 'malformed-v1-updates',
    covers: ['ENC-29', 'ENC-30', 'ENC-31', 'ENC-32', 'ENC-33', 'ENC-34', 'ENC-35', 'ENC-37', 'ENC-38', 'ENC-39', 'ENC-40', 'ENC-41', 'ENC-42', 'ENC-43', 'ENC-45', 'ENC-52', 'ENC-53', 'ENC-54'],
    malformed: [
      c('valid-reference', validV1),
      c('empty-bytes', ''),
      c('empty-update', '00 00'),
      c('valid-plus-trailing-junk', concat(validV1, 'ff ff')),
      c('num-structs-2-pow-32', '01 80 80 80 80 10 01 00'),
      c('num-structs-2-pow-32-minus-1-body-short', '01 ff ff ff ff 0f 01 00'),
      c('num-clients-2-pow-32-minus-1', 'ff ff ff ff 0f'),
      c('unterminated-varuint', '80 80 80 80 80 80 80 80 80 80 00'),
      c('client-id-2-pow-63', `01 01 80 80 80 80 80 80 80 80 80 01 00 04 ${root} 02 68 69 00`, { classification: 'diverge', divergence: 'Q12' }),
      c('content-ref-11', textStruct('0b', root)),
      c('unknown-type-ref-9', textStruct('07', `${root} 09`)),
      c('invalid-utf8-string', textStruct('04', `${root} 01 ff`)),
      c('cesu-surrogate-string', textStruct('04', `${root} 03 ed a0 80`)),
      c('bom-string', textStruct('04', `${root} 04 ef bb bf 61`)),
      c('json-content-syntax-error', textStruct('02', `${root} 01 02 7b 7b`)),
      c('any-tag-0x10', textStruct('08', `${root} 01 10`)),
      c('any-tag-0x73', textStruct('08', `${root} 01 73`)),
      c('any-tag-7d-last-byte', '01 01 01 00 08 01 01 74 01 7d'),
      c('delete-set-range-beyond-state', '00 01 05 01 00 01'),
      c('delete-set-truncated', '00 01 05 02 00 01'),
      c('delete-set-zero-deletes-for-client', '00 01 05 00'),
      c('v2-bytes-through-v1-applier', validV2),
      c('truncated-mid-struct', validV1.slice(0, 6)),
      c('truncated-before-delete-set', validV1.slice(0, validV1.length - 1))
    ]
  },
  {
    area: 'hostile',
    name: 'malformed-v2-updates',
    covers: ['ENC-54', 'ENC-55', 'ENC-56'],
    malformed: [
      c('valid-reference', validV2, { encoding: 'v2' }),
      c('v1-bytes-through-v2-applier', validV1, { encoding: 'v2' }),
      c('empty-bytes', '', { encoding: 'v2' }),
      c('column-length-beyond-buffer', '00 ff 01', { encoding: 'v2' }),
      c('feature-flag-set', concat('01', validV2.slice(1)), { encoding: 'v2' }),
      c('truncated-in-columns', validV2.slice(0, 8), { encoding: 'v2' })
    ]
  }
]
