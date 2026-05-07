using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PbiModelingMcp.PowerBi;
using PbiModelingMcp.Tools;
using Xunit;

namespace PbiModelingMcp.Tests.Tools;

public class DiscoveryToolsTests
{
    [Fact]
    public async Task ListDatasets_NullOrBlankWorkspace_Throws()
    {
        var rest = new StubRest();
        var tools = new DiscoveryTools(rest);

        var act = () => tools.ListDatasetsAsync(workspace: " ");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*workspace is required*");
    }

    [Fact]
    public async Task ListDatasets_PassesWorkspaceThroughToRest()
    {
        var rest = new StubRest();
        var tools = new DiscoveryTools(rest);

        await tools.ListDatasetsAsync(workspace: "Sales");

        rest.LastWorkspace.Should().Be("Sales");
    }

    private sealed class StubRest : IPowerBiRestClient
    {
        public string? LastWorkspace { get; private set; }

        public Task<IReadOnlyList<WorkspaceSummary>> ListWorkspacesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WorkspaceSummary>>(Array.Empty<WorkspaceSummary>());

        public Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string workspace, CancellationToken ct)
        {
            LastWorkspace = workspace;
            return Task.FromResult<IReadOnlyList<DatasetSummary>>(Array.Empty<DatasetSummary>());
        }
    }
}
