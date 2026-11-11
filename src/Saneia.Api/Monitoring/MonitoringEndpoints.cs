using Saneia.Application.Monitoring;

namespace Saneia.Api.Monitoring;

public static class MonitoringEndpoints
{
    public static void MapMonitoring(this WebApplication app)
    {
        var routes = app.MapGroup("/api/sensors").WithTags("Monitoramento")
            .RequireAuthorization("Operator").RequireRateLimiting("monitoring")
            .AddEndpointFilter(async (context, next) =>
            {
                try { return await next(context); }
                catch (CatalogUnavailable) { return Error(503, "Catálogo Java indisponível."); }
            });

        routes.MapGet("", async (int? page, int? size, HttpContext context,
            TelemetryService telemetry, CancellationToken cancellation) =>
        {
            if (page is < 0 || size is < 1 or > 200)
                return Error(400, "Use page a partir de zero e size entre 1 e 200.");
            var result = await telemetry.Map(Bearer(context), page ?? 0, size ?? 200, cancellation);
            context.Response.Headers["X-Total-Pages"] = result.TotalPages.ToString();
            return Results.Ok(result.Items);
        }).WithSummary("Lista equipamentos ativos com o estado mais recente")
            .WithDescription("Lista paginada por page (base zero) e size (até 200). Total em X-Total-Pages.")
            .Produces<Sensor[]>().Produces<ApiError>(400).Produces<ApiError>(401)
            .Produces<ApiError>(403).Produces<ApiError>(429).Produces<ApiError>(503);

        routes.MapGet("/{id}", async (string id, HttpContext context,
            TelemetryService telemetry, CancellationToken cancellation) =>
        {
            var item = await telemetry.Detail(id, Bearer(context), cancellation);
            return item is null ? Error(404, "Equipamento ativo não encontrado.") : Results.Ok(item);
        }).WithSummary("Consulta a ficha e o histórico do equipamento")
            .WithDescription("O id é o do catálogo Java. A ficha inclui até 500 amostras e 100 eventos recentes.")
            .Produces<Sensor>().Produces<ApiError>(404).Produces<ApiError>(401)
            .Produces<ApiError>(403).Produces<ApiError>(429).Produces<ApiError>(503);

        routes.MapPost("/{id}/readings", async (string id, ReadingInput input, HttpContext context,
                TelemetryService telemetry, CancellationToken cancellation) =>
            {
                var result = await telemetry.Ingest(id, input.ToCommand(), Bearer(context), cancellation);
                return ToHttpResult(result);
            })
            .WithSummary("Registra uma medição para teste da telemetria")
            .WithDescription("Use o id e o prefixo do equipamento Java. Retorna 201 na gravação, 200 no replay idêntico " +
                "e 409 se a mesma chave deviceId/sampleId vier com outros dados ou a medição estiver fora de ordem.")
            .Produces<ReadingReceipt>(201).Produces<ReadingReceipt>().Produces<ApiError>(400)
            .Produces<ApiError>(404).Produces<ApiError>(409).Produces<ApiError>(415)
            .Produces<ApiError>(401).Produces<ApiError>(403).Produces<ApiError>(429).Produces<ApiError>(503);
    }

    private static IResult ToHttpResult(ReadingOutcome result) => result switch
    {
        ReadingOutcome.Created outcome => Results.Json(outcome.Receipt, statusCode: 201),
        ReadingOutcome.Replayed outcome => Results.Ok(outcome.Receipt),
        ReadingOutcome.Invalid outcome => Error(400, outcome.Message),
        ReadingOutcome.SensorNotFound => Error(404, "Equipamento ativo não encontrado."),
        ReadingOutcome.Conflict outcome => Error(409, outcome.Message),
        _ => throw new InvalidOperationException("Resultado de gravação desconhecido.")
    };

    private static string Bearer(HttpContext context) => context.Request.Headers.Authorization.ToString();
    private static IResult Error(int status, string message) => Results.Json(new ApiError(message), statusCode: status);
}
