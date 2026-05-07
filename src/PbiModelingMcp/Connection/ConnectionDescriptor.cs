namespace PbiModelingMcp.Connection;

/// <summary>
/// Identifies a target Power BI model. Workspace and Dataset accept either
/// the display name or the GUID — the XMLA endpoint requires the workspace
/// **name** specifically; Initial Catalog accepts either.
/// </summary>
/// <param name="Workspace">Workspace name (preferred) or GUID.</param>
/// <param name="Dataset">Dataset (semantic model) name or GUID.</param>
/// <param name="CredentialName">
/// Reserved for future per-workspace credential mapping. Null = use the
/// ambient identity resolved by
/// <see cref="Auth.DefaultAzureCredentialTokenProvider"/>.
/// </param>
public sealed record ConnectionDescriptor(
    string Workspace,
    string Dataset,
    string? CredentialName = null)
{
    public override string ToString() => $"{Workspace}/{Dataset}";
}
