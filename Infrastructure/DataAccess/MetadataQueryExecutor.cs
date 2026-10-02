using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Features.Application.DataAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Features.Infrastructure.DataAccess;

internal sealed class MetadataQueryExecutor(DbContext dbContext, IEntityMetadataResolver resolver)
{
    private const int MaximumPageSize = 500;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        QueryRequest request,
        CancellationToken cancellationToken)
    {
        var rootEntityType = resolver.ResolveEntityType(request.Entity);
        if (rootEntityType.GetDerivedTypes().Any())
        {
            throw new DataEngineValidationException(
                $"Queries against inheritance hierarchies are not supported for '{rootEntityType.ClrType.Name}'.");
        }

        var pageNumber = request.Page?.PageNumber ?? 1;
        var pageSize = request.Page?.PageSize ?? 25;
        if (pageNumber < 1 || pageSize < 1 || pageSize > MaximumPageSize)
        {
            throw new DataEngineValidationException($"Page number must be positive and page size must be between 1 and {MaximumPageSize}.");
        }

        var sqlBuilder = new SqlQueryBuilder(rootEntityType, resolver);
        var selected = request.Select is { Count: > 0 }
            ? request.Select.Select(field => (Name: field, Path: resolver.ResolveScalarPath(request.Entity, field))).ToArray()
            : rootEntityType.GetProperties()
                .Select(property => (Name: property.Name, Path: new ResolvedScalarPath(rootEntityType, [], property)))
                .ToArray();

        if (selected.Length == 0)
        {
            throw new DataEngineValidationException($"Entity '{rootEntityType.ClrType.Name}' has no selectable scalar properties.");
        }

        var projection = string.Join(", ", selected.Select((item, index) =>
            $"{sqlBuilder.Column(item.Path)} AS {QuoteIdentifier($"c{index}")}"));

        var where = request.Filter is null ? null : CompileFilter(request.Filter, request.Entity, rootEntityType, sqlBuilder);
        var orderBy = CompileSort(request.Sort, request.Entity, rootEntityType, sqlBuilder);
        if (orderBy.Count == 0)
        {
            var primaryKey = rootEntityType.FindPrimaryKey();
            if (primaryKey is null)
            {
                throw new DataEngineValidationException(
                    $"Entity '{rootEntityType.ClrType.Name}' has no primary key; specify a sort for paging.");
            }

            orderBy.AddRange(primaryKey.Properties.Select(property =>
                $"{sqlBuilder.Column(new ResolvedScalarPath(rootEntityType, [], property))} ASC"));
        }

        var offset = checked((long)(pageNumber - 1) * pageSize);
        var offsetParameter = sqlBuilder.AddParameter(offset);
        var limitParameter = sqlBuilder.AddParameter(pageSize);
        var (table, schema) = resolver.GetTable(rootEntityType);
        var from = $"{QuoteTable(schema, table)} AS {QuoteIdentifier("t0")}";
        var sql = $"SELECT {projection} FROM {from}{sqlBuilder.Joins}" +
            (where is null ? string.Empty : $" WHERE {where}") +
            $" ORDER BY {string.Join(", ", orderBy)} OFFSET {offsetParameter} ROWS FETCH NEXT {limitParameter} ROWS ONLY";

        return await ExecuteSqlAsync(sql, sqlBuilder.Parameters, selected.Select(item => item.Name).ToArray(), cancellationToken);
    }

    private string CompileFilter(FilterNode node, string rootEntityName, IEntityType rootEntityType, SqlQueryBuilder sqlBuilder)
    {
        if (node is null)
        {
            throw new DataEngineValidationException("A filter rule cannot be null.");
        }

        if (node.Rules is not null)
        {
            var logic = node.Logic?.ToUpperInvariant();
            if (logic is not ("AND" or "OR") || node.Rules.Count == 0 || node.Field is not null || node.Operator is not null)
            {
                throw new DataEngineValidationException("A filter group must have AND/OR logic and at least one rule.");
            }

            return "(" + string.Join(logic == "AND" ? " AND " : " OR ",
                node.Rules.Select(rule => CompileFilter(rule, rootEntityName, rootEntityType, sqlBuilder))) + ")";
        }

        if (node.Logic is not null || string.IsNullOrWhiteSpace(node.Field) || string.IsNullOrWhiteSpace(node.Operator))
        {
            throw new DataEngineValidationException("A filter rule requires field and operator values.");
        }

        var path = resolver.ResolveScalarPath(rootEntityName, node.Field);
        var column = sqlBuilder.Column(path);
        var operation = node.Operator.ToLowerInvariant();
        var hasValue = node.Value is { ValueKind: not JsonValueKind.Undefined };

        if (operation is "isnull" or "isnotnull")
        {
            if (hasValue)
            {
                throw new DataEngineValidationException($"Operator '{node.Operator}' does not accept a value.");
            }

            return $"{column} IS {(operation == "isnull" ? string.Empty : "NOT ")}NULL";
        }

        if (!hasValue)
        {
            throw new DataEngineValidationException($"Operator '{node.Operator}' requires a value.");
        }

        var valueElement = node.Value!.Value;
        if (valueElement.ValueKind == JsonValueKind.Null)
        {
            if (operation is "eq" or "ne")
            {
                return $"{column} IS {(operation == "ne" ? "NOT " : string.Empty)}NULL";
            }

            throw new DataEngineValidationException($"Operator '{node.Operator}' does not accept a null value.");
        }

        if (operation == "in")
        {
            if (valueElement.ValueKind != JsonValueKind.Array || valueElement.GetArrayLength() == 0)
            {
                throw new DataEngineValidationException("The 'in' operator requires a non-empty array value.");
            }

            var parameters = valueElement.EnumerateArray().Select(element =>
                sqlBuilder.AddParameter(ConvertValue(element, path.Property))).ToArray();
            return $"{column} IN ({string.Join(", ", parameters)})";
        }

        if (operation is "contains" or "startswith" or "endswith")
        {
            if (path.Property.ClrType != typeof(string) || valueElement.ValueKind != JsonValueKind.String)
            {
                throw new DataEngineValidationException($"Operator '{node.Operator}' requires a string property and string value.");
            }

            var text = valueElement.GetString()!;
            var parameter = sqlBuilder.AddParameter(text);
            return operation switch
            {
                "contains" => $"CHARINDEX({parameter}, {column}) > 0",
                "startswith" => $"CHARINDEX({parameter}, {column}) = 1",
                _ => $"RIGHT({column}, LEN({parameter})) = {parameter}"
            };
        }

        var comparison = operation switch
        {
            "eq" => "=",
            "ne" => "<>",
            "gt" => ">",
            "gte" => ">=",
            "lt" => "<",
            "lte" => "<=",
            _ => throw new DataEngineValidationException($"Operator '{node.Operator}' is not supported.")
        };

        return $"{column} {comparison} {sqlBuilder.AddParameter(ConvertValue(valueElement, path.Property))}";
    }

    private List<string> CompileSort(
        List<SortRule>? sortRules,
        string rootEntityName,
        IEntityType rootEntityType,
        SqlQueryBuilder sqlBuilder)
    {
        var orderBy = new List<string>();
        if (sortRules is null)
        {
            return orderBy;
        }

        foreach (var sortRule in sortRules)
        {
            if (sortRule is null)
            {
                throw new DataEngineValidationException("A sort rule cannot be null.");
            }

            var direction = sortRule.Direction?.ToUpperInvariant();
            if (direction is not ("ASC" or "DESC"))
            {
                throw new DataEngineValidationException($"Sort direction '{sortRule.Direction}' is not supported.");
            }

            var path = resolver.ResolveScalarPath(rootEntityName, sortRule.Field);
            orderBy.Add($"{sqlBuilder.Column(path)} {direction}");
        }

        return orderBy;
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteSqlAsync(
        string sql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        IReadOnlyList<string> outputNames,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>(outputNames.Count, StringComparer.Ordinal);
                for (var index = 0; index < outputNames.Count; index++)
                {
                    var value = reader.GetValue(index);
                    row[outputNames[index]] = value is DBNull ? null : value;
                }

                rows.Add(row);
            }

            return rows;
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static object? ConvertValue(JsonElement value, IProperty property)
    {
        var targetType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        object? converted;
        try
        {
            converted = value.Deserialize(targetType, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new DataEngineValidationException(
                $"Filter value cannot be converted to property '{property.Name}' of type '{targetType.Name}': {exception.Message}");
        }

        return property.GetTypeMapping().Converter?.ConvertToProvider(converted) ?? converted;
    }

    private static string QuoteTable(string? schema, string table) => schema is null
        ? QuoteIdentifier(table)
        : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class SqlQueryBuilder(IEntityType rootEntityType, IEntityMetadataResolver resolver)
    {
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private readonly List<string> _joins = [];
        private readonly List<(string Name, object? Value)> _parameters = [];
        private int _nextAlias = 1;
        private int _nextParameter;

        public string Joins => string.Concat(_joins);
        public IReadOnlyList<(string Name, object? Value)> Parameters => _parameters;

        public string Column(ResolvedScalarPath path)
        {
            var alias = "t0";
            var currentEntityType = rootEntityType;
            var navigationPath = string.Empty;
            foreach (var navigation in path.Navigations)
            {
                navigationPath = navigationPath.Length == 0
                    ? navigation.Name
                    : $"{navigationPath}.{navigation.Name}";
                if (!_aliases.TryGetValue(navigationPath, out var targetAlias))
                {
                    targetAlias = $"t{_nextAlias++}";
                    AddJoin(alias, targetAlias, currentEntityType, navigation);
                    _aliases.Add(navigationPath, targetAlias);
                }

                alias = targetAlias;
                currentEntityType = navigation.TargetEntityType;
            }

            return $"{QuoteIdentifier(alias)}.{QuoteIdentifier(resolver.GetColumn(path.Property, path.PropertyEntityType))}";
        }

        public string AddParameter(object? value)
        {
            var name = $"@p{_nextParameter++}";
            _parameters.Add((name, value));
            return name;
        }

        private void AddJoin(string sourceAlias, string targetAlias, IEntityType sourceEntityType, INavigation navigation)
        {
            var targetEntityType = navigation.TargetEntityType;
            var foreignKey = navigation.ForeignKey;
            var joinConditions = navigation.IsOnDependent
                ? foreignKey.Properties.Select((property, index) =>
                    $"{QuoteIdentifier(sourceAlias)}.{QuoteIdentifier(resolver.GetColumn(property, sourceEntityType))} = " +
                    $"{QuoteIdentifier(targetAlias)}.{QuoteIdentifier(resolver.GetColumn(foreignKey.PrincipalKey.Properties[index], targetEntityType))}")
                : foreignKey.PrincipalKey.Properties.Select((property, index) =>
                    $"{QuoteIdentifier(sourceAlias)}.{QuoteIdentifier(resolver.GetColumn(property, sourceEntityType))} = " +
                    $"{QuoteIdentifier(targetAlias)}.{QuoteIdentifier(resolver.GetColumn(foreignKey.Properties[index], targetEntityType))}");

            var (table, schema) = resolver.GetTable(targetEntityType);
            _joins.Add($" LEFT JOIN {QuoteTable(schema, table)} AS {QuoteIdentifier(targetAlias)} ON {string.Join(" AND ", joinConditions)}");
        }
    }
}
