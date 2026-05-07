using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Modeling;

namespace PbiModelingMcp.Tools;

/// <summary>
/// Mutating measure tools. Every call runs through the safety pipeline in
/// <see cref="IModelingService"/>: validate → audit pre → backup (real
/// writes only) → apply → audit post. <c>dryRun: true</c> returns a TMSL
/// diff without saving.
/// </summary>
[McpServerToolType]
public sealed class WriteTools
{
    private readonly IModelingService _modeling;
    private readonly ServerOptions _serverOptions;

    public WriteTools(IModelingService modeling, IOptions<ServerOptions> serverOptions)
    {
        ArgumentNullException.ThrowIfNull(modeling);
        ArgumentNullException.ThrowIfNull(serverOptions);
        _modeling = modeling;
        _serverOptions = serverOptions.Value;
    }

    [McpServerTool(Name = "add_measure")]
    [Description(
        "Add a new measure to a table. Fails cleanly if a measure with that name already exists. " +
        "Set dryRun: true to preview the TMSL diff without saving.")]
    public Task<WriteResult> AddMeasureAsync(
        [Description("Workspace name (preferred) or workspace GUID.")] string workspace,
        [Description("Dataset (semantic model) name or GUID.")] string dataset,
        [Description("Table to add the measure to.")] string table,
        [Description("New measure name. Must be unique within the table.")] string name,
        [Description("DAX expression for the measure.")] string dax,
        [Description("Optional. Format string (e.g. '0.00%').")] string? formatString = null,
        [Description("Optional. Display folder.")] string? displayFolder = null,
        [Description("Optional. Description / tooltip.")] string? description = null,
        [Description("If true, return a TMSL diff but do not save.")] bool dryRun = false,
        CancellationToken ct = default)
        => _modeling.AddMeasureAsync(
            new AddMeasureRequest(table, name, dax, formatString, displayFolder, description, dryRun),
            ModelTools.BuildDescriptor(workspace, dataset),
            ct);

    [McpServerTool(Name = "update_measure")]
    [Description(
        "Partial update of an existing measure. Convention: any optional field left null is unchanged; " +
        "an explicit empty string clears it (where the model permits). dax cannot be cleared. " +
        "Set dryRun: true to preview the TMSL diff without saving.")]
    public Task<WriteResult> UpdateMeasureAsync(
        [Description("Workspace name (preferred) or workspace GUID.")] string workspace,
        [Description("Dataset (semantic model) name or GUID.")] string dataset,
        [Description("Table owning the measure.")] string table,
        [Description("Measure name.")] string name,
        [Description("Optional. New DAX expression. Empty string is rejected.")] string? dax = null,
        [Description("Optional. New format string; empty clears.")] string? formatString = null,
        [Description("Optional. New display folder; empty clears.")] string? displayFolder = null,
        [Description("Optional. New description; empty clears.")] string? description = null,
        [Description("If true, return a TMSL diff but do not save.")] bool dryRun = false,
        CancellationToken ct = default)
        => _modeling.UpdateMeasureAsync(
            new UpdateMeasureRequest(table, name, dax, formatString, displayFolder, description, dryRun),
            ModelTools.BuildDescriptor(workspace, dataset),
            ct);

    [McpServerTool(Name = "delete_measure")]
    [Description(
        "Delete a measure from a table. When the server is configured with RequireConfirmDelete=true (default), " +
        "a yes/no elicitation is sent unless confirm: true is passed. Set dryRun: true to preview without deleting.")]
    public async Task<WriteResult> DeleteMeasureAsync(
        IMcpServer server,
        [Description("Workspace name (preferred) or workspace GUID.")] string workspace,
        [Description("Dataset (semantic model) name or GUID.")] string dataset,
        [Description("Table owning the measure.")] string table,
        [Description("Measure name.")] string name,
        [Description("Skip the elicitation prompt for this call (only effective when RequireConfirmDelete=true).")] bool confirm = false,
        [Description("If true, do not actually delete; return a TMSL diff.")] bool dryRun = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!dryRun && _serverOptions.RequireConfirmDelete && !confirm)
        {
            // Method-group reference picks up the McpServerExtensions.ElicitAsync
            // extension on IMcpServer. The helper is unit-tested via a fake delegate.
            await ElicitConfirmAsync(server.ElicitAsync, table, name, ct).ConfigureAwait(false);
        }

        return await _modeling
            .DeleteMeasureAsync(
                new DeleteMeasureRequest(table, name, dryRun),
                ModelTools.BuildDescriptor(workspace, dataset),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Pure confirmation flow, isolated from <see cref="IMcpServer"/> so it can
    /// be unit-tested with a fake elicit delegate. Throws
    /// <see cref="OperationCanceledException"/> when the user declines or the
    /// client cancels; throws <see cref="InvalidOperationException"/> when the
    /// client doesn't support elicitation at all.
    /// </summary>
    internal static async Task ElicitConfirmAsync(
        Func<ElicitRequestParams, CancellationToken, ValueTask<ElicitResult>> elicit,
        string table,
        string name,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(elicit);

        var req = new ElicitRequestParams
        {
            Message = $"Delete measure '{name}' from table '{table}'? This cannot be undone (a TMSL backup will be written).",
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    ["confirm"] = new ElicitRequestParams.BooleanSchema
                    {
                        Title = "Confirm",
                        Description = $"Yes, delete '{name}' from '{table}'.",
                    },
                },
                Required = new[] { "confirm" },
            },
        };

        ElicitResult result;
        try
        {
            result = await elicit(req, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("elicit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Delete requires confirmation but the MCP client does not support elicitation. "
                + "Pass confirm: true on the call, or set PBI_MCP__RequireConfirmDelete=false.");
        }

        if (!string.Equals(result.Action, "accept", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationCanceledException(
                $"Delete of '{name}' on '{table}' was not confirmed (action='{result.Action}').");
        }

        var confirmed = result.Content is not null
            && result.Content.TryGetValue("confirm", out var v)
            && v.ValueKind == System.Text.Json.JsonValueKind.True;

        if (!confirmed)
        {
            throw new OperationCanceledException(
                $"Delete of '{name}' on '{table}' was declined in the elicitation prompt.");
        }
    }
}
