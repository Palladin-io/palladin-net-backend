import { buildFixtureData as buildProtocolFixtureData } from '../v2/build-fixtures.mjs'
import {
  b64u,
  canonicalJson,
  concat,
  decryptXChaCha20,
  deterministicSeal,
  encodeAad,
  encryptXChaCha20,
  fromB64u,
  fromHex,
  hex,
  sha256,
  syntheticBytes,
  u16be,
  u32be,
  u64be,
  utf8,
  uuidBytes,
} from '../v2/lib.mjs'

const ids = {
  organizationId: '11111111-1111-4111-8111-111111111111',
  otherOrganizationId: '10101010-1010-4010-8010-101010101010',
  vaultId: '22222222-2222-4222-8222-222222222222',
  otherVaultId: '12121212-1212-4212-8212-121212121212',
  entryId: '33333333-3333-4333-8333-333333333333',
  secondEntryId: '34343434-3434-4434-8434-343434343434',
  memberId: '44444444-4444-4444-8444-444444444444',
  otherMemberId: '45454545-4545-4545-8545-454545454545',
}

const pageBoundaryEntryIds = [
  ids.entryId,
  ids.secondEntryId,
  '35353535-3535-4535-8535-353535353535',
  '36363636-3636-4636-8636-363636363636',
  '37373737-3737-4737-8737-373737373737',
  '38383838-3838-4838-8838-383838383838',
]

const pretty = (value) => `${JSON.stringify(value, null, 2)}\n`
const clone = (value) => structuredClone(value)
const suite = 'palladin-vault-xchacha-v1'
const wrapperSuite = 'palladin-x25519-sealed-box-v1'
const memberRecipientBinding = {
  keyVersion: 2,
  fingerprint: 'kMtix1fwHouJJYm3dfCyTe28XAG7lT3aYS18rqPZKBM',
}

const purposeByType = {
  MemberIndexEnvelope: 'memberIndex',
  MemberSecretEnvelope: 'memberSecret',
  VaultEntryKey: 'entryDekByVaultKey',
}

const operationName = {
  1: 'created',
  2: 'updated',
  3: 'archived',
  4: 'restored',
  5: 'deleted',
}

function scope(organizationId, vaultId, entryId = null, memberId = null) {
  return {
    organizationId,
    vaultId,
    entryId,
    grantOrRequestId: null,
    agentId: null,
    memberId,
  }
}

function encodedSuitePayload(vector) {
  return b64u(concat(
    fromB64u(vector.envelope.header.nonce),
    fromB64u(vector.envelope[vector.ciphertextField]),
  ))
}

function toWireEnvelope(vector) {
  const envelope = vector.envelope
  const binding = vector.type === 'VaultEntryKey'
    ? { wrappingVaultKeyVersion: envelope.wrappingKeyVersion }
    : vector.type === 'MemberSecretEnvelope'
      ? { operation: operationName[envelope.operation] }
      : {}
  return {
    descriptor: {
      protocolVersion: envelope.header.protocolVersion,
      cryptoSuiteId: suite,
      purpose: purposeByType[vector.type],
      scope: scope(envelope.organizationId, envelope.vaultId, envelope.entryId),
      resourceRevision: envelope.header.resourceRevision,
      keyVersion: envelope.header.keyVersion,
      memberKeyGeneration: envelope.header.memberKeyGeneration,
      binding,
    },
    encodedSuitePayload: encodedSuitePayload(vector),
  }
}

function toWireMemberVaultKey(vector) {
  const envelope = vector.envelope
  const vaultKey = fromB64u(JSON.parse(vector.plaintextCanonical).vaultKey)
  const fingerprint = fromB64u(envelope.recipientMemberKeyFingerprint)
  const wrapperContext = concat(
    utf8('PLDNX2W1'),
    u16be(2),
    u16be(utf8(wrapperSuite).length),
    utf8(wrapperSuite),
    u16be(1),
    u16be(35),
    uuidBytes(envelope.organizationId),
    uuidBytes(envelope.vaultId),
    uuidBytes(envelope.memberId),
    u64be(envelope.vkVersion),
    u32be(envelope.vkVersion),
    Uint8Array.of(1),
    u32be(envelope.memberKeyGeneration),
    u16be(5),
    u32be(envelope.recipientMemberKeyVersion),
    fingerprint,
    Uint8Array.of(0),
  )
  const contextHash = sha256(concat(utf8('PLDNX2CTX'), wrapperContext))
  const plaintextPackage = concat(utf8('PLDNX2K1'), vaultKey, contextHash)
  const sealedPackage = deterministicSeal(
    plaintextPackage,
    fromHex(vector.recipientPublicKeyHex),
    syntheticBytes('cvt-557:member-vault-key:ephemeral-seed'),
  )
  if (plaintextPackage.length !== 72 || sealedPackage.length !== 120) {
    throw new Error('descriptor-bound Member Vault key package size changed')
  }
  return {
    envelope: {
      wrappedVaultKey: {
        descriptor: {
          protocolVersion: envelope.protocolVersion,
          wrapperSuiteId: wrapperSuite,
          purpose: 'memberVaultKey',
          scope: scope(envelope.organizationId, envelope.vaultId, null, envelope.memberId),
          resourceRevision: String(envelope.vkVersion),
          wrappedKeyVersion: envelope.vkVersion,
          memberKeyGeneration: envelope.memberKeyGeneration,
          recipientKeyKind: 'memberX25519',
          recipientKeyVersion: envelope.recipientMemberKeyVersion,
          recipientFingerprint: envelope.recipientMemberKeyFingerprint,
          parentDescriptorHash: null,
        },
        encodedSealedKeyPackage: b64u(sealedPackage),
      },
    },
    evidence: {
      vaultKeyHex: hex(vaultKey),
      wrapperContextHex: hex(wrapperContext),
      contextHashHex: hex(contextHash),
      plaintextPackageHex: hex(plaintextPackage),
      recipientPublicKeyHex: vector.recipientPublicKeyHex,
      recipientPrivateKeyHex: vector.recipientPrivateKeyHex,
    },
  }
}

