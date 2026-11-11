using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using Saneia.Application.Monitoring;
using Saneia.Infrastructure.Catalog;

namespace Saneia.Infrastructure.Mqtt;

internal sealed class MqttIngestion : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<MqttIngestion> log;
    private readonly Settings settings;
    private readonly string key;

    public MqttIngestion(IServiceScopeFactory scopes, IConfiguration config, ILogger<MqttIngestion> log)
    {
        this.scopes = scopes;
        this.log = log;
        settings = config.GetSection("Mqtt").Get<Settings>() ?? new();
        key = config["SANEIA_INTEGRATION_KEY"] ?? "";
        if (System.Text.Encoding.UTF8.GetByteCount(key) < 32 || string.IsNullOrWhiteSpace(settings.Host)
            || settings.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(settings.Topic)
            || settings.Topic.IndexOfAny(['+', '#', '\0']) >= 0 || string.IsNullOrWhiteSpace(settings.DeviceId)
            || string.IsNullOrWhiteSpace(settings.Prefix) || settings.HeightCm is <= 0 or > 400
            || settings.EmptyDistanceCm < settings.HeightCm || settings.EmptyDistanceCm > 400
            || (settings.Username is null) != (settings.Password is null)
            || !settings.Tls && settings.Username is not null)
            throw new InvalidOperationException("Confira Mqtt e SANEIA_INTEGRATION_KEY antes de habilitar a ingestão.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttFactory();
        var connection = new MqttClientOptionsBuilder().WithTcpServer(settings.Host, settings.Port)
            .WithClientId("saneia-" + Guid.NewGuid().ToString("N")[..16]).WithCleanSession();
        if (settings.Tls) connection.WithTlsOptions(options => options.UseTls());
        if (settings.Username is not null) connection.WithCredentials(settings.Username, settings.Password);
        var options = connection.Build();
        var subscription = factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(filter => filter.WithTopic(settings.Topic).WithAtLeastOnceQoS()).Build();
        while (!stoppingToken.IsCancellationRequested)
        {
            using var client = factory.CreateMqttClient();
            client.ApplicationMessageReceivedAsync += args => Receive(args.ApplicationMessage, stoppingToken);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var connected = await client.ConnectAsync(options, deadline.Token);
                if (connected.ResultCode != MqttClientConnectResultCode.Success)
                    throw new InvalidOperationException("O broker recusou a conexão MQTT.");
                var subscribed = await client.SubscribeAsync(subscription, deadline.Token);
                if (subscribed.Items.Count != 1 || subscribed.Items.Any(item => (int)item.ResultCode > 2))
                    throw new InvalidOperationException("O broker recusou a inscrição no tópico MQTT.");
                log.LogInformation("MQTT inscrito no tópico {Topic} para {Prefix}.", settings.Topic, settings.Prefix);
                while (client.IsConnected)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("Conexão MQTT indisponível ({Error}). Nova tentativa em 5 segundos.", ex.GetType().Name);
            }
            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task Receive(MqttApplicationMessage message, CancellationToken cancellation)
    {
        if (message.Topic != settings.Topic || message.Retain || message.PayloadSegment.Count is 0 or > 256)
            return;
        try
        {
            var sample = JsonSerializer.Deserialize<WokwiSample>(message.PayloadSegment.AsSpan(), Json);
            if (sample?.SchemaVersion != 1 || sample.SensorId != settings.DeviceId
                || sample.SampleId is not { Length: > 0 and <= 80 } sampleId
                || sampleId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                || sample.DistanceCm is not (>= 2 and <= 400))
                return;

            await using var scope = scopes.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<JavaCatalog>();
            var sensor = await catalog.GetDevice(settings.Prefix, key, cancellation);
            if (sensor is null)
            {
                log.LogWarning("Cadastre o equipamento {Prefix} no catálogo Java para receber leituras MQTT.", settings.Prefix);
                return;
            }

            var level = Math.Round(Math.Clamp(settings.EmptyDistanceCm - sample.DistanceCm.Value, 0, settings.HeightCm), 1);
            var volume = Math.Round(level / settings.HeightCm * sensor.Capacity, MidpointRounding.AwayFromZero);
            var telemetry = scope.ServiceProvider.GetRequiredService<TelemetryService>();
            var outcome = await telemetry.IngestMqtt(sensor, sampleId, volume, level, cancellation);
            if (outcome is ReadingOutcome.Invalid or ReadingOutcome.Conflict)
                log.LogWarning("Medição MQTT rejeitada para {Prefix} ({Outcome}).", sensor.Prefix, outcome.GetType().Name);
            else if (outcome is ReadingOutcome.Created)
                log.LogDebug("Medição MQTT registrada para {Prefix}.", sensor.Prefix);
        }
        catch (JsonException)
        {
            log.LogDebug("Payload MQTT fora do formato Wokwi.");
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            log.LogWarning("Falha ao processar medição MQTT para {Prefix} ({Error}).", settings.Prefix, ex.GetType().Name);
        }
    }

    private sealed class Settings
    {
        public string Host { get; init; } = "";
        public int Port { get; init; }
        public string Topic { get; init; } = "";
        public bool Tls { get; init; }
        public string? Username { get; init; }
        public string? Password { get; init; }
        public string DeviceId { get; init; } = "";
        public string Prefix { get; init; } = "";
        public decimal HeightCm { get; init; }
        public decimal EmptyDistanceCm { get; init; }
    }

    private sealed record WokwiSample(int SchemaVersion, string? SensorId, string? SampleId, decimal? DistanceCm);
}
