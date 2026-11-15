namespace Tern.Desktop.Services;

/// <summary>A credential-free GUI snapshot. A past test never promises present reachability.</summary>
public sealed record ConnectionSummary(string ProfileId, string Name, string Capabilities, string Destination, string Model,
    string State, DateTimeOffset? TestedAt)
{
    public string Description => $"{Name} · {Capabilities}\n{Destination}\nModel: {Model}\n{State}"
        + (TestedAt is { } time ? $" · {time.LocalDateTime:g}" : "");
}
