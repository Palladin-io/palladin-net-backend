using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using NodaTime;
using Palladin.Core.Json;
using Palladin.Module.Vault.Domain;

namespace Palladin.Module.Vault.Infrastructure.Purge;

internal sealed record EntryPurgeLedgerRecord(
    uint PurgeKeyVersion,
    string OpaqueEntryToken,
    Instant PurgedAt);

internal interface IEntryPurgeLedger
{
    Task<EntryPurgeLedgerRecord> AppendAsync(
        EntryScope scope,
        Instant purgedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EntryPurgeLedgerRecord>> ReadAllAsync(CancellationToken cancellationToken);
}

internal sealed class S3EntryPurgeLedger(
    IAmazonS3 s3,
    EntryPurgeTokenGenerator tokenGenerator,
    IOptions<EntryPurgeLedgerOptions> options) : IEntryPurgeLedger
{
    public async Task<EntryPurgeLedgerRecord> AppendAsync(
        EntryScope scope,
        Instant purgedAt,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var keyVersion = options.Value.CurrentKeyVersion;
        var token = await tokenGenerator.GenerateAsync(scope, keyVersion, cancellationToken);
        var record = new EntryPurgeLedgerRecord(keyVersion, token, purgedAt);
        var objectKey = ObjectKey(record);
        var retainUntil = CeilingToSecond(
            (purgedAt + Duration.FromDays(options.Value.RetentionDays)).ToDateTimeUtc());
        var payload = JsonSerializer.Serialize(record, PalladinJsonSerializationSettings.DefaultOptions);
        try
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = options.Value.BucketName,
                Key = objectKey,
                ContentBody = payload,
                ContentType = "application/json",
                IfNoneMatch = "*",
                ObjectLockMode = ObjectLockMode.Compliance,
                ObjectLockRetainUntilDate = retainUntil,
                ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            }, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
        }

        var durableRecord = await ReadRecordAsync(objectKey, null, cancellationToken);
        if (durableRecord.PurgeKeyVersion != record.PurgeKeyVersion
            || !string.Equals(
                durableRecord.OpaqueEntryToken,
                record.OpaqueEntryToken,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Vault Entry purge ledger object does not match its opaque key.");
        }

        return durableRecord;
    }

    public async Task<IReadOnlyList<EntryPurgeLedgerRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var records = new List<EntryPurgeLedgerRecord>();
        var request = new ListVersionsRequest
        {
            BucketName = options.Value.BucketName,
            Prefix = $"{options.Value.ObjectPrefix.Trim('/')}/entries/",
        };
        ListVersionsResponse response;
        do
        {
            response = await s3.ListVersionsAsync(request, cancellationToken);
            foreach (var item in response.Versions ?? [])
            {
                if (item.IsDeleteMarker == true)
                {
                    throw new InvalidOperationException(
                        "Vault Entry purge ledger contains a delete marker and cannot be trusted.");
                }

                records.Add(await ReadRecordAsync(item.Key, item.VersionId, cancellationToken));
            }

            request.KeyMarker = response.NextKeyMarker;
            request.VersionIdMarker = response.NextVersionIdMarker;
        } while (response.IsTruncated == true);

        return records;
    }

    private async Task<EntryPurgeLedgerRecord> ReadRecordAsync(
        string objectKey,
        string? versionId,
        CancellationToken cancellationToken)
    {
        using var stored = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = options.Value.BucketName,
            Key = objectKey,
            VersionId = versionId,
        }, cancellationToken);
        if (stored.ObjectLockMode != ObjectLockMode.Compliance
            || stored.ObjectLockRetainUntilDate is null)
        {
            throw new InvalidOperationException("Vault Entry purge ledger object is not Compliance locked.");
        }

        using var reader = new StreamReader(stored.ResponseStream, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var record = JsonSerializer.Deserialize<EntryPurgeLedgerRecord>(
                         payload,
                         PalladinJsonSerializationSettings.DefaultOptions)
                     ?? throw new InvalidOperationException("Vault Entry purge ledger record is invalid.");
        if (record.PurgeKeyVersion == 0
            || record.OpaqueEntryToken.Length != 64
            || record.OpaqueEntryToken.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || !string.Equals(ObjectKey(record), objectKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Vault Entry purge ledger record does not match its object key.");
        }

        var requiredRetention = (record.PurgedAt + Duration.FromDays(options.Value.RetentionDays)).ToDateTimeUtc();
        if (stored.ObjectLockRetainUntilDate.Value < requiredRetention)
        {
            throw new InvalidOperationException("Vault Entry purge ledger retention is shorter than policy.");
        }

        return record;
    }

    private string ObjectKey(EntryPurgeLedgerRecord record) =>
        $"{options.Value.ObjectPrefix.Trim('/')}/entries/{record.PurgeKeyVersion}/{record.OpaqueEntryToken}.json";

    private static DateTime CeilingToSecond(DateTime value)
    {
        var remainder = value.Ticks % TimeSpan.TicksPerSecond;
        return remainder == 0
            ? value
            : value.AddTicks(TimeSpan.TicksPerSecond - remainder);
    }

    private void EnsureEnabled()
    {
        if (!options.Value.Enabled)
        {
            throw new InvalidOperationException("Vault Entry purge ledger is disabled.");
        }
    }
}
