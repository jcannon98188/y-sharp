// The differential server (ADR 0012). Protocol catalogue (schema 1; the C# mirror is tests/YSharp.Interop.Tests/Harness/Protocol.cs):
//
//   server -> client, first line:  {"hello":true,"schema":1,"node":"v24.x.y","pid":n,"versions":{yjs,lib0,"y-protocols"},"ops":[...]}
//   client -> server:              {"id":n,"op":"<op>","payload":<json>}
//   server -> client:              {"id":n,"ok":true,"result":<json>}   or   {"id":n,"ok":false,"error":"<text>"}
//
//   ping      payload any                      result {pong:true, served}            liveness; `served` counts requests
//   run       payload {script, extras?}        result {status, ...}                  replay one stateless ops script on fresh docs
//               status "ok"            -> observations {updates,state,sv,doc,[results],[events],[structs],[ds],...}
//               status "script-error"  -> message      the script is invalid (bad vocabulary or an unresolved path); never a y-sharp failure
//               status "oracle-error"  -> name,message the oracle itself threw or misbehaved on a valid script (`oracle-misbehaved`, counted and
//                                                      saved by the harness, not a failure)
//   shutdown  no payload                       result null                           the server exits after answering
//
// Fault ops exist only when YSHARP_DIFF_FAULTS=1, so the harness can test its own hang and crash handling:
//   fault.hang   never answers (the client must time out, kill and restart the child)
//   fault.crash  exits the process with code 70 without answering
//   fault.spin   run-style request that busy-loops for payload.ms before answering
//
// Time: the oracle's clock is virtual while a script runs (oracle.mjs installs the Date.now indirection before any pinned code is
// imported; steps.mjs `tick` drives it; setTimeout/setInterval are virtual inside the steps that need them), so a response never
// depends on real time.
import readline from 'node:readline'
import { versions, repoRoot } from './oracle.mjs'
import { interpret } from './steps.mjs'
import './steps-doc.mjs'
import './protocol-steps.mjs'
import './steps-sim.mjs'
import './steps-foreign.mjs'
import { rootsOf } from './script.mjs'
import { observe } from './observe.mjs'
import { checkScript, checkSchemaMatchesRegistry } from './validate.mjs'
import { ScriptError } from './tagged.mjs'

// stdout is the protocol channel: anything the oracle prints goes to stderr
for (const level of ['log', 'info', 'warn', 'debug']) console[level] = (...args) => console.error(...args)

const faults = process.env.YSHARP_DIFF_FAULTS === '1'
const EXTRAS = new Set(['structs', 'ds'])
const OPS = ['ping', 'run', 'shutdown', ...(faults ? ['fault.hang', 'fault.crash', 'fault.spin'] : [])]

checkSchemaMatchesRegistry()

const send = o => process.stdout.write(JSON.stringify(o) + '\n')
let served = 0

const run = payload => {
  const script = payload?.script
  if (script === null || typeof script !== 'object') return { status: 'script-error', message: 'run needs a payload {script}' }
  const extras = payload.extras ?? []
  if (!Array.isArray(extras) || extras.some(e => !EXTRAS.has(e))) return { status: 'script-error', message: `extras must be a subset of ${[...EXTRAS]}` }
  try {
    checkScript(script, 'script')
  } catch (e) {
    return { status: 'script-error', message: String(e.message) }
  }
  try {
    const roots = rootsOf(script)
    const ctx = interpret(script)
    return { status: 'ok', observations: observe(ctx, roots, extras) }
  } catch (e) {
    if (e instanceof ScriptError) return { status: 'script-error', message: e.message }
    return { status: 'oracle-error', name: e?.constructor?.name ?? 'Error', message: String(e?.message ?? e) }
  }
}

const handle = req => {
  switch (req.op) {
    case 'ping': return { pong: true, served }
    case 'run': return run(req.payload)
    case 'shutdown': setImmediate(() => process.exit(0)); return null
    case 'fault.hang': if (faults) return undefined; break
    case 'fault.crash': if (faults) process.exit(70); break
    case 'fault.spin': if (faults) { const end = Date.now() + (req.payload?.ms ?? 1000); while (Date.now() < end); return { status: 'ok', observations: {} } } break
  }
  throw new Error('unknown op ' + req.op)
}

send({ hello: true, schema: 1, node: process.version, pid: process.pid, root: repoRoot, versions, ops: OPS })

const rl = readline.createInterface({ input: process.stdin })
rl.on('line', line => {
  if (line.trim().length === 0) return
  let req
  try { req = JSON.parse(line) } catch (e) { return send({ id: null, ok: false, error: 'bad json: ' + e.message }) }
  served++
  try {
    const result = handle(req)
    if (result === undefined) return // fault.hang: no answer
    send({ id: req.id, ok: true, result })
  } catch (e) {
    send({ id: req.id, ok: false, error: String(e.message ?? e) })
  }
})
rl.on('close', () => process.exit(0))
process.on('uncaughtException', e => { console.error('uncaught', e); process.exit(70) })
