using Confluent.Kafka;
using Microsoft.Data.Sqlite;

namespace Claims.Api;

public sealed class KafkaOutbox(ClaimsStore store, IConfiguration config, ILogger<KafkaOutbox> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"],
            EnableIdempotence = true,
            MessageTimeoutMs = 4_000
        };
        var topic = config["Kafka:Topic"] ?? "claims.status-changed.v1";
        using var producer = new ProducerBuilder<string, string>(producerConfig).Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = store.NextEvent();
                if (item is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                var (sequence, claimId, json) = item.Value;
                await producer.ProduceAsync(topic, new Message<string, string> { Key = claimId, Value = json }, stoppingToken);
                store.MarkPublished(sequence);
            }
            catch (Exception error) when (error is KafkaException or SqliteException)
            {
                logger.LogWarning(error, "The status event remains in the outbox and will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
