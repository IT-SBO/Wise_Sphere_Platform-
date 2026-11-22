using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WiseSphere.Portal.Services;

/// <summary>Calls the integration service for SOC-04 approved containment. The portal checks who may approve;
/// the integration service checks the client's opt-in, allow-list and duration and talks to the firewall.</summary>
public sealed class ContainmentClient(HttpClient http, IOptions<IntegrationOptions> options)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly IntegrationOptions _o = options.Value;
    public bool Enabled => _o.Url.Length > 0 && _o.Secret.Length > 0;

    public async Task<JsonElement> ListAsync(int ticketId, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, $"/containment/blocks?ticket={ticketId}", null, ct) is var (_, body) ? body : default;

    public Task<(HttpStatusCode Status, JsonElement Body)> BlockAsync(object request, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/containment/blocks", request, ct);

    public async Task<bool> UnblockAsync(string id, string by, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Delete, $"/containment/blocks/{Uri.EscapeDataString(id)}?by={Uri.EscapeDataString(by)}", null, ct)).Status == HttpStatusCode.NoContent;

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, _o.Url.TrimEnd('/') + path);
        req.Headers.Add("X-WS-Secret", _o.Secret);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return (res.StatusCode, text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }
}
