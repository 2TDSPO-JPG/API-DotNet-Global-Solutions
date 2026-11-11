using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using System.Net.Http.Json;
using Saneia.Application.Monitoring;

namespace Saneia.Infrastructure.Catalog;

public sealed class JavaCatalog(HttpClient http) : ISensorCatalog
{
    public async Task<CatalogPage> List(string bearer, int page, int size, CancellationToken cancellation)
    {
        var route = FormattableString.Invariant($"sensors?page={page}&size={size}");
        using var response = await Request(route, bearer, cancellation);
        if (!response.IsSuccessStatusCode) throw Unavailable(response.StatusCode);
        var items = await Read<CatalogSensor[]>(response, cancellation);
        var totalPages = 1;
        if (response.Headers.TryGetValues("X-Total-Pages", out var values) &&
            int.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
            totalPages = parsed;
        return new(items, totalPages);
    }

    public async Task<CatalogSensor?> Get(string id, string bearer, CancellationToken cancellation)
    {
        if (id.Length is 0 or > 80 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) return null;
        using var response = await Request($"sensors/{Uri.EscapeDataString(id)}", bearer, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw Unavailable(response.StatusCode);
        var sensor = await Read<CatalogSensor>(response, cancellation);
        if (sensor.Id != id || sensor.Coordinate is null || sensor.Capacity <= 0 ||
            sensor.Limit <= 0 || sensor.Limit > sensor.Capacity)
            throw new CatalogUnavailable("O catálogo retornou um equipamento inválido.");
        return sensor;
    }

    public async Task<DeviceCatalog?> GetDevice(string prefix, string key, CancellationToken cancellation)
    {
        using var response = await Request("internal/catalog/sensors/by-prefix/" + Uri.EscapeDataString(prefix),
            key, cancellation, serviceCredential: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw Unavailable(response.StatusCode);
        var sensor = await Read<DeviceCatalog>(response, cancellation);
        if (string.IsNullOrWhiteSpace(sensor.Id) || sensor.Prefix != prefix ||
            sensor.Capacity <= 0 || sensor.Limit <= 0 || sensor.Limit > sensor.Capacity)
            throw new CatalogUnavailable("O catálogo retornou um equipamento inválido.");
        return sensor;
    }

    private async Task<HttpResponseMessage> Request(string route, string credential, CancellationToken cancellation,
        bool serviceCredential = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (serviceCredential)
            request.Headers.Add("X-Saneia-Integration-Key", credential);
        else
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(credential);
        try
        {
            // ResponseContentRead keeps the configured timeout in effect for the entire response.
            return await http.SendAsync(request, cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   ex is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new CatalogUnavailable("Não foi possível consultar o catálogo Java.", ex);
        }
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellation)
                   ?? throw new CatalogUnavailable("O catálogo retornou uma resposta vazia.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new CatalogUnavailable("O catálogo retornou uma resposta inválida.", ex);
        }
    }

    private static CatalogUnavailable Unavailable(HttpStatusCode code) =>
        new($"O catálogo Java respondeu com HTTP {(int)code}.");
}
