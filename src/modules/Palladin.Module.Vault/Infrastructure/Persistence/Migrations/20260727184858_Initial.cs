using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Palladin.Module.Vault.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RecipientKeyVersion = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    SigningPublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IconKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IconColor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AccessEpoch = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    AccessEpochStartedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastProcessedDeactivationEpoch = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastProcessedDeactivationAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.UniqueConstraint("AK_Agents_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "MemberKeyDirectory",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberKeyDirectory", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultCreationChallenges",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultCreationChallenges", x => new { x.OrganizationId, x.VaultId });
                });

            migrationBuilder.CreateTable(
                name: "VaultPresentationAssetCutoverStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LegacyObjectsPurgedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultPresentationAssetCutoverStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VaultPrincipalDeprovisionings",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalType = table.Column<int>(type: "integer", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CurrentVaultId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentRotationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedVaultCount = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultPrincipalDeprovisionings", x => new { x.OrganizationId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Vaults",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    MetadataRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MemberSequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    DiscoverySequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MinRetainedMemberSequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MinRetainedDiscoverySequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    CurrentVaultKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    CurrentVdkVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    CurrentAgentMessageKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    CurrentManifestSigningKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    MemberVaultMetadataCryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberVaultMetadataKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    MemberVaultMetadataEncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 16408, nullable: false),
                    ManifestSigningPublicKey = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ManifestSigningKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentMessagePublicKey = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentMessageKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vaults", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("AK_Vaults_Id", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentPairingActivations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    AgentX25519Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentEd25519Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CandidateDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CandidateVaultCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ConfirmedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPairingActivations", x => x.Id);
                    table.UniqueConstraint("AK_AgentPairingActivations_Id_OrganizationId_AgentId", x => new { x.Id, x.OrganizationId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_AgentPairingActivations_Agents_OrganizationId_AgentId",
                        columns: x => new { x.OrganizationId, x.AgentId },
                        principalTable: "Agents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CredentialFailureReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredentialFailureReports", x => new { x.OrganizationId, x.AgentId, x.VaultId, x.EntryId, x.Id });
                    table.ForeignKey(
                        name: "FK_CredentialFailureReports_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentVaultDiscoveryEnvelope",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    CryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WrapperSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VdkVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientAgentKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientAgentKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentWrappedVdk = table.Column<byte[]>(type: "bytea", maxLength: 120, nullable: false),
                    ManifestRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    ManifestSignature = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                    AgentX25519Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentEd25519Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    VaultSigningPublicKey = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    VaultSigningKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ManifestSigningKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    VaultAgentMessagePublicKey = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    VaultAgentMessageKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    AgentMessageKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    AgentWrappedVdkDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    IssuedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    MinimumAgentRuntimeProtocol = table.Column<int>(type: "integer", nullable: false),
                    ProvisionedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ProvisionedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ProvisionedAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentVaultDiscoveryEnvelope", x => new { x.OrganizationId, x.VaultId, x.AgentId });
                    table.ForeignKey(
                        name: "FK_AgentVaultDiscoveryEnvelope_Agents_OrganizationId_AgentId",
                        columns: x => new { x.OrganizationId, x.AgentId },
                        principalTable: "Agents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentVaultDiscoveryEnvelope_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EntryCreationChallenges",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryCreationChallenges", x => new { x.OrganizationId, x.VaultId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_EntryCreationChallenges_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentAccessEpoch = table.Column<long>(type: "bigint", nullable: false),
                    AgentPublicKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestType = table.Column<int>(type: "integer", nullable: true),
                    Methods = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    QueryLimit = table.Column<int>(type: "integer", nullable: true),
                    QueryCount = table.Column<int>(type: "integer", nullable: false),
                    ExpirySource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedBySystem = table.Column<bool>(type: "boolean", nullable: false),
                    DeniedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeniedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    LastAccessedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastAccessIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    LastAccessHostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    GrantType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => x.Id);
                    table.UniqueConstraint("AK_Grants_OrganizationId_VaultId_Id", x => new { x.OrganizationId, x.VaultId, x.Id });
                    table.ForeignKey(
                        name: "FK_Grants_Agents_OrganizationId_AgentId",
                        columns: x => new { x.OrganizationId, x.AgentId },
                        principalTable: "Agents",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultEntries",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CurrentRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MemberIndexRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    AgentDiscoveryRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    AgentDiscoveryRevisionHighWatermark = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    CurrentKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ArchivedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DeletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    MemberIndexProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    MemberIndexCryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberIndexMemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    MemberIndexEncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 32792, nullable: false),
                    AgentDiscoveryProtocolVersion = table.Column<int>(type: "integer", nullable: true),
                    AgentDiscoveryCryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AgentDiscoveryVdkVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: true),
                    AgentDiscoveryMemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: true),
                    AgentDiscoveryEncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 16408, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultEntries", x => new { x.OrganizationId, x.VaultId, x.Id });
                    table.ForeignKey(
                        name: "FK_VaultEntries_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultKeyMaterialEnvelopes",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    KeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    MemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    WrappingKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    CryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 280, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultKeyMaterialEnvelopes", x => new { x.OrganizationId, x.VaultId, x.Kind });
                    table.ForeignKey(
                        name: "FK_VaultKeyMaterialEnvelopes_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultKeyRotations",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Cause = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    BaseMemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    BaseKeyEpoch_VaultKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    BaseKeyEpoch_VdkVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    BaseKeyEpoch_AgentMessageKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    BaseKeyEpoch_ManifestSigningKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    BaseMemberSequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    BaseDiscoverySequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    TargetMemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    TargetKeyEpoch_VaultKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    TargetKeyEpoch_VdkVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    TargetKeyEpoch_AgentMessageKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    TargetKeyEpoch_ManifestSigningKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    TriggeredBy = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggeredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LeaseOwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    FencingToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LeaseRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    CommittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeprovisioningId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExcludedMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExcludedAgentId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultKeyRotations", x => new { x.OrganizationId, x.VaultId, x.Id });
                    table.ForeignKey(
                        name: "FK_VaultKeyRotations_VaultPrincipalDeprovisionings_Organizatio~",
                        columns: x => new { x.OrganizationId, x.DeprovisioningId },
                        principalTable: "VaultPrincipalDeprovisionings",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VaultKeyRotations_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultMemberKeyEnvelopes",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    WrapperSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VaultKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientKeyFingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                    SealedVaultKeyPackage = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultMemberKeyEnvelopes", x => new { x.OrganizationId, x.VaultId, x.MemberId, x.MemberKeyGeneration });
                    table.ForeignKey(
                        name: "FK_VaultMemberKeyEnvelopes_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultMembers",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultMembers", x => new { x.OrganizationId, x.VaultId, x.UserId });
                    table.ForeignKey(
                        name: "FK_VaultMembers_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentPairingActivationCandidates",
                columns: table => new
                {
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    VaultSigningKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    SignedManifestDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPairingActivationCandidates", x => new { x.ActivationId, x.VaultId });
                    table.ForeignKey(
                        name: "FK_AgentPairingActivationCandidates_AgentPairingActivations_Ac~",
                        columns: x => new { x.ActivationId, x.OrganizationId, x.AgentId },
                        principalTable: "AgentPairingActivations",
                        principalColumns: new[] { "Id", "OrganizationId", "AgentId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentPairingActivationCandidates_Vaults_OrganizationId_Vaul~",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EncryptedReasonEnvelopes",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    CryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ReasonKeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    AgentMessageKeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    MemberKeyGeneration = table.Column<long>(type: "bigint", nullable: false),
                    RecipientAgentMessageKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    RequestedMethods = table.Column<int>(type: "integer", nullable: false),
                    EncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 4120, nullable: false),
                    WrapperSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentMessageWrappedReasonDek = table.Column<byte[]>(type: "bytea", maxLength: 120, nullable: false),
                    AgentSignature = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptedReasonEnvelopes", x => new { x.OrganizationId, x.VaultId, x.GrantRequestId });
                    table.ForeignKey(
                        name: "FK_EncryptedReasonEnvelopes_Grants_OrganizationId_VaultId_Gran~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.GrantRequestId },
                        principalTable: "Grants",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EncryptedPresentationAssets",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<short>(type: "smallint", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    MediaType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CiphertextLength = table.Column<int>(type: "integer", nullable: false),
                    CiphertextSha256 = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptedPresentationAssets", x => new { x.OrganizationId, x.VaultId, x.Id });
                    table.ForeignKey(
                        name: "FK_EncryptedPresentationAssets_VaultEntries_OrganizationId_Vau~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId },
                        principalTable: "VaultEntries",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EncryptedPresentationAssets_Vaults_OrganizationId_VaultId",
                        columns: x => new { x.OrganizationId, x.VaultId },
                        principalTable: "Vaults",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GrantEntryScopes",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Methods = table.Column<int>(type: "integer", nullable: false),
                    DeliveryPolicy = table.Column<int>(type: "integer", nullable: false),
                    FieldIds = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantEntryScopes", x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_GrantEntryScopes_Grants_OrganizationId_VaultId_GrantId",
                        columns: x => new { x.OrganizationId, x.VaultId, x.GrantId },
                        principalTable: "Grants",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GrantEntryScopes_VaultEntries_OrganizationId_VaultId_EntryId",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId },
                        principalTable: "VaultEntries",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VaultEntryKeys",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    WrapperRevision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    WrappingKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    CryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 88, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultEntryKeys", x => new { x.OrganizationId, x.VaultId, x.EntryId, x.KeyVersion });
                    table.ForeignKey(
                        name: "FK_VaultEntryKeys_VaultEntries_OrganizationId_VaultId_EntryId",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId },
                        principalTable: "VaultEntries",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultKeyRotationPreparedItems",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    RotationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectVersion = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    SourceRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Payload = table.Column<byte[]>(type: "bytea", maxLength: 32768, nullable: false),
                    PayloadDigest = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PreparedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultKeyRotationPreparedItems", x => new { x.OrganizationId, x.VaultId, x.RotationId, x.Kind, x.SubjectId, x.SubjectVersion });
                    table.ForeignKey(
                        name: "FK_VaultKeyRotationPreparedItems_VaultKeyRotations_Organizatio~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.RotationId },
                        principalTable: "VaultKeyRotations",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GrantEntryEnvelopes",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantEnvelopeRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    EntryRevision = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    CryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GrantKeyVersion = table.Column<long>(type: "bigint", nullable: false),
                    MemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    RecipientAgentKeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    EncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 262168, nullable: false),
                    AgentWrappedGrantDek = table.Column<byte[]>(type: "bytea", maxLength: 120, nullable: false),
                    WrapperSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentKeyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    RemainingUses = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantEntryEnvelopes", x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_GrantEntryEnvelopes_GrantEntryScopes_OrganizationId_VaultId~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.GrantId, x.EntryId },
                        principalTable: "GrantEntryScopes",
                        principalColumns: new[] { "OrganizationId", "VaultId", "GrantId", "EntryId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VaultEntryVersions",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    MemberSequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: false),
                    DiscoverySequence = table.Column<decimal>(type: "numeric(20,0)", precision: 20, scale: 0, nullable: true),
                    MemberIndexChanged = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ChangedByType = table.Column<int>(type: "integer", nullable: false),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<int>(type: "integer", nullable: false),
                    KeyVersion = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    CryptoSuiteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberKeyGeneration = table.Column<decimal>(type: "numeric(10,0)", precision: 10, scale: 0, nullable: false),
                    MemberSecretEncodedSuitePayload = table.Column<byte[]>(type: "bytea", maxLength: 262168, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VaultEntryVersions", x => new { x.OrganizationId, x.VaultId, x.EntryId, x.Revision });
                    table.ForeignKey(
                        name: "FK_VaultEntryVersions_VaultEntries_OrganizationId_VaultId_Entr~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId },
                        principalTable: "VaultEntries",
                        principalColumns: new[] { "OrganizationId", "VaultId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VaultEntryVersions_VaultEntryKeys_OrganizationId_VaultId_En~",
                        columns: x => new { x.OrganizationId, x.VaultId, x.EntryId, x.KeyVersion },
                        principalTable: "VaultEntryKeys",
                        principalColumns: new[] { "OrganizationId", "VaultId", "EntryId", "KeyVersion" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "VaultPresentationAssetCutoverStates",
                columns: new[] { "Id", "LegacyObjectsPurgedAt" },
                values: new object[] { "zero-knowledge-assets", null });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivationCandidates_ActivationId_OrganizationI~",
                table: "AgentPairingActivationCandidates",
                columns: new[] { "ActivationId", "OrganizationId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivationCandidates_OrganizationId_AgentId_Vau~",
                table: "AgentPairingActivationCandidates",
                columns: new[] { "OrganizationId", "AgentId", "VaultId", "ManifestRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivationCandidates_OrganizationId_VaultId",
                table: "AgentPairingActivationCandidates",
                columns: new[] { "OrganizationId", "VaultId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivations_ExpiresAt",
                table: "AgentPairingActivations",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AgentPairingActivations_OrganizationId_AgentId_AgentAccessE~",
                table: "AgentPairingActivations",
                columns: new[] { "OrganizationId", "AgentId", "AgentAccessEpoch", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_OrganizationId_Status",
                table: "Agents",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentVaultDiscoveryEnvelope_OrganizationId_AgentId",
                table: "AgentVaultDiscoveryEnvelope",
                columns: new[] { "OrganizationId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CredentialFailureReports_AgentId",
                table: "CredentialFailureReports",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_CredentialFailureReports_OrganizationId_CreatedAt",
                table: "CredentialFailureReports",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CredentialFailureReports_OrganizationId_EntryId",
                table: "CredentialFailureReports",
                columns: new[] { "OrganizationId", "EntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_EncryptedPresentationAssets_OrganizationId_VaultId_EntryId",
                table: "EncryptedPresentationAssets",
                columns: new[] { "OrganizationId", "VaultId", "EntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_EncryptedPresentationAssets_StorageId",
                table: "EncryptedPresentationAssets",
                column: "StorageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EncryptedReasonEnvelopes_OrganizationId_VaultId_EntryId",
                table: "EncryptedReasonEnvelopes",
                columns: new[] { "OrganizationId", "VaultId", "EntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_EntryCreationChallenges_ExpiresAt",
                table: "EntryCreationChallenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_EntryCreationChallenges_OrganizationId_VaultId_RequestedBy",
                table: "EntryCreationChallenges",
                columns: new[] { "OrganizationId", "VaultId", "RequestedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_GrantEntryScopes_OrganizationId_VaultId_EntryId",
                table: "GrantEntryScopes",
                columns: new[] { "OrganizationId", "VaultId", "EntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_AgentId_AgentAccessEpoch_Status",
                table: "Grants",
                columns: new[] { "AgentId", "AgentAccessEpoch", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_AgentId_Status",
                table: "Grants",
                columns: new[] { "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_EntryId_OrganizationId_CreatedAt_Id",
                table: "Grants",
                columns: new[] { "EntryId", "OrganizationId", "CreatedAt", "Id" },
                filter: "\"EntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_OrganizationId_AgentId_CreatedAt_Id",
                table: "Grants",
                columns: new[] { "OrganizationId", "AgentId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_OrganizationId_Status_CreatedAt_Id",
                table: "Grants",
                columns: new[] { "OrganizationId", "Status", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_OrganizationId_VaultId_CreatedAt_Id",
                table: "Grants",
                columns: new[] { "OrganizationId", "VaultId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_VaultId_CreatedAt_Id",
                table: "Grants",
                columns: new[] { "VaultId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Grants_VaultId_Status",
                table: "Grants",
                columns: new[] { "VaultId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultCreationChallenges_ExpiresAt",
                table: "VaultCreationChallenges",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_VaultCreationChallenges_OrganizationId_RequestedBy",
                table: "VaultCreationChallenges",
                columns: new[] { "OrganizationId", "RequestedBy" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultEntries_OrganizationId_VaultId_CreatedAt_Id",
                table: "VaultEntries",
                columns: new[] { "OrganizationId", "VaultId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultEntryVersions_OrganizationId_VaultId_DiscoverySequence",
                table: "VaultEntryVersions",
                columns: new[] { "OrganizationId", "VaultId", "DiscoverySequence" },
                unique: true,
                filter: "\"DiscoverySequence\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_VaultEntryVersions_OrganizationId_VaultId_EntryId_KeyVersion",
                table: "VaultEntryVersions",
                columns: new[] { "OrganizationId", "VaultId", "EntryId", "KeyVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultEntryVersions_OrganizationId_VaultId_MemberSequence",
                table: "VaultEntryVersions",
                columns: new[] { "OrganizationId", "VaultId", "MemberSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VaultKeyRotationPreparedItems_OrganizationId_VaultId_Rotati~",
                table: "VaultKeyRotationPreparedItems",
                columns: new[] { "OrganizationId", "VaultId", "RotationId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultKeyRotations_OrganizationId_DeprovisioningId",
                table: "VaultKeyRotations",
                columns: new[] { "OrganizationId", "DeprovisioningId" });

            migrationBuilder.CreateIndex(
                name: "IX_VaultKeyRotations_OrganizationId_VaultId",
                table: "VaultKeyRotations",
                columns: new[] { "OrganizationId", "VaultId" },
                unique: true,
                filter: "\"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_VaultPrincipalDeprovisionings_OrganizationId_PrincipalType_~",
                table: "VaultPrincipalDeprovisionings",
                columns: new[] { "OrganizationId", "PrincipalType", "PrincipalId" },
                unique: true,
                filter: "\"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_VaultPrincipalDeprovisionings_OrganizationId_RequestedAt_Id",
                table: "VaultPrincipalDeprovisionings",
                columns: new[] { "OrganizationId", "RequestedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Vaults_CreatedBy",
                table: "Vaults",
                column: "CreatedBy",
                unique: true,
                filter: "\"IsDefault\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentPairingActivationCandidates");

            migrationBuilder.DropTable(
                name: "AgentVaultDiscoveryEnvelope");

            migrationBuilder.DropTable(
                name: "CredentialFailureReports");

            migrationBuilder.DropTable(
                name: "EncryptedPresentationAssets");

            migrationBuilder.DropTable(
                name: "EncryptedReasonEnvelopes");

            migrationBuilder.DropTable(
                name: "EntryCreationChallenges");

            migrationBuilder.DropTable(
                name: "GrantEntryEnvelopes");

            migrationBuilder.DropTable(
                name: "MemberKeyDirectory");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "VaultCreationChallenges");

            migrationBuilder.DropTable(
                name: "VaultEntryVersions");

            migrationBuilder.DropTable(
                name: "VaultKeyMaterialEnvelopes");

            migrationBuilder.DropTable(
                name: "VaultKeyRotationPreparedItems");

            migrationBuilder.DropTable(
                name: "VaultMemberKeyEnvelopes");

            migrationBuilder.DropTable(
                name: "VaultMembers");

            migrationBuilder.DropTable(
                name: "VaultPresentationAssetCutoverStates");

            migrationBuilder.DropTable(
                name: "AgentPairingActivations");

            migrationBuilder.DropTable(
                name: "GrantEntryScopes");

            migrationBuilder.DropTable(
                name: "VaultEntryKeys");

            migrationBuilder.DropTable(
                name: "VaultKeyRotations");

            migrationBuilder.DropTable(
                name: "Grants");

            migrationBuilder.DropTable(
                name: "VaultEntries");

            migrationBuilder.DropTable(
                name: "VaultPrincipalDeprovisionings");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "Vaults");
        }
    }
}