function headerFor(registry, type, resourceRevision, keyVersion, memberKeyGeneration, nonce) {
  const envelopeBinding = registry.envelopeBindings.find((candidate) => candidate.type === type)
  const resourceKind = registry.resourceKinds.find((kind) => kind.name === envelopeBinding.resourceKind).id
  const projectionKind = registry.projectionKinds.find((kind) => kind.name === envelopeBinding.projectionKind).id
  return {
    protocolVersion: 2,
    algorithmSuite: 1,
    resourceKind,
    projectionKind,
    resourceRevision,
    keyVersion,
    memberKeyGeneration,
    nonce: b64u(nonce),
  }
}

function makeAeadVector({
  registry,
  id,
  type,
  profile,
  key,
  plaintext,
  resourceRevision,
  keyVersion,
  memberKeyGeneration,
  operation,
  wrappingKeyVersion,
  ciphertextField = 'ciphertext',
  entryId = ids.entryId,
}) {
  const nonce = syntheticBytes(`cvt-557:${id}:nonce`, 24)
  const header = headerFor(registry, type, resourceRevision, keyVersion, memberKeyGeneration, nonce)
  const envelope = {
    organizationId: ids.organizationId,
    vaultId: ids.vaultId,
    entryId,
    ...(type === 'MemberIndexEnvelope' ? { memberIndexRevision: resourceRevision } : {}),
    ...(type === 'MemberSecretEnvelope' ? { revision: resourceRevision, operation } : {}),
    ...(type === 'VaultEntryKey' ? {
      wrapperRevision: resourceRevision,
      keyVersion,
      memberKeyGeneration,
      wrappingKeyVersion,
    } : {}),
    header,
  }
  const context = {
    ...ids,
    entryId,
    ...(type === 'MemberIndexEnvelope' ? { memberIndexRevision: resourceRevision } : {}),
    ...(type === 'MemberSecretEnvelope' ? { revision: resourceRevision, operation } : {}),
    ...(type === 'VaultEntryKey' ? { wrapperRevision: resourceRevision, keyVersion, memberKeyGeneration, wrappingKeyVersion } : {}),
    header,
  }
  const plaintextBytes = plaintext instanceof Uint8Array ? plaintext : utf8(canonicalJson(plaintext))
  const aad = encodeAad(registry, profile, context)
  const ciphertext = encryptXChaCha20(plaintextBytes, aad.bytes, nonce, key)
  envelope[ciphertextField] = b64u(ciphertext)
  return {
    id,
    type,
    aadProfile: profile,
    decryptionKeyHex: hex(key),
    plaintextCanonical: plaintext instanceof Uint8Array ? null : canonicalJson(plaintext),
    plaintextHex: hex(plaintextBytes),
    nonceHex: hex(nonce),
    aadHex: hex(aad.bytes),
    ciphertextField,
    envelope,
  }
}

function accessContext(overrides = {}) {
  return {
    contextVersion: 1,
    principalId: ids.memberId,
    organizationId: ids.organizationId,
    organizationMembershipGeneration: '7',
    vaultId: ids.vaultId,
    memberId: ids.memberId,
    memberKeyGeneration: 4,
    vaultKeyVersion: 3,
    memberRecipientKeyVersion: memberRecipientBinding.keyVersion,
    memberRecipientKeyFingerprint: memberRecipientBinding.fingerprint,
    offlinePolicy: '24h',
    offlinePolicyVersion: 1,
    issuedAt: '2026-08-29T08:00:00Z',
    notAfter: '2026-08-30T08:00:00Z',
    ...overrides,
  }
}

function headItem(entryKey, memberIndex, memberSecret, overrides = {}) {
  return {
    entryId: memberSecret.descriptor.scope.entryId,
    kind: 'head',
    state: 'active',
    updatedAt: '2026-08-29T08:00:00Z',
    currentRevision: memberSecret.descriptor.resourceRevision,
    memberIndexRevision: memberIndex.descriptor.resourceRevision,
    currentKeyVersion: entryKey.descriptor.keyVersion,
    entryKey,
    memberIndex,
    memberSecret,
    ...overrides,
  }
}

function tombstone(entryId = ids.entryId) {
  return {
    entryId,
    kind: 'tombstone',
    state: null,
    updatedAt: null,
    currentRevision: null,
    memberIndexRevision: null,
    currentKeyVersion: null,
    entryKey: null,
    memberIndex: null,
    memberSecret: null,
  }
}

function makeLargeSecret(registry, baseSecretVector, overrides = {}) {
  const content = {
    v: 2,
    username: 'fixture-user',
    password: 'not-a-real-password',
    url: 'https://fixture.invalid',
    fields: [],
    notes: '',
  }
  const payload = {
    entryType: 1,
    memberLabel: 'Large synthetic credential',
    description: 'Deterministic near-limit MemberSecret fixture',
    agentLabel: 'Large synthetic credential',
    content,
    agentVisibilityPolicy: {
      schemaVersion: 1,
      discoveryEnabled: false,
      fields: [
        { fieldId: 'common.agent-label', access: 1 },
        { fieldId: 'common.capabilities', access: 1 },
        { fieldId: 'common.description', access: 0 },
        { fieldId: 'common.entry-type', access: 1 },
        { fieldId: 'common.icon-reference', access: 0 },
        { fieldId: 'common.member-label', access: 0 },
        { fieldId: 'common.search-fields', access: 0 },
        { fieldId: 'credential.notes', access: 2 },
        { fieldId: 'credential.password', access: 2 },
        { fieldId: 'credential.totp', access: 3 },
        { fieldId: 'credential.url', access: 2 },
        { fieldId: 'credential.url-domain', access: 1 },
        { fieldId: 'credential.username', access: 1 },
      ],
    },
  }
  const targetPlaintextBytes = 262128
  const emptyBytes = utf8(canonicalJson(payload)).length
  content.notes = 'x'.repeat(targetPlaintextBytes - emptyBytes)
  if (utf8(canonicalJson(payload)).length !== targetPlaintextBytes) {
    throw new Error('large MemberSecret plaintext did not reach the exact 262128-byte boundary')
  }
  return makeAeadVector({
    registry,
    id: overrides.id ?? 'near-limit-member-secret',
    type: 'MemberSecretEnvelope',
    profile: 'member-secret',
    key: overrides.key ?? fromHex(baseSecretVector.decryptionKeyHex),
    plaintext: payload,
    resourceRevision: overrides.resourceRevision ?? '13',
    keyVersion: overrides.keyVersion ?? 5,
    memberKeyGeneration: overrides.memberKeyGeneration ?? 4,
    operation: overrides.operation ?? 2,
    entryId: overrides.entryId ?? ids.entryId,
  })
}

