using System.Text.Json;
using System.Text.Json.Serialization;
using Features.Application.DataAccess;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Features.Infrastructure.DataAccess;

internal sealed class MetadataCommandPreparer(IEntityMetadataResolver resolver)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public IReadOnlyList<PreparedCommandOperation> Prepare(CommandRequest request)
    {
        if (request.Operations is null || request.Operations.Count == 0)
        {
            throw new DataEngineValidationException("At least one command operation is required.");
        }

        var clientReferences = new HashSet<string>(StringComparer.Ordinal);
        return request.Operations
            .Select((operation, index) => Prepare(operation, null, $"Operations[{index}]", clientReferences))
            .ToArray();
    }

    private PreparedCommandOperation Prepare(
        CommandOperation operation,
        PreparedCommandOperation? parent,
        string path,
        HashSet<string> clientReferences)
    {
        if (operation is null)
        {
            throw new DataEngineValidationException($"Command operation '{path}' cannot be null.");
        }

        if (!Enum.IsDefined(operation.OperationType))
        {
            throw new DataEngineValidationException($"Operation type at '{path}' is not supported.");
        }

        var entityType = resolver.ResolveEntityType(operation.Entity);
        resolver.EnsureWritable(entityType);
        ValidateConstructor(entityType, path);

        if (operation.ClientRef is not null &&
            (operation.OperationType != CommandOperationType.Insert ||
             string.IsNullOrWhiteSpace(operation.ClientRef) ||
             !clientReferences.Add(operation.ClientRef)))
        {
            throw new DataEngineValidationException(
                $"Client references at '{path}' must be unique, non-empty, and used only for inserts.");
        }

        IForeignKey? parentForeignKey = null;
        if (parent is null)
        {
            if (operation.ParentRef is not null || operation.ForeignKeyProperty is not null)
            {
                throw new DataEngineValidationException(
                    $"Top-level operation '{path}' cannot specify parentRef or foreignKeyProperty.");
            }
        }
        else if (parent.Operation.OperationType == CommandOperationType.Insert)
        {
            if (operation.OperationType != CommandOperationType.Insert ||
                string.IsNullOrWhiteSpace(parent.Operation.ClientRef) ||
                !string.Equals(operation.ParentRef, parent.Operation.ClientRef, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(operation.ForeignKeyProperty))
            {
                throw new DataEngineValidationException(
                    $"Nested insert '{path}' must reference its parent's clientRef and specify foreignKeyProperty.");
            }

            parentForeignKey = resolver.ResolveForeignKey(entityType, parent.EntityType, operation.ForeignKeyProperty);
            if (parentForeignKey.Properties.Count != 1 || parentForeignKey.PrincipalKey.Properties.Count != 1 ||
                parentForeignKey.PrincipalKey != parent.EntityType.FindPrimaryKey())
            {
                throw new DataEngineValidationException(
                    $"Nested insert '{path}' requires a single-column foreign key to its parent's primary key.");
            }
        }
        else if (parent.Operation.OperationType == CommandOperationType.Delete)
        {
            if (operation.OperationType != CommandOperationType.Delete ||
                operation.ParentRef is not null || operation.ForeignKeyProperty is not null)
            {
                throw new DataEngineValidationException(
                    $"Nested delete '{path}' must also be a delete and cannot specify insert-only parent references.");
            }
        }
        else
        {
            throw new DataEngineValidationException(
                $"Nested operations are supported only under inserts or deletes ('{path}').");
        }

        var data = ReadData(operation.Data, entityType, operation.OperationType, path);
        var keyValues = ReadKeyValues(operation.KeyValues, entityType, operation.OperationType, path);
        if (parentForeignKey is not null && data.ContainsKey(parentForeignKey.Properties[0]))
        {
            throw new DataEngineValidationException(
                $"Child data at '{path}' cannot supply foreign-key property '{parentForeignKey.Properties[0].Name}'.");
        }

        if (operation.OperationType == CommandOperationType.Update && data.Count == 0)
        {
            throw new DataEngineValidationException($"Update '{path}' must include at least one data property.");
        }

        if (operation.OperationType == CommandOperationType.Update && operation.Children is { Count: > 0 })
        {
            throw new DataEngineValidationException($"Update '{path}' cannot contain nested operations.");
        }

        var placeholder = new PreparedCommandOperation(
            operation,
            entityType,
            data,
            keyValues,
            parentForeignKey,
            path,
            []);
        var children = operation.Children?
            .Select((child, index) => Prepare(
                child,
                placeholder,
                $"{path}.Children[{index}]",
                clientReferences))
            .ToArray() ?? [];

        if (children.Length > 0 && operation.OperationType == CommandOperationType.Insert &&
            entityType.FindPrimaryKey()?.Properties.Count != 1)
        {
            throw new DataEngineValidationException(
                $"Parent insert '{path}' with children must have a single-column primary key.");
        }

        return placeholder with { Children = children };
    }

    private IReadOnlyDictionary<IProperty, object?> ReadData(
        Dictionary<string, JsonElement> data,
        IEntityType entityType,
        CommandOperationType operationType,
        string path)
    {
        if (data is null)
        {
            throw new DataEngineValidationException($"Data at '{path}' cannot be null.");
        }

        if (operationType == CommandOperationType.Delete)
        {
            if (data.Count != 0)
            {
                throw new DataEngineValidationException($"Delete '{path}' cannot include data.");
            }

            return new Dictionary<IProperty, object?>();
        }

        var values = new Dictionary<IProperty, object?>();
        foreach (var (name, jsonValue) in data)
        {
            var property = resolver.ResolveProperty(entityType, name);
            if (operationType == CommandOperationType.Update && property.IsPrimaryKey())
            {
                throw new DataEngineValidationException(
                    $"Primary key '{property.Name}' at '{path}' must be supplied through keyValues.");
            }

            if (operationType == CommandOperationType.Update && property.IsConcurrencyToken)
            {
                throw new DataEngineValidationException(
                    $"Concurrency token '{property.Name}' cannot be changed by update '{path}'.");
            }

            values.Add(property, DeserializeValue(jsonValue, property, path));
        }

        return values;
    }

    private IReadOnlyDictionary<IProperty, object?> ReadKeyValues(
        Dictionary<string, JsonElement> keyValues,
        IEntityType entityType,
        CommandOperationType operationType,
        string path)
    {
        if (keyValues is null)
        {
            throw new DataEngineValidationException($"keyValues at '{path}' cannot be null.");
        }

        if (operationType == CommandOperationType.Insert)
        {
            if (keyValues.Count != 0)
            {
                throw new DataEngineValidationException($"Insert '{path}' cannot include keyValues; use data.");
            }

            return new Dictionary<IProperty, object?>();
        }

        var primaryKey = entityType.FindPrimaryKey()!;
        var values = new Dictionary<IProperty, object?>();
        foreach (var (name, jsonValue) in keyValues)
        {
            var property = resolver.ResolveProperty(entityType, name);
            if (!primaryKey.Properties.Contains(property))
            {
                throw new DataEngineValidationException($"'{name}' at '{path}' is not a primary key property.");
            }

            values.Add(property, DeserializeValue(jsonValue, property, path));
        }

        if (values.Count != primaryKey.Properties.Count || primaryKey.Properties.Any(property => !values.ContainsKey(property)))
        {
            throw new DataEngineValidationException($"keyValues at '{path}' must provide the complete primary key.");
        }

        return values;
    }

    private static object? DeserializeValue(JsonElement value, IProperty property, string path)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!property.IsNullable)
            {
                throw new DataEngineValidationException(
                    $"Property '{property.Name}' at '{path}' does not allow null values.");
            }

            return null;
        }

        try
        {
            return value.Deserialize(property.ClrType, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new DataEngineValidationException(
                $"Value for property '{property.Name}' at '{path}' cannot be converted to '{property.ClrType.Name}': {exception.Message}");
        }
    }

    private static void ValidateConstructor(IEntityType entityType, string path)
    {
        var clrType = entityType.ClrType;
        if (clrType.IsAbstract || clrType.IsInterface ||
            clrType.GetConstructor(System.Reflection.BindingFlags.Instance |
                                   System.Reflection.BindingFlags.Public |
                                   System.Reflection.BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null) is null)
        {
            throw new DataEngineValidationException(
                $"Entity '{clrType.Name}' at '{path}' cannot be created by the command engine.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
