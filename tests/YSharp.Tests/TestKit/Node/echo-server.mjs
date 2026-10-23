// Test double for the ADR 0012 child: newline-delimited JSON, schema handshake, console redirected to stderr.
// Only used to test the C# NodeClient; the real oracle server belongs to the differential harness spec.
import readline from 'node:readline'

console.log = (...args) => console.error(...args)

const send = o => process.stdout.write(JSON.stringify(o) + '\n')
send({ hello: true, schema: 1, node: process.version, pid: process.pid })

const rl = readline.createInterface({ input: process.stdin })
rl.on('line', line => {
  const req = JSON.parse(line)
  switch (req.op) {
    case 'echo': return send({ id: req.id, ok: true, result: req.payload })
    case 'pid': return send({ id: req.id, ok: true, result: process.pid })
    case 'log': console.log('hello from node'); return send({ id: req.id, ok: true, result: null })
    case 'fail': return send({ id: req.id, ok: false, error: 'boom' })
    case 'hang': return // never answers: the client must time out, kill and restart us
    default: return send({ id: req.id, ok: false, error: 'unknown op ' + req.op })
  }
})
