using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging;
using PbiModelingMcp.Auth;

namespace PbiModelingMcp.Connection;

/// <summary>
/// Default <see cref="ITomServerFactory"/>. Acquires an Entra access token
/// via <see cref="ITokenProvider"/> and hands it to TOM through the XMLA
/// connection string's <c>Password=&lt;token&gt;</c> slot. No
/// <c>User ID</c> is set: the token already encodes the principal, so any
/// identity the token provider can produce — Managed Identity in Azure or
/// the developer's user via <c>az login</c> locally — works with the same
/// code path.
/// </summary>
internal sealed class TomServerFactory : ITomServerFactory
{
    private readonly ITokenProvider _tokens;
    private readonly ILogger<TomServerFactory> _logger;

    public TomServerFactory(
        ITokenProvider tokens,
        ILogger<TomServerFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(logger);
        _tokens = tokens;
        _logger = logger;
    }

    public async Task<Server> OpenAsync(ConnectionDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var token = await _tokens.GetPowerBiTokenAsync(ct).ConfigureAwait(false);
        var connStr = BuildConnectionString(descriptor, token);

        _logger.LogDebug("Opening TOM Server for {Descriptor}", descriptor);

        var server = new Server();

        // TOM's Connect is synchronous and not cancellable mid-call. We run it
        // on a worker so the await side honors cancellation, and we tie disposal
        // to the worker's eventual completion — so a cancellation while Connect
        // is still in flight cannot leak a connected Server (the dispose runs
        // after Connect actually returns, on the worker thread).
        var connectTask = Task.Run(() => server.Connect(connStr), CancellationToken.None);
        return await AwaitConnectAsync(server, connectTask, ct).ConfigureAwait(false);
    }

    private static async Task<Server> AwaitConnectAsync(
        Server server, Task connectTask, CancellationToken ct)
    {
        try
        {
            await connectTask.WaitAsync(ct).ConfigureAwait(false);
            return server;
        }
        catch
        {
            // Schedule disposal once Connect actually finishes. If Connect already
            // threw, this runs immediately; if we were canceled while Connect was
            // still in flight, this disposes after it returns.
            _ = connectTask.ContinueWith(
                _ => server.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// Builds the XMLA connection string with each value properly escaped per
    /// the OLEDB connection-string grammar via <see cref="DbConnectionStringBuilder"/>.
    /// Without this, a value containing <c>;</c>, <c>=</c>, or quote chars
    /// could inject arbitrary keys — a real concern given that one of the
    /// values is a bearer token.
    /// </summary>
    /// <remarks>
    /// Exposed <c>internal</c> for unit tests.
    /// </remarks>
    internal static string BuildConnectionString(ConnectionDescriptor descriptor, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(accessToken);

        // The workspace name lands inside the Data Source URL, not as its own
        // key, so it must be URL-encoded rather than connection-string-quoted.
        var workspaceSegment = Uri.EscapeDataString(descriptor.Workspace);

        // Provider=MSOLAP is required: without it AMO does not recognise the
        // connection as XMLA-over-HTTPS and silently falls back to integrated
        // (Windows SSPI) auth, which on Linux fails with the unhelpful
        // "Authentication failed for all authenticators". The bearer token in
        // Password fully encodes the principal, so no User ID is set.
        var b = new DbConnectionStringBuilder
        {
            ["Provider"] = "MSOLAP",
            ["Data Source"] = $"powerbi://api.powerbi.com/v1.0/myorg/{workspaceSegment}",
            ["Initial Catalog"] = descriptor.Dataset,
            ["Password"] = accessToken,
        };
        return b.ConnectionString;
    }
}
