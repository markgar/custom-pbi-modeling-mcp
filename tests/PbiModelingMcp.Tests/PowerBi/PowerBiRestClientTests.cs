using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PbiModelingMcp.Auth;
using PbiModelingMcp.PowerBi;
using Xunit;

namespace PbiModelingMcp.Tests.PowerBi;

public class PowerBiRestClientTests
{
    [Fact]
    public async Task ListWorkspaces_FollowsNextLink()
    {
        var handler = new RecordingHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            return url.Contains("groups", StringComparison.Ordinal) && !url.Contains("page2", StringComparison.Ordinal)
                ? Json("""
                    {
                      "value": [{"id":"1","name":"A"}],
                      "@odata.nextLink": "https://api.powerbi.com/v1.0/myorg/groups?page2"
                    }
                    """)
                : Json("""
                    {
                      "value": [{"id":"2","name":"B"}, {"id":"3","name":"C"}]
                    }
                    """);
        });

        var client = MakeClient(handler);

        var result = await client.ListWorkspacesAsync(CancellationToken.None);

        result.Should().HaveCount(3);
        result.Select(w => w.Name).Should().BeEquivalentTo(["A", "B", "C"]);
        handler.Calls.Should().HaveCount(2);
        handler.Calls[1].RequestUri!.ToString().Should().EndWith("page2");
    }

    [Fact]
    public async Task ListDatasets_GuidShortCircuit_SkipsWorkspaceLookup()
    {
        var handler = new RecordingHandler(req => Json("""{"value":[]}"""));

        var client = MakeClient(handler);
        await client.ListDatasetsAsync("11111111-1111-1111-1111-111111111111", CancellationToken.None);

        // Should hit /datasets directly, never /groups for resolution.
        handler.Calls.Should().HaveCount(1);
        handler.Calls[0].RequestUri!.ToString()
            .Should().Contain("/groups/11111111-1111-1111-1111-111111111111/datasets");
    }

    [Fact]
    public async Task ListDatasets_NameToIdResolution_UsesGroupsLookup()
    {
        var calls = 0;
        var handler = new RecordingHandler(req =>
        {
            calls++;
            var url = req.RequestUri!.ToString();
            if (url.Contains("/datasets", StringComparison.Ordinal))
            {
                return Json("""{"value":[{"id":"d1","name":"DS"}]}""");
            }
            // First call: list workspaces for resolution.
            return Json("""
                {
                  "value": [
                    {"id":"00000000-0000-0000-0000-000000000001","name":"Other"},
                    {"id":"00000000-0000-0000-0000-00000000abcd","name":"Sales"}
                  ]
                }
                """);
        });

        var client = MakeClient(handler);
        var result = await client.ListDatasetsAsync("Sales", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("d1");
        // First call was groups lookup, second was datasets/{id}.
        handler.Calls.Should().HaveCount(2);
        handler.Calls[0].RequestUri!.ToString().Should().EndWith("/groups");
        handler.Calls[1].RequestUri!.ToString()
            .Should().Contain("/groups/00000000-0000-0000-0000-00000000abcd/datasets");
    }

    [Fact]
    public async Task ListDatasets_UnknownWorkspaceName_Throws()
    {
        var handler = new RecordingHandler(req =>
            Json("""{"value":[{"id":"x","name":"Other"}]}"""));

        var client = MakeClient(handler);
        var act = () => client.ListDatasetsAsync("Missing", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'Missing'*");
    }

    [Fact]
    public async Task ListDatasets_EmptyWorkspace_Throws()
    {
        var client = MakeClient(new RecordingHandler(req => Json("""{"value":[]}""")));

        var act = () => client.ListDatasetsAsync("   ", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetAsync_NonSuccess_IncludesStatusAndBody()
    {
        var handler = new RecordingHandler(req =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"nope\"}"),
            });

        var client = MakeClient(handler);
        var act = () => client.ListWorkspacesAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.Message.Should().Contain("401").And.Contain("nope");
    }

    [Fact]
    public async Task EveryRequest_SendsBearerToken()
    {
        var handler = new RecordingHandler(req => Json("""{"value":[]}"""));
        var client = MakeClient(handler, token: "TOKEN-XYZ");

        await client.ListWorkspacesAsync(CancellationToken.None);

        handler.Calls[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Calls[0].Headers.Authorization!.Parameter.Should().Be("TOKEN-XYZ");
    }

    private static PowerBiRestClient MakeClient(HttpMessageHandler handler, string token = "tok")
    {
        var http = new HttpClient(handler);
        var tokens = new StubTokenProvider(token);
        return new PowerBiRestClient(http, tokens, NullLogger<PowerBiRestClient>.Instance);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class StubTokenProvider : ITokenProvider
    {
        private readonly string _token;
        public StubTokenProvider(string token) => _token = token;
        public ValueTask<string> GetPowerBiTokenAsync(CancellationToken ct) => ValueTask.FromResult(_token);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<HttpRequestMessage> Calls { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(_respond(request));
        }
    }
}
