using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbiModelingMcp;
using PbiModelingMcp.Audit;
using PbiModelingMcp.Auth;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;
using PbiModelingMcp.Http;
using PbiModelingMcp.Modeling;
using PbiModelingMcp.PowerBi;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// ----------------------------------------------------------------------------
// CRITICAL: when running in stdio transport mode, stdout is reserved for the
// MCP JSON-RPC transport. Every byte that goes to stdout must be MCP. All
// logging therefore goes to stderr or to a file under ${AuditDir}/logs/.
// In HTTP transport mode the same convention is preserved for symmetry.
// ----------------------------------------------------------------------------

// Pull values from a sibling .env file (if present) before configuration
// binding. Real environment variables always win.
DotEnv.Load();

// Resolve server options up front so Serilog can write to AuditDir before
// the host is built, and so we know which transport to spin up. We bind a
// throwaway POCO here (no validation — DI binds + validates the same data
// during host startup).
var earlyConfig = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();
var earlyServerOpts = earlyConfig
    .GetSection(ServerOptions.SectionName)
    .Get<ServerOptions>() ?? new ServerOptions();

var auditDir = earlyServerOpts.ResolveAuditDir();
var logsDir = Path.Combine(auditDir, "logs");
Directory.CreateDirectory(logsDir);

var minLevel = LogLevelParser.Parse(earlyServerOpts.LogLevel, out var levelRecognized);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        standardErrorFromLevel: LogEventLevel.Verbose,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}",
        formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .WriteTo.File(
        formatter: new CompactJsonFormatter(),
        path: Path.Combine(logsDir, "server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

if (!levelRecognized)
{
    Log.Warning(
        "PBI_MCP__LogLevel value {Raw} not recognized; defaulting to {Level}. " +
        "Allowed: Verbose, Debug, Information, Warning, Error, Fatal.",
        earlyServerOpts.LogLevel,
        minLevel);
}

try
{
    Log.Information("Starting Power BI Modeling MCP server (transport: {Transport}, audit dir: {Dir})",
        earlyServerOpts.Transport, auditDir);

    var transport = (earlyServerOpts.Transport ?? "stdio").Trim();
    if (string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
    {
        return await RunHttpAsync(args).ConfigureAwait(false);
    }

    if (!string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase))
    {
        throw new OptionsValidationException(
            nameof(ServerOptions.Transport),
            typeof(ServerOptions),
            new[] { $"PBI_MCP__Transport must be 'stdio' or 'http'; got '{transport}'." });
    }

    return await RunStdioAsync(args).ConfigureAwait(false);
}
catch (OptionsValidationException ex)
{
    var failures = string.Join("; ", ex.Failures);
    Log.Fatal(ex, "Configuration validation failed for {OptionsType}: {Failures}",
        ex.OptionsType?.FullName ?? "<unknown>", failures);
#pragma warning disable RS0030 // Emergency stderr diagnostic: serilog file writes can be lost on early exit.
    Console.Error.WriteLine(
        $"FATAL: configuration validation failed ({ex.OptionsType?.FullName}): {failures}");
#pragma warning restore RS0030
    return 78; // EX_CONFIG
}
catch (Exception ex)
{
    Log.Fatal(ex, "Server crashed: {ExceptionType}: {Message}",
        ex.GetType().FullName, ex.Message);
#pragma warning disable RS0030 // Emergency stderr diagnostic: serilog file writes can be lost on early exit.
    Console.Error.WriteLine($"FATAL: {ex.GetType().FullName}: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);
#pragma warning restore RS0030
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static async Task<int> RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(dispose: false);

    RegisterCoreServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var host = builder.Build();
    Log.Debug("Stdio host built; starting RunAsync");
    await host.RunAsync().ConfigureAwait(false);
    Log.Information("Server stopped cleanly");
    return 0;
}

static async Task<int> RunHttpAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(dispose: false);

    RegisterCoreServices(builder.Services, builder.Configuration);

    var opts = builder.Configuration
        .GetSection(ServerOptions.SectionName)
        .Get<ServerOptions>() ?? new ServerOptions();

    // Fail-closed startup checks. Any failure aborts process start with a
    // clear stderr message — see PbiModelingMcp.Http.HttpTransportValidation.
    var (_, isLoopback) = HttpTransportValidation.ValidateForHttp(opts);
    var actor = opts.ResolveActor();
    var banner = HttpTransportValidation.BuildStartupBanner(opts, actor, isLoopback);
#pragma warning disable RS0030 // Stderr banner is intentional, mirrors plan §4.
    Console.Error.WriteLine(banner);
#pragma warning restore RS0030
    Log.Warning("{Banner}", banner);

    var app = HttpServerHost.Build(builder, opts);
    app.Urls.Clear();

    var listenPort = opts.ResolveListenPort();
    var bindHost = opts.HttpListenAllInterfaces ? "*" : opts.HttpHost;
    app.Urls.Add($"http://{bindHost}:{listenPort}");

    Log.Debug("HTTP host built; starting RunAsync on http://{Host}:{Port}", bindHost, listenPort);
    await app.RunAsync().ConfigureAwait(false);
    Log.Information("Server stopped cleanly");
    return 0;
}

static void RegisterCoreServices(IServiceCollection services, IConfiguration config)
{
    services
        .AddOptions<ServerOptions>()
        .Bind(config.GetSection(ServerOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    services.AddSingleton<ITokenProvider, DefaultAzureCredentialTokenProvider>();
    services.AddSingleton<ITomServerFactory, TomServerFactory>();
    services.AddSingleton<IConnectionManager, ConnectionManager>();
    services.AddSingleton<IModelingService, ModelingService>();
    services.AddSingleton<IAuditLogger, AuditLogger>();
    services.AddSingleton<IBackupWriter, BackupWriter>();

    services.AddHttpClient<IPowerBiRestClient, PowerBiRestClient>();
}