function makeCompleteItemData(registry, baseVectors, {
  entryId,
  id,
  currentRevision,
  memberIndexRevision = currentRevision,
  keyVersion = 5,
  memberKeyGeneration = 4,
  wrappingVaultKeyVersion = 3,
  operation = 2,
  state = 'active',
  largeSecret = false,
}) {
  const entryDek = syntheticBytes(`cvt-557:${id}:entry-dek`, 32)
  const entryKeyVector = makeAeadVector({
    registry,
    id: `${id}:entry-key`,
    type: 'VaultEntryKey',
    profile: 'entry-key-wrapper',
    key: fromHex(baseVectors.entryKeyVector.decryptionKeyHex),
    plaintext: entryDek,
    resourceRevision: currentRevision,
    keyVersion,
    memberKeyGeneration,
    wrappingKeyVersion: wrappingVaultKeyVersion,
    entryId,
  })
  const memberIndexVector = makeAeadVector({
    registry,
    id: `${id}:member-index`,
    type: 'MemberIndexEnvelope',
    profile: 'member-index',
    key: entryDek,
    plaintext: JSON.parse(baseVectors.memberIndexVector.plaintextCanonical),
    resourceRevision: memberIndexRevision,
    keyVersion,
    memberKeyGeneration,
    entryId,
  })
  const memberSecretVector = largeSecret
    ? makeLargeSecret(registry, baseVectors.memberSecretVector, {
      id: `${id}:member-secret`,
      key: entryDek,
      resourceRevision: currentRevision,
      keyVersion,
      memberKeyGeneration,
      operation,
      entryId,
    })
    : makeAeadVector({
      registry,
      id: `${id}:member-secret`,
      type: 'MemberSecretEnvelope',
      profile: 'member-secret',
      key: entryDek,
      plaintext: JSON.parse(baseVectors.memberSecretVector.plaintextCanonical),
      resourceRevision: currentRevision,
      keyVersion,
      memberKeyGeneration,
      operation,
      entryId,
    })
  return {
    item: headItem(
      toWireEnvelope(entryKeyVector),
      toWireEnvelope(memberIndexVector),
      toWireEnvelope(memberSecretVector),
      {
        state,
        updatedAt: `2026-08-29T08:${String(Number(currentRevision) % 60).padStart(2, '0')}:00Z`,
      },
    ),
    vectors: { entryKeyVector, memberIndexVector, memberSecretVector },
  }
}

const makeCompleteItem = (...args) => makeCompleteItemData(...args).item

function wireSnapshot(memberVaultKey, item, overrides = {}) {
  return {
    snapshotBaseSequence: '12',
    accessContext: accessContext(),
    memberVaultKey,
    items: [item],
    nextCursor: null,
    ...overrides,
  }
}

function wireDelta(memberVaultKey, items, {
  deltaUpperBound,
  appliedThroughSequence = deltaUpperBound,
  continuationCursor = null,
  context = accessContext(),
} = {}) {
  return {
    deltaUpperBound,
    appliedThroughSequence,
    accessContext: context,
    memberVaultKey,
    items,
    continuationCursor,
  }
}

const encodedResponseBytes = (value) => utf8(JSON.stringify(value)).length

function authoritativeEntryHead(item) {
  return {
    entryId: item.entryId,
    kind: item.kind,
    state: item.state,
    updatedAt: item.updatedAt,
    currentRevision: item.currentRevision,
    memberIndexRevision: item.memberIndexRevision,
    currentKeyVersion: item.currentKeyVersion,
  }
}

function responseAuthority(baseAuthority, items, overrides = {}) {
  return {
    ...baseAuthority,
    ...overrides,
    authoritativeEntryHeads: items.map(authoritativeEntryHead),
  }
}

function deltaAuthority(baseAuthority, items, currentSequence, afterSequence, overrides = {}) {
  return responseAuthority(baseAuthority, items, {
    authoritativeCurrentMemberSequence: currentSequence,
    authoritativeMinRetainedMemberSequence: '1',
    requestAfterSequence: afterSequence,
    requestPageSize: 100,
    deltaRequestKind: 'initial',
    authenticatedCursorDeltaUpperBound: null,
    ...overrides,
  })
}

