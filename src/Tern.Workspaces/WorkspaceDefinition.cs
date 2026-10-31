using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tern.Workspaces;

/// <summary>Portable saved-document paths are relative to the workspace directory. Unsaved buffers are recovery data, never stored here.</summary>
public sealed record WorkspaceWorkingContext(IReadOnlyList<TabState> Tabs, string? ActiveTab, string ViewMode);

/// <summary>A portable named context whose content root is the folder containing this descriptor.</summary>
public sealed record WorkspaceDefinition(int Version, string Name)
{
    public const string FileName = ".tern-workspace.json";
    public const int MaxDefinitionBytes = 256 * 1024;
    public WorkspaceWorkingContext? WorkingContext { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    public static WorkspaceDefinition? Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaxDefinitionBytes) throw new InvalidDataException("The workspace definition is too large. Repair or restore its .tern-workspace.json file before reopening the workspace.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var offset = bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0;
        var definition = JsonSerializer.Deserialize<WorkspaceDefinition>(bytes.AsSpan(offset));
        if (definition is not { Version: 1 } || string.IsNullOrWhiteSpace(definition.Name))
            throw new InvalidDataException("This workspace definition is unsupported or has no name. Repair or restore its .tern-workspace.json file before reopening the workspace.");
        return definition;
    }

    public static void Create(string root, string name)
    {
        using var stream = new FileStream(Path.Combine(root, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, new WorkspaceDefinition(1, name));
    }

    public static void SaveWorkingContext(string root, string name, WorkspaceWorkingContext context)
    {
        var current = Load(root) ?? new WorkspaceDefinition(1, name);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(current with { WorkingContext = context });
        if (bytes.Length > MaxDefinitionBytes) throw new InvalidDataException("The workspace context exceeds the supported metadata size. Close some saved-document tabs before retrying.");
        AtomicFile.WriteAllBytes(Path.Combine(root, FileName), bytes);
    }
}
