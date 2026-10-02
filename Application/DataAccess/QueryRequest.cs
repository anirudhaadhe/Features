using System.Text.Json;

namespace Features.Application.DataAccess;

public sealed class QueryRequest
{
    public string Entity { get; init; } = string.Empty;
    public List<string>? Select { get; init; }
    public FilterNode? Filter { get; init; }
    public List<SortRule>? Sort { get; init; }
    public PageRequest? Page { get; init; }
}

public sealed class FilterNode
{
    public string? Logic { get; init; }
    public List<FilterNode>? Rules { get; init; }
    public string? Field { get; init; }
    public string? Operator { get; init; }
    public JsonElement? Value { get; init; }
}

public sealed class SortRule
{
    public string Field { get; init; } = string.Empty;
    public string Direction { get; init; } = "asc";
}

public sealed class PageRequest
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}
