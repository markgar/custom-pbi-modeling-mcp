using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PbiModelingMcp.Auth;

namespace PbiModelingMcp.PowerBi;

/// <summary>
/// Thin Power BI REST client for the read-only workspace / dataset listings
/// the MCP server exposes. We intentionally do NOT pull in
/// <c>Microsoft.PowerBI.Api</c> just for two GETs — it brings a heavy
/// dependency tree (Newtonsoft etc.) and we already have a token provider.
/// </summary>
public interface IPowerBiRestClient
{
    Task<IReadOnlyList<WorkspaceSummary>> ListWorkspacesAsync(CancellationToken ct);

    /// <summary>
    /// Lists datasets in a workspace. <paramref name="workspace"/> may be a
    /// workspace name or GUID; names are resolved via
    /// <see cref="ListWorkspacesAsync"/>.
    /// </summary>
    Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string workspace, CancellationToken ct);
}

public sealed record WorkspaceSummary(
    string Id,
    string Name,
    bool? IsReadOnly,
    bool? IsOnDedicatedCapacity,
    string? CapacityId);

public sealed record DatasetSummary(
    string Id,
    string Name,
    string? ConfiguredBy,
    bool? IsRefreshable,
    string? WebUrl);

internal sealed class PowerBiRestClient : IPowerBiRestClient
{
    private const string BaseUrl = "https://api.powerbi.com/v1.0/myorg/";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly ILogger<PowerBiRestClient> _logger;

    public PowerBiRestClient(HttpClient http, ITokenProvider tokens, ILogger<PowerBiRestClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _tokens = tokens;
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        }
    }

    public async Task<IReadOnlyList<WorkspaceSummary>> ListWorkspacesAsync(CancellationToken ct)
    {
        var all = new List<WorkspaceSummary>();
        string? next = "groups";
        while (next is not null)
        {
            var page = await GetAsync<OData<WorkspaceDto>>(next, ct).ConfigureAwait(false);
            foreach (var w in page.Value)
            {
                all.Add(new WorkspaceSummary(
                    w.Id,
                    w.Name ?? string.Empty,
                    w.IsReadOnly,
                    w.IsOnDedicatedCapacity,
                    w.CapacityId));
            }
            next = page.NextLink;
        }
        return all;
    }

    public async Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            throw new ArgumentException("workspace is required.", nameof(workspace));
        }

        var groupId = await ResolveWorkspaceIdAsync(workspace, ct).ConfigureAwait(false);
        var all = new List<DatasetSummary>();
        string? next = $"groups/{groupId}/datasets";
        while (next is not null)
        {
            var page = await GetAsync<OData<DatasetDto>>(next, ct).ConfigureAwait(false);
            foreach (var d in page.Value)
            {
                all.Add(new DatasetSummary(
                    d.Id,
                    d.Name ?? string.Empty,
                    d.ConfiguredBy,
                    d.IsRefreshable,
                    d.WebUrl));
            }
            next = page.NextLink;
        }
        return all;
    }

    private async Task<string> ResolveWorkspaceIdAsync(string workspace, CancellationToken ct)
    {
        // GUID short-circuit: skip the round-trip if the caller already gave us one.
        if (Guid.TryParse(workspace, out _))
        {
            return workspace;
        }

        var groups = await ListWorkspacesAsync(ct).ConfigureAwait(false);
        var match = groups.FirstOrDefault(g =>
            string.Equals(g.Name, workspace, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Workspace '{workspace}' not found, or the current identity has no access.");
        }
        return match.Id;
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        // The first page uses a relative URL against BaseAddress; subsequent
        // OData @odata.nextLink values are absolute.
        var uri = Uri.TryCreate(url, UriKind.Absolute, out var abs) ? abs : new Uri(url, UriKind.Relative);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        var token = await _tokens.GetPowerBiTokenAsync(ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Power BI REST {Status} on {Url}: {Body}",
                (int)resp.StatusCode, url, Truncate(body, 512));
            throw new HttpRequestException(
                $"Power BI REST GET {url} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. {Truncate(body, 256)}");
        }

        var result = await resp.Content
            .ReadFromJsonAsync<T>(JsonOpts, ct)
            .ConfigureAwait(false);
        return result
            ?? throw new InvalidOperationException($"Power BI REST returned empty body for {url}.");
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");

    private sealed class OData<T>
    {
        public IReadOnlyList<T> Value { get; init; } = Array.Empty<T>();

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; init; }
    }

    private sealed class WorkspaceDto
    {
        public string Id { get; init; } = "";
        public string? Name { get; init; }
        public bool? IsReadOnly { get; init; }
        public bool? IsOnDedicatedCapacity { get; init; }
        public string? CapacityId { get; init; }
    }

    private sealed class DatasetDto
    {
        public string Id { get; init; } = "";
        public string? Name { get; init; }
        public string? ConfiguredBy { get; init; }
        public bool? IsRefreshable { get; init; }
        public string? WebUrl { get; init; }
    }
}
