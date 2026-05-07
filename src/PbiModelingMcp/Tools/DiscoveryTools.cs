using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using PbiModelingMcp.PowerBi;

namespace PbiModelingMcp.Tools;

/// <summary>
/// Power BI REST API discovery tools — workspace + dataset listings the
/// current identity can see.
/// </summary>
[McpServerToolType]
public sealed class DiscoveryTools
{
    private readonly IPowerBiRestClient _rest;

    public DiscoveryTools(IPowerBiRestClient rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        _rest = rest;
    }

    [McpServerTool(Name = "list_workspaces")]
    [Description("List Power BI workspaces (groups) visible to the current identity.")]
    public Task<IReadOnlyList<WorkspaceSummary>> ListWorkspacesAsync(CancellationToken ct = default)
        => _rest.ListWorkspacesAsync(ct);

    [McpServerTool(Name = "list_datasets")]
    [Description("List datasets (semantic models) in a workspace.")]
    public Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(
        [Description("Workspace name or GUID.")] string workspace,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            throw new ArgumentException("workspace is required.", nameof(workspace));
        }

        return _rest.ListDatasetsAsync(workspace, ct);
    }
}
