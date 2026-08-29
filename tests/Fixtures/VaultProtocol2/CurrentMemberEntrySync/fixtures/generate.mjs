import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { buildFixtureFiles } from './build-fixtures.mjs'

const root = dirname(fileURLToPath(import.meta.url))
const registryRaw = await readFile(new URL('../../v1/registry.json', import.meta.url))
const registry = JSON.parse(registryRaw)
const policyRaw = await readFile(new URL('../../v2/current-member-entry-sync-policy.json', import.meta.url))

for (const [relativePath, content] of buildFixtureFiles(registry, registryRaw, policyRaw)) {
  const target = `${root}/${relativePath}`
  await mkdir(dirname(target), { recursive: true })
  await writeFile(target, content)
}

console.log('Generated deterministic current Member Entry sync fixtures.')
