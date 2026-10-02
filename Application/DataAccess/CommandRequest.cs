using System.Text.Json;
using System.Text.Json.Serialization;

namespace Features.Application.DataAccess;

public sealed class CommandRequest
{
    public List<CommandOperation> Operations { get; init; } = [];
}

public sealed class CommandOperation
{
    public string Entity { get; init; } = string.Empty;
    public CommandOperationType OperationType { get; init; }
    public string? ClientRef { get; init; }
    public string? ParentRef { get; init; }
    public string? ForeignKeyProperty { get; init; }
    public Dictionary<string, JsonElement> Data { get; init; } = [];
    public Dictionary<string, JsonElement> KeyValues { get; init; } = [];
    public List<CommandOperation>? Children { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandOperationType>))]
public enum CommandOperationType
{
    Insert = 1,
    Update = 2,
    Delete = 3
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandOperationStatus>))]
public enum CommandOperationStatus
{
    Succeeded,
    Failed,
    RolledBack,
    NotRun
}

public sealed class CommandResult
{
    public bool Succeeded { get; init; }
    public string? Error { get; init; }
    public List<CommandOperationResult> Operations { get; init; } = [];
}

public sealed class CommandOperationResult
{
    public string Path { get; init; } = string.Empty;
    public string Entity { get; init; } = string.Empty;
    public CommandOperationType OperationType { get; init; }
    public string? ClientRef { get; init; }
    public CommandOperationStatus Status { get; init; }
    public string? Error { get; init; }
    public Dictionary<string, object?> GeneratedKeys { get; init; } = [];
}
