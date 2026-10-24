// Live oracle server of the differential harness (ADR 0012): one long-lived child per test run, newline-delimited JSON on
// stdin/stdout, a schema handshake first, `console` redirected to stderr (which the C# side writes to the test log).
//
// This file only registers the module hooks that pin yjs/lib0/y-protocols to the submodules, then loads the real server; the hooks
// must be registered before the oracle graph is resolved, so the server module is imported dynamically.
import './lib/register.mjs'
await import('./lib/server.mjs')
