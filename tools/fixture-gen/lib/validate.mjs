import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import Ajv2020 from 'ajv/dist/2020.js'
import { steps } from './steps.mjs'

const here = path.dirname(fileURLToPath(import.meta.url))
const load = n => JSON.parse(readFileSync(path.join(here, '..', 'schema', n), 'utf8'))

export const opsSchema = load('ops.schema.json')
export const fixtureSchema = load('fixture.schema.json')

const ajv = new Ajv2020({ allErrors: true, strict: false })
ajv.addSchema(opsSchema)
const validateFixture = ajv.compile(fixtureSchema)
const validateScript = ajv.getSchema(opsSchema.$id)

const fail = (what, errors) => {
  throw new Error(`${what} fails schema validation:\n${errors.slice(0, 8).map(e => `  ${e.instancePath || '/'} ${e.message}`).join('\n')}`)
}

export const checkScript = (script, what) => { if (!validateScript(script)) fail(what, validateScript.errors) }
export const checkFixture = (fixture, what) => { if (!validateFixture(fixture)) fail(what, validateFixture.errors) }

/** The schema's step list must equal the registered interpreter kinds, field for field (single normative vocabulary). */
export const checkSchemaMatchesRegistry = () => {
  const inSchema = new Map(opsSchema.$defs.step.oneOf.map(o => [o.properties.step.const, o]))
  const problems = []
  for (const [kind, def] of steps) {
    const o = inSchema.get(kind)
    if (!o) { problems.push(`step '${kind}' is registered but not in ops.schema.json`); continue }
    const fields = Object.keys(o.properties).filter(f => f !== 'step' && f !== 'steps').sort().join(',')
    const want = [...def.required, ...def.optional].sort().join(',')
    if (fields !== want) problems.push(`step '${kind}': schema fields [${fields}] != interpreter fields [${want}]`)
    const req = o.required.filter(f => f !== 'step' && f !== 'steps').sort().join(',')
    if (req !== [...def.required].sort().join(',')) problems.push(`step '${kind}': required fields differ`)
  }
  for (const kind of inSchema.keys()) if (!steps.has(kind)) problems.push(`step '${kind}' is in ops.schema.json but has no interpreter`)
  if (problems.length) throw new Error('ops.schema.json and the interpreter disagree:\n  ' + problems.join('\n  '))
}
