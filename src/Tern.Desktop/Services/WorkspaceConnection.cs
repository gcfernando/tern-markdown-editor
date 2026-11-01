namespace Tern.Desktop.Services;

/// <summary>A saved API model, Copilot CLI, or manually operated MCP tool server.</summary>
public sealed record WorkspaceConnection(string Id, string Name, string Kind, string Model, string Destination)
{
    public bool CanWrite => Kind is "api" or "copilot";
    public string Endpoint => Destination;
    public string ProviderLabel => Kind switch { "copilot" => "GitHub Copilot", "mcp" => "MCP tools", _ => "AI API" };
    public string Summary => Kind switch
    {
        "copilot" => Model + " · GitHub Copilot",
        "mcp" => "Tools · " + (Uri.TryCreate(Destination, UriKind.Absolute, out var server) ? server.Host : Destination),
        _ => Model + " · " + (Uri.TryCreate(Destination, UriKind.Absolute, out var endpoint) ? endpoint.Host : Destination)
    };
    public override string ToString() => Name;
}