export function buildFixtureData(registry, registryRaw, policyRaw) {
  const base = buildProtocolFixtureData(registry, registryRaw)
  const envelopeVectors = base.vectorObjects.get('vectors/envelopes.json')
  const memberIndexVector = envelopeVectors.aeadVectors.find((vector) => vector.id === 'member-index')
  const memberSecretVector = envelopeVectors.aeadVectors.find((vector) => vector.id === 'member-secret')
  const entryKeyVector = envelopeVectors.aeadVectors.find((vector) => vector.id === 'vault-entry-key')
  const memberVaultKeyVector = envelopeVectors.sealedBoxVectors.find((vector) => vector.id === 'member-vault-key')
  const completeBaseVectors = { memberIndexVector, memberSecretVector, entryKeyVector }
  const memberVaultKeyData = toWireMemberVaultKey(memberVaultKeyVector)
  const memberVaultKey = memberVaultKeyData.envelope
  if (memberVaultKey.wrappedVaultKey.descriptor.recipientKeyVersion !== memberRecipientBinding.keyVersion
    || memberVaultKey.wrappedVaultKey.descriptor.recipientFingerprint !== memberRecipientBinding.fingerprint) {
    throw new Error('synthetic Member recipient binding changed')
  }
  const validItemData = makeCompleteItemData(registry, completeBaseVectors, {
    entryId: ids.entryId,
    id: 'valid-current-head',
    currentRevision: '12',
  })
  const validItem = validItemData.item
  const baseRequestAuthority = {
    authenticatedPrincipalId: ids.memberId,
    authenticatedOrganizationId: ids.organizationId,
    currentOrganizationMembershipGeneration: '7',
    routeVaultId: ids.vaultId,
    currentStructuralMemberId: ids.memberId,
    authoritativeMemberKeyGeneration: 4,
    authoritativeCurrentVaultKeyVersion: 3,
    authoritativeMemberRecipientKeyVersion: memberVaultKey.wrappedVaultKey.descriptor.recipientKeyVersion,
    authoritativeMemberRecipientFingerprint: memberVaultKey.wrappedVaultKey.descriptor.recipientFingerprint,
    authoritativeOfflinePolicy: '24h',
    authoritativeOfflinePolicyVersion: 1,
    clientCryptographicAuthority: {
      expectedCurrentVaultKeyHex: memberVaultKeyData.evidence.vaultKeyHex,
      source: 'synthetic-client-current-vault-key-trust-anchor',
    },
    validationTime: '2026-08-29T08:30:00Z',
  }
  const validSnapshot = {
    contract: 'palladin-current-member-entry-sync-valid-snapshot-fixture',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    syntheticDataOnly: true,
    requestAuthority: responseAuthority(baseRequestAuthority, [validItem]),
    response: wireSnapshot(memberVaultKey, validItem),
    cryptoEvidence: {
      baseFixture: '../v2/vectors/envelopes.json',
      sourceVectorIds: ['member-vault-key', 'vault-entry-key', 'member-index', 'member-secret'],
      generatedVectorIds: Object.values(validItemData.vectors).map((vector) => vector.id),
      expectedMemberIndexPlaintext: JSON.parse(memberIndexVector.plaintextCanonical),
      expectedMemberSecretPlaintext: JSON.parse(memberSecretVector.plaintextCanonical),
      expectedEntryDekHex: validItemData.vectors.entryKeyVector.plaintextHex,
      expectedVaultKeyHex: memberVaultKeyData.evidence.vaultKeyHex,
      expectedWrapperContextHex: memberVaultKeyData.evidence.wrapperContextHex,
      expectedContextHashHex: memberVaultKeyData.evidence.contextHashHex,
      expectedPlaintextPackageHex: memberVaultKeyData.evidence.plaintextPackageHex,
    },
  }

  const largeItemData = makeCompleteItemData(registry, completeBaseVectors, {
    entryId: ids.entryId,
    id: 'near-limit-current-head',
    currentRevision: '13',
    largeSecret: true,
  })
  const largeItem = largeItemData.item
  const largeSecretVector = largeItemData.vectors.memberSecretVector
  const largeSecret = largeItem.memberSecret
  const largeFixture = {
    contract: 'palladin-current-member-entry-sync-near-limit-member-secret-fixture',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    syntheticDataOnly: true,
    decodedMemberSecretCiphertextBytes: fromB64u(largeSecretVector.envelope.ciphertext).length,
    decodedMemberSecretEncodedSuitePayloadBytes: fromB64u(largeSecret.encodedSuitePayload).length,
    plaintextBytes: fromHex(largeSecretVector.plaintextHex).length,
    requestAuthority: responseAuthority(baseRequestAuthority, [largeItem]),
    response: wireSnapshot(memberVaultKey, largeItem, { snapshotBaseSequence: '13' }),
    fixtureDecryptionKeyHex: largeSecretVector.decryptionKeyHex,
    expectedPlaintextSha256: hex(sha256(fromHex(largeSecretVector.plaintextHex))),
    aadHex: largeSecretVector.aadHex,
  }

  const controls = {
    contract: 'palladin-current-member-entry-sync-control-fixtures',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    tombstoneDelta: {
      requestAuthority: deltaAuthority(baseRequestAuthority, [tombstone()], '14', '13'),
      response: wireDelta(memberVaultKey, [tombstone()], { deltaUpperBound: '14' }),
    },
    reset: {
      httpStatus: 409,
      response: {
        outcome: 'resetRequired',
        currentSequence: '14',
        minRetainedSequence: '13',
        newSnapshotRequired: true,
      },
      expectedClientAction: 'delete-active-generation-cancel-staging-start-fresh-snapshot',
    },
  }

  const boundaryCandidates = pageBoundaryEntryIds.map((entryId, index) => makeCompleteItem(registry, completeBaseVectors, {
    entryId,
    id: `page-boundary-${index + 1}`,
    currentRevision: String(20 + index),
    largeSecret: true,
  }))
  const boundaryIncludedItems = []
  let boundaryOmittedItem = null
  for (const candidate of boundaryCandidates) {
    const candidateResponse = wireSnapshot(memberVaultKey, candidate, {
      snapshotBaseSequence: '25',
      items: [...boundaryIncludedItems, candidate],
      nextCursor: `cvt-557-synthetic-cursor-after-${candidate.entryId}`,
    })
    if (boundaryIncludedItems.length > 0 && encodedResponseBytes(candidateResponse) > 2097152) {
      boundaryOmittedItem = candidate
      break
    }
    boundaryIncludedItems.push(candidate)
  }
  if (boundaryOmittedItem === null) throw new Error('page-boundary candidates did not cross the operational response budget')
  const boundaryResponse = wireSnapshot(memberVaultKey, boundaryIncludedItems[0], {
    snapshotBaseSequence: '25',
    items: boundaryIncludedItems,
    nextCursor: `cvt-557-synthetic-cursor-after-${boundaryIncludedItems.at(-1).entryId}`,
  })
  const boundaryResponseWithOmittedCandidate = {
    ...boundaryResponse,
    items: [...boundaryIncludedItems, boundaryOmittedItem],
  }
  const safePrefixRow23 = makeCompleteItem(registry, completeBaseVectors, {
    entryId: pageBoundaryEntryIds[0], id: 'safe-prefix-row-23', currentRevision: '32',
  })
  const safePrefixRow24 = makeCompleteItem(registry, completeBaseVectors, {
    entryId: pageBoundaryEntryIds[0], id: 'safe-prefix-row-24', currentRevision: '33',
  })
  const safePrefixRow25 = makeCompleteItem(registry, completeBaseVectors, {
    entryId: pageBoundaryEntryIds[1], id: 'safe-prefix-row-25', currentRevision: '34',
  })
  const safePrefixRow26 = makeCompleteItem(registry, completeBaseVectors, {
    entryId: pageBoundaryEntryIds[2], id: 'safe-prefix-row-26', currentRevision: '35',
  })
  const safePrefixJournalRows = [
    { memberSequence: '23', completeItem: safePrefixRow23 },
    { memberSequence: '24', completeItem: safePrefixRow24 },
    { memberSequence: '25', completeItem: safePrefixRow25 },
    { memberSequence: '26', completeItem: safePrefixRow26 },
  ].map((row) => ({
    memberSequence: row.memberSequence,
    entryId: row.completeItem.entryId,
    authoritativeResultingHead: authoritativeEntryHead(row.completeItem),
    completeItem: row.completeItem,
  }))
  const safePrefixResponseItems = [safePrefixRow24, safePrefixRow25]
  const partialDelta = wireDelta(memberVaultKey, safePrefixResponseItems, {
    deltaUpperBound: '26',
    appliedThroughSequence: '25',
    continuationCursor: 'cvt-557-synthetic-delta-cursor-after-25',
  })
  const pageBoundary = {
    contract: 'palladin-current-member-entry-sync-page-boundary-fixture',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    operationalResponseBytes: 2097152,
    maximumResponseBytes: 4194304,
    defaultItems: 100,
    maximumItems: 200,
    requestAuthority: responseAuthority(baseRequestAuthority, boundaryCandidates),
    candidateOrder: boundaryCandidates.map((item) => item.entryId),
    response: boundaryResponse,
    firstOmittedCompleteItem: boundaryOmittedItem,
    measuredBudget: {
      includedResponseUtf8Bytes: encodedResponseBytes(boundaryResponse),
      responseWithFirstOmittedItemUtf8Bytes: encodedResponseBytes(boundaryResponseWithOmittedCandidate),
      measurementEncoding: 'compact-UTF-8-JSON-before-transport',
    },
    expected: {
      includedEntryIds: boundaryIncludedItems.map((item) => item.entryId),
      omittedEntryIds: [boundaryOmittedItem.entryId],
      nextCursorLastEntryId: boundaryIncludedItems.at(-1).entryId,
      cursorMustNotAdvanceTo: boundaryOmittedItem.entryId,
      pageAndCursorCommit: 'atomic',
    },
    partialDeltaSafePrefix: {
      requestAuthority: deltaAuthority(baseRequestAuthority, safePrefixResponseItems, '26', '22', { requestPageSize: 3 }),
      response: partialDelta,
      orderedJournalRows: safePrefixJournalRows,
      fullyScannedThroughSequence: '25',
      firstOmittedJournalSequence: '26',
      expectedResumeStrictlyAfterSequence: '25',
      expectedCoalescedEntryIds: safePrefixResponseItems.map((item) => item.entryId),
      expectedCoalescedCurrentRevisions: safePrefixResponseItems.map((item) => item.currentRevision),
      continuationAfterVaultAdvance: {
        requestAuthority: deltaAuthority(baseRequestAuthority, [safePrefixRow26], '27', '25', {
          requestPageSize: 3,
          deltaRequestKind: 'continuation',
          authenticatedCursorDeltaUpperBound: '26',
        }),
        response: wireDelta(memberVaultKey, [safePrefixRow26], { deltaUpperBound: '26' }),
        excludedPostBoundaryMemberSequence: '27',
      },
    },
  }

  const concurrentRevision = makeCompleteItem(registry, completeBaseVectors, {
    entryId: ids.entryId,
    id: 'concurrent-revision-13',
    currentRevision: '13',
    operation: 2,
  })
  const concurrentMutation = {
    contract: 'palladin-current-member-entry-sync-concurrent-mutation-fixture',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    mutationSequence: '13',
    cases: [
      {
        id: 'mutation-visible-in-snapshot-winner',
        snapshotRequestAuthority: responseAuthority(baseRequestAuthority, [concurrentRevision]),
        deltaRequestAuthority: deltaAuthority(baseRequestAuthority, [], '13', '13'),
        snapshotResponse: wireSnapshot(memberVaultKey, concurrentRevision, { snapshotBaseSequence: '13' }),
        closingDeltaResponse: wireDelta(memberVaultKey, [], { deltaUpperBound: '13' }),
        expectedApplyCount: 1,
        expectedFinalRevision: '13',
      },
      {
        id: 'mutation-visible-in-closing-delta',
        snapshotRequestAuthority: responseAuthority(baseRequestAuthority, [validItem]),
        deltaRequestAuthority: deltaAuthority(baseRequestAuthority, [concurrentRevision], '13', '12'),
        snapshotResponse: wireSnapshot(memberVaultKey, validItem, { snapshotBaseSequence: '12' }),
        closingDeltaResponse: wireDelta(memberVaultKey, [concurrentRevision], { deltaUpperBound: '13' }),
        expectedApplyCount: 1,
        expectedFinalRevision: '13',
      },
    ],
    forbidden: ['missing-revision-13', 'active-revision-12-and-13-at-once', 'cursor-before-incomplete-page-commit'],
  }

  const transitionItem = (id, entryId, revision, operation, state = 'active', options = {}) => makeCompleteItem(
    registry,
    completeBaseVectors,
    { entryId, id, currentRevision: revision, operation, state, ...options },
  )
  const transitionDelta = (sequence, afterSequence, items) => ({
    requestAuthority: deltaAuthority(baseRequestAuthority, items, sequence, afterSequence),
    response: wireDelta(memberVaultKey, items, { deltaUpperBound: sequence }),
  })
  const mutations = {
    contract: 'palladin-current-member-entry-sync-mutation-transition-fixtures',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    transitions: [
      { mutation: 'create', outcomeKind: 'delta', ...transitionDelta('20', '19', [transitionItem('mutation-create', ids.entryId, '20', 1)]), expected: 'complete-head', operation: 'created', projectionFetches: 0 },
      { mutation: 'update-secret', outcomeKind: 'delta', ...transitionDelta('21', '20', [transitionItem('mutation-update-secret', ids.entryId, '21', 2)]), expected: 'complete-head', operation: 'updated', projectionFetches: 0 },
      { mutation: 'update-presentation', outcomeKind: 'delta', ...transitionDelta('22', '21', [transitionItem('mutation-update-presentation', ids.entryId, '22', 2)]), expected: 'complete-head', operation: 'updated', projectionFetches: 0 },
      { mutation: 'import', outcomeKind: 'delta', ...transitionDelta('24', '22', [transitionItem('mutation-import-1', ids.entryId, '23', 1), transitionItem('mutation-import-2', ids.secondEntryId, '24', 1)]), expected: 'one-complete-head-per-committed-entry', operation: 'created', projectionFetches: 0 },
      { mutation: 'archive', outcomeKind: 'delta', ...transitionDelta('25', '24', [transitionItem('mutation-archive', ids.entryId, '25', 3, 'archived')]), expected: 'complete-non-active-head', operation: 'archived', projectionFetches: 0 },
      { mutation: 'restore', outcomeKind: 'delta', ...transitionDelta('26', '25', [transitionItem('mutation-restore', ids.entryId, '26', 4)]), expected: 'complete-head-at-new-current-revision', operation: 'restored', projectionFetches: 0 },
      { mutation: 'delete', outcomeKind: 'delta', ...transitionDelta('27', '26', [transitionItem('mutation-delete', ids.entryId, '27', 5, 'deleted')]), expected: 'complete-non-active-head', operation: 'deleted', projectionFetches: 0 },
      { mutation: 'purge', outcomeKind: 'delta', ...transitionDelta('28', '27', [tombstone()]), expected: 'tombstone', projectionFetches: 0 },
      { mutation: 'agent-visibility-policy-change', outcomeKind: 'delta', ...transitionDelta('29', '28', [transitionItem('mutation-policy', ids.entryId, '29', 2)]), expected: 'complete-head-with-complete-member-secret', operation: 'updated', projectionFetches: 0 },
      { mutation: 'entry-dek-rotation', outcomeKind: 'delta', ...transitionDelta('30', '29', [transitionItem('mutation-entry-dek-rotation', ids.entryId, '30', 2, 'active', { keyVersion: 6 })]), expected: 'complete-head-at-new-key-version', operation: 'updated', projectionFetches: 0 },
      { mutation: 'vault-or-member-rekey', outcomeKind: 'reset', httpStatus: 409, response: { outcome: 'resetRequired', currentSequence: '31', minRetainedSequence: '30', newSnapshotRequired: true }, expected: 'reset', projectionFetches: 0 },
      { mutation: 'membership-removal', outcomeKind: 'access-denied', httpStatus: 403, response: { outcome: 'accessDenied' }, expected: 'access-denial-without-ciphertext', projectionFetches: 0 },
    ],
  }

  const negativeCases = []
  const missingSecret = clone(validSnapshot)
  delete missingSecret.response.items[0].memberSecret
  negativeCases.push({ id: 'missing-member-secret', expectedError: 'incomplete-head', fixture: missingSecret.response })

  const revisionMismatch = clone(validSnapshot)
  revisionMismatch.response.items[0].currentRevision = '13'
  negativeCases.push({ id: 'member-secret-revision-mismatch', expectedError: 'binding-mismatch', fixture: revisionMismatch.response })

  const scopeMismatch = clone(validSnapshot)
  scopeMismatch.response.items[0].memberSecret.descriptor.scope.vaultId = ids.otherVaultId
  negativeCases.push({ id: 'member-secret-vault-substitution', expectedError: 'binding-mismatch', fixture: scopeMismatch.response })

  const principalMismatch = clone(validSnapshot)
  principalMismatch.response.accessContext.principalId = ids.otherMemberId
  negativeCases.push({ id: 'access-context-principal-substitution', expectedError: 'authority-mismatch', fixture: principalMismatch.response })

  const generationMismatch = clone(validSnapshot)
  generationMismatch.response.items[0].memberIndex.descriptor.memberKeyGeneration = 5
  negativeCases.push({ id: 'member-index-generation-substitution', expectedError: 'binding-mismatch', fixture: generationMismatch.response })

  const coordinatedGenerationSubstitution = clone(validSnapshot)
  coordinatedGenerationSubstitution.response.accessContext.memberKeyGeneration = 5
  coordinatedGenerationSubstitution.response.memberVaultKey.wrappedVaultKey.descriptor.memberKeyGeneration = 5
  for (const field of ['entryKey', 'memberIndex', 'memberSecret']) {
    coordinatedGenerationSubstitution.response.items[0][field].descriptor.memberKeyGeneration = 5
  }
  negativeCases.push({ id: 'coordinated-member-generation-substitution', expectedError: 'authority-mismatch', fixture: coordinatedGenerationSubstitution.response })

  const coordinatedVaultKeySubstitution = clone(validSnapshot)
  coordinatedVaultKeySubstitution.response.accessContext.vaultKeyVersion = 4
  coordinatedVaultKeySubstitution.response.memberVaultKey.wrappedVaultKey.descriptor.resourceRevision = '4'
  coordinatedVaultKeySubstitution.response.memberVaultKey.wrappedVaultKey.descriptor.wrappedKeyVersion = 4
  coordinatedVaultKeySubstitution.response.items[0].entryKey.descriptor.binding.wrappingVaultKeyVersion = 4
  negativeCases.push({ id: 'coordinated-vault-key-version-substitution', expectedError: 'authority-mismatch', fixture: coordinatedVaultKeySubstitution.response })

  const recipientKeyVersionSubstitution = clone(validSnapshot)
  recipientKeyVersionSubstitution.response.memberVaultKey.wrappedVaultKey.descriptor.recipientKeyVersion += 1
  negativeCases.push({ id: 'member-recipient-key-version-substitution', expectedError: 'authority-mismatch', fixture: recipientKeyVersionSubstitution.response })

  const recipientFingerprintSubstitution = clone(validSnapshot)
  recipientFingerprintSubstitution.response.memberVaultKey.wrappedVaultKey.descriptor.recipientFingerprint = b64u(syntheticBytes('cvt-557:substituted-member-fingerprint', 32))
  negativeCases.push({ id: 'member-recipient-fingerprint-substitution', expectedError: 'authority-mismatch', fixture: recipientFingerprintSubstitution.response })

  const coordinatedRecipientKeyVersionSubstitution = clone(recipientKeyVersionSubstitution)
  coordinatedRecipientKeyVersionSubstitution.response.accessContext.memberRecipientKeyVersion += 1
  negativeCases.push({ id: 'coordinated-member-recipient-key-version-substitution', expectedError: 'authority-mismatch', fixture: coordinatedRecipientKeyVersionSubstitution.response })

  const coordinatedRecipientFingerprintSubstitution = clone(recipientFingerprintSubstitution)
  coordinatedRecipientFingerprintSubstitution.response.accessContext.memberRecipientKeyFingerprint = coordinatedRecipientFingerprintSubstitution.response.memberVaultKey.wrappedVaultKey.descriptor.recipientFingerprint
  negativeCases.push({ id: 'coordinated-member-recipient-fingerprint-substitution', expectedError: 'authority-mismatch', fixture: coordinatedRecipientFingerprintSubstitution.response })

  const corruptedMemberVaultKeyPackage = clone(validSnapshot)
  const sealedPackage = corruptedMemberVaultKeyPackage.response.memberVaultKey.wrappedVaultKey.encodedSealedKeyPackage
  corruptedMemberVaultKeyPackage.response.memberVaultKey.wrappedVaultKey.encodedSealedKeyPackage = `${sealedPackage[0] === 'A' ? 'B' : 'A'}${sealedPackage.slice(1)}`
  negativeCases.push({ id: 'member-vault-key-package-corruption', expectedError: 'authentication-failed', fixture: corruptedMemberVaultKeyPackage.response })

  const substitutedVaultKeyPackage = clone(validSnapshot)
  const wrongVaultKeyPackage = concat(
    utf8('PLDNX2K1'),
    syntheticBytes('cvt-557:substituted-vault-key', 32),
    fromHex(memberVaultKeyData.evidence.contextHashHex),
  )
  substitutedVaultKeyPackage.response.memberVaultKey.wrappedVaultKey.encodedSealedKeyPackage = b64u(deterministicSeal(
    wrongVaultKeyPackage,
    fromHex(memberVaultKeyData.evidence.recipientPublicKeyHex),
    syntheticBytes('cvt-557:substituted-vault-key:ephemeral-seed'),
  ))
  negativeCases.push({ id: 'member-vault-key-plaintext-substitution', expectedError: 'vault-key-mismatch', fixture: substitutedVaultKeyPackage.response })

  const keyVersionMismatch = clone(validSnapshot)
  keyVersionMismatch.response.items[0].currentKeyVersion = 6
  negativeCases.push({ id: 'head-key-version-substitution', expectedError: 'binding-mismatch', fixture: keyVersionMismatch.response })

  const coordinatedHeadSubstitution = clone(validSnapshot)
  const substitutedHead = coordinatedHeadSubstitution.response.items[0]
  substitutedHead.state = 'archived'
  substitutedHead.updatedAt = '2026-08-29T09:00:00Z'
  substitutedHead.currentRevision = '13'
  substitutedHead.memberIndexRevision = '13'
  substitutedHead.currentKeyVersion = 6
  for (const field of ['entryKey', 'memberIndex', 'memberSecret']) {
    substitutedHead[field].descriptor.resourceRevision = '13'
    substitutedHead[field].descriptor.keyVersion = 6
  }
  negativeCases.push({ id: 'coordinated-entry-head-substitution', expectedError: 'authority-mismatch', fixture: coordinatedHeadSubstitution.response })

  const expired = clone(validSnapshot)
  expired.requestAuthority.validationTime = '2026-08-30T08:00:00Z'
  negativeCases.push({ id: 'offline-access-exact-expiry', expectedError: 'offline-access-expired', fixture: expired.response, validationTime: expired.requestAuthority.validationTime })

  const excessiveLease = clone(validSnapshot)
  excessiveLease.response.accessContext.notAfter = '2026-08-31T08:00:00Z'
  negativeCases.push({ id: 'offline-access-duration-exceeds-policy', expectedError: 'offline-access-duration-mismatch', fixture: excessiveLease.response })

  const disabledFutureLease = clone(validSnapshot)
  disabledFutureLease.response.accessContext.offlinePolicy = 'disabled'
  disabledFutureLease.response.accessContext.notAfter = '2026-08-29T09:00:00Z'
  negativeCases.push({
    id: 'disabled-policy-future-lease',
    expectedError: 'offline-access-duration-mismatch',
    fixture: disabledFutureLease.response,
    requestAuthorityOverrides: { authoritativeOfflinePolicy: 'disabled', validationTime: '2026-08-29T08:00:00Z' },
  })

  const microsecondLeaseDrift = clone(validSnapshot)
  microsecondLeaseDrift.response.accessContext.issuedAt = '2026-08-29T08:00:00.000001Z'
  microsecondLeaseDrift.response.accessContext.notAfter = '2026-08-30T08:00:00.000002Z'
  negativeCases.push({ id: 'offline-access-microsecond-duration-drift', expectedError: 'offline-access-duration-mismatch', fixture: microsecondLeaseDrift.response })

  const crossOrganization = clone(validSnapshot)
  crossOrganization.response.accessContext.organizationId = ids.otherOrganizationId
  negativeCases.push({ id: 'access-context-organization-substitution', expectedError: 'authority-mismatch', fixture: crossOrganization.response })

  const corrupted = clone(validSnapshot)
  const originalPayload = corrupted.response.items[0].memberSecret.encodedSuitePayload
  corrupted.response.items[0].memberSecret.encodedSuitePayload = `${originalPayload[0] === 'A' ? 'B' : 'A'}${originalPayload.slice(1)}`
  negativeCases.push({ id: 'member-secret-ciphertext-corruption', expectedError: 'authentication-failed', fixture: corrupted.response })

  const replay = clone(validSnapshot)
  replay.response.snapshotBaseSequence = '11'
  negativeCases.push({
    id: 'stale-snapshot-boundary-replay',
    expectedError: 'stale-stream-generation',
    fixture: replay.response,
    persistedClientState: { activeSnapshotBaseSequence: '12', activeAppliedThroughSequence: '12' },
  })

  const inflatedDeltaBoundary = clone(partialDelta)
  inflatedDeltaBoundary.deltaUpperBound = '999'
  inflatedDeltaBoundary.appliedThroughSequence = '999'
  inflatedDeltaBoundary.continuationCursor = null
  negativeCases.push({
    id: 'delta-upper-bound-substitution',
    responseKind: 'delta',
    expectedError: 'authority-mismatch',
    requestAuthority: deltaAuthority(baseRequestAuthority, safePrefixResponseItems, '26', '22'),
    fixture: inflatedDeltaBoundary,
  })

  const movedContinuationBoundary = clone(pageBoundary.partialDeltaSafePrefix.continuationAfterVaultAdvance)
  movedContinuationBoundary.response.deltaUpperBound = '27'
  movedContinuationBoundary.response.appliedThroughSequence = '27'
  negativeCases.push({
    id: 'continuation-cursor-boundary-substitution',
    responseKind: 'delta',
    expectedError: 'cursor-boundary-mismatch',
    requestAuthority: movedContinuationBoundary.requestAuthority,
    fixture: movedContinuationBoundary.response,
  })

  const negative = {
    contract: 'palladin-current-member-entry-sync-negative-fixtures',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    rule: 'expected values come from requestAuthority, authoritative Vault state and structural heads, never from the substituted object',
    cases: negativeCases,
  }

  const vectorObjects = new Map([
    ['vectors/valid-snapshot.json', validSnapshot],
    ['vectors/near-limit-member-secret.json', largeFixture],
    ['vectors/tombstone-reset.json', controls],
    ['vectors/page-boundary.json', pageBoundary],
    ['vectors/concurrent-mutation.json', concurrentMutation],
    ['vectors/mutation-transitions.json', mutations],
    ['negative/rejections.json', negative],
  ])
  const vectorFiles = new Map([...vectorObjects].map(([path, value]) => [path, pretty(value)]))
  const manifest = {
    contract: 'palladin-current-member-entry-sync-cross-language-fixtures',
    fixtureSchemaVersion: 1,
    protocolVersion: 2,
    syncPolicyVersion: 2,
    status: 'normative-for-cvt-555',
    syntheticDataOnly: true,
    immutableDependencies: [
      { path: '../../v1/registry.json', sha256: hex(sha256(registryRaw)) },
      { path: '../../v1/sync-retention-performance-policy.json', sha256: 'fd357f631a5f4ce4b59190324641bfc26365624cdf475a805be0aead33cf9d40' },
      { path: '../../v2/current-member-entry-sync-policy.json', sha256: hex(sha256(policyRaw)) },
      { path: '../v2/manifest.json', sha256: hex(sha256(utf8(pretty(base.manifest)))) },
    ],
    consumers: ['net-backend', 'react-web-panel', 'flutter-mobile', 'browser-extension'],
    explicitlyExcludedConsumers: ['agent-runtime-rust', 'node-agent-cli', 'mcp'],
    generatedBy: 'fixtures/current-member-entry-sync-v2/generate.mjs',
    generationRule: 'deterministic synthetic nonces and keys are fixture-only; production uses its reviewed cryptographic boundaries',
    files: [...vectorFiles].map(([path, content]) => ({ path, sha256: hex(sha256(utf8(content))) })),
  }
  return {
    manifest,
    vectorObjects,
    vectorFiles,
    evidence: {
      memberIndexVector,
      memberSecretVector,
      entryKeyVector,
      memberVaultKeyVector,
      memberVaultKeyData,
      validItemData,
      largeSecretVector,
    },
  }
}

export function buildFixtureFiles(registry, registryRaw, policyRaw) {
  const fixture = buildFixtureData(registry, registryRaw, policyRaw)
  return new Map([['manifest.json', pretty(fixture.manifest)], ...fixture.vectorFiles])
}

export function decryptFixtureVector(vector) {
  return decryptXChaCha20(
    fromB64u(vector.envelope[vector.ciphertextField]),
    fromHex(vector.aadHex),
    fromHex(vector.nonceHex),
    fromHex(vector.decryptionKeyHex),
  )
}
