using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Palladin.Module.Notification.Infrastructure.Email.Suppression;

namespace Palladin.Module.Notification.Infrastructure.Email.Events;

// Polls the SQS queue fed by the SES bounce/complaint SNS topic and writes hard bounces / complaints
// to the suppression list. Kept out of MassTransit on purpose — the RabbitMQ transport is unrelated,
// and SQS long-polling runs the same locally and in the cloud. No-op when the queue is not configured.
[UsedImplicitly]
internal sealed class SesEventPoller(
    IOptions<SesOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<SesEventPoller> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.EventsQueueUrl))
        {
            logger.LogInformation("SES events queue not configured — bounce/complaint poller disabled.");
            return;
        }

        using var sqs = BuildClient(opts);
        logger.LogInformation("SES bounce/complaint poller started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(sqs, opts.EventsQueueUrl, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "SES event poll failed; retrying after backoff.");
                await Task.Delay(ErrorBackoff, stoppingToken);
            }
        }
    }

    private async Task PollOnceAsync(IAmazonSQS sqs, string queueUrl, CancellationToken ct)
    {
        var response = await sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 20 },
            ct);

        if (response.Messages is null || response.Messages.Count == 0)
        {
            return;
        }

        foreach (var message in response.Messages)
        {
            await ProcessMessageAsync(sqs, queueUrl, message, ct);
        }
    }

    private async Task ProcessMessageAsync(IAmazonSQS sqs, string queueUrl, Message message, CancellationToken ct)
    {
        SesEventParseResult result;
        try
        {
            result = SesEventParser.Parse(message.Body);
        }
        catch (JsonException exception)
        {
            // Leave malformed messages on the queue — redrive moves them to the DLQ after maxReceiveCount.
            logger.LogError(exception, "Unparseable SES event message; leaving for redrive to DLQ.");
            return;
        }

        if (result.Enveloped)
        {
            logger.LogWarning(
                "SES event arrived in an SNS envelope — RawMessageDelivery is disabled on the subscription; fix it.");
        }

        using var scope = scopeFactory.CreateScope();
        var suppression = scope.ServiceProvider.GetRequiredService<IEmailSuppressionStore>();
        foreach (var entry in result.Suppressions)
        {
            await suppression.SuppressAsync(entry.Address, entry.Reason, ct);
        }

        logger.LogInformation(
            "Processed SES event {Kind} (messageId {MessageId}, suppressions {SuppressionCount}).",
            result.Kind, result.MessageId ?? "-", result.Suppressions.Count);

        await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
    }

    private static IAmazonSQS BuildClient(SesOptions opts) =>
        AwsClientFactory.Build<IAmazonSQS, AmazonSQSConfig>(
            opts,
            () => new AmazonSQSConfig(),
            (credentials, config) => new AmazonSQSClient(credentials, config),
            config => new AmazonSQSClient(config));
}
