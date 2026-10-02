using Features.Application.DataAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;

namespace Features.Infrastructure.DataAccess;

internal sealed class EntityMetadataResolver : IEntityMetadataResolver
{
    private readonly Dictionary<string, IEntityType> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IEntityType, EntityMetadata> _metadata = [];

    public EntityMetadataResolver(IModel model, IOptions<MetadataDataAccessOptions> options)
    {
        var modelEntities = model.GetEntityTypes().ToArray();
        var allowedTypes = ResolveAllowList(modelEntities, options.Value.ExposedEntities);
        var exposedEntities = modelEntities
            .Where(entityType =>
                Attribute.IsDefined(entityType.ClrType, typeof(LowCodeExposedAttribute), inherit: false) ||
                allowedTypes.Contains(entityType))
            .ToArray();

        foreach (var entityType in exposedEntities)
        {
            var properties = entityType.GetProperties().ToDictionary(
                property => property.Name,
                StringComparer.OrdinalIgnoreCase);
            var navigations = entityType.GetNavigations().ToDictionary(
                navigation => navigation.Name,
                StringComparer.OrdinalIgnoreCase);
            _metadata.Add(entityType, new EntityMetadata(properties, navigations));
        }

        foreach (var entityType in exposedEntities)
        {
            AddEntityAlias(entityType.Name, entityType);
            AddEntityAlias(entityType.ClrType.Name, entityType);
            AddEntityAlias(entityType.ClrType.FullName, entityType);
        }
    }

    public IEntityType ResolveEntityType(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            throw new EntityNotAllowedException("An entity name is required.");
        }

        return _entities.TryGetValue(entityName, out var entityType)
            ? entityType
            : throw new EntityNotAllowedException($"Entity '{entityName}' is not exposed to low-code access.");
    }

    public IProperty ResolveProperty(string entityName, string propertyName)
    {
        var entityType = ResolveEntityType(entityName);
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new PropertyNotAllowedException("A property name is required.");
        }

        return GetProperty(entityType, propertyName);
    }

    public IProperty ResolveProperty(IEntityType entityType, string propertyName)
    {
        EnsureExposed(entityType);
        return GetProperty(entityType, propertyName);
    }

    public INavigation ResolveNavigation(string entityName, string navPath)
    {
        var entityType = ResolveEntityType(entityName);
        if (string.IsNullOrWhiteSpace(navPath))
        {
            throw new PropertyNotAllowedException("A navigation path is required.");
        }

        var segments = navPath.Split('.', StringSplitOptions.None);
        if (segments.Length is < 1 or > 2 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new PropertyNotAllowedException($"Navigation path '{navPath}' is invalid.");
        }

        var navigation = GetNavigation(entityType, segments[0]);
        EnsureExposed(navigation.TargetEntityType);
        if (navigation.IsCollection)
        {
            throw new PropertyNotAllowedException($"Collection navigation '{segments[0]}' is not supported.");
        }

        if (segments.Length == 2)
        {
            GetProperty(navigation.TargetEntityType, segments[1]);
        }

        return navigation;
    }

    public ResolvedScalarPath ResolveScalarPath(string entityName, string path)
    {
        var rootEntityType = ResolveEntityType(entityName);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new PropertyNotAllowedException("A field path is required.");
        }

        var segments = path.Split('.', StringSplitOptions.None);
        if (segments.Any(string.IsNullOrWhiteSpace) || segments.Length > 2)
        {
            throw new PropertyNotAllowedException($"Field path '{path}' is invalid.");
        }

        if (segments.Length == 1)
        {
            return new ResolvedScalarPath(rootEntityType, [], GetProperty(rootEntityType, segments[0]));
        }

        var navigation = ResolveNavigation(entityName, path);
        var property = GetProperty(navigation.TargetEntityType, segments[1]);
        return new ResolvedScalarPath(rootEntityType, [navigation], property);
    }

    public IForeignKey ResolveForeignKey(IEntityType childEntityType, IEntityType parentEntityType, string propertyName)
    {
        EnsureExposed(childEntityType);
        EnsureExposed(parentEntityType);
        var property = GetProperty(childEntityType, propertyName);
        var matchingForeignKeys = childEntityType.GetForeignKeys()
            .Where(foreignKey =>
                foreignKey.PrincipalEntityType == parentEntityType &&
                foreignKey.Properties.Contains(property))
            .ToArray();

        if (matchingForeignKeys.Length != 1)
        {
            throw new PropertyNotAllowedException(
                $"Property '{propertyName}' is not a unique foreign key from '{childEntityType.ClrType.Name}' to '{parentEntityType.ClrType.Name}'.");
        }

        return matchingForeignKeys[0];
    }

    public (string Table, string? Schema) GetTable(IEntityType entityType)
    {
        var table = EnsureTableMapping(entityType);
        return (table, entityType.GetSchema());
    }

    public string GetColumn(IProperty property, IEntityType entityType)
    {
        EnsureExposed(entityType);
        if (!_metadata[entityType].Properties.TryGetValue(property.Name, out var mappedProperty) || mappedProperty != property)
        {
            throw new PropertyNotAllowedException(
                $"Property '{property.Name}' is not exposed on entity '{entityType.ClrType.Name}'.");
        }

        var (table, schema) = GetTable(entityType);
        var storeObject = StoreObjectIdentifier.Table(table, schema);
        return property.GetColumnName(storeObject)
            ?? throw new DataEngineValidationException(
                $"Property '{property.Name}' has no column mapping in table '{table}'.");
    }

    public void EnsureWritable(IEntityType entityType)
    {
        EnsureExposed(entityType);
        EnsureTableMapping(entityType);
        if (entityType.IsOwned())
        {
            throw new DataEngineValidationException(
                $"Owned entity '{entityType.ClrType.Name}' cannot be modified as a standalone operation.");
        }

        if (entityType.FindPrimaryKey() is null)
        {
            throw new DataEngineValidationException(
                $"Entity '{entityType.ClrType.Name}' has no primary key and cannot be modified.");
        }

        if (entityType.GetDerivedTypes().Any())
        {
            throw new DataEngineValidationException(
                $"Writes to inheritance hierarchies are not supported for '{entityType.ClrType.Name}'.");
        }
    }

    private string EnsureTableMapping(IEntityType entityType)
    {
        EnsureExposed(entityType);
        return entityType.GetTableName()
            ?? throw new DataEngineValidationException(
                $"Entity '{entityType.ClrType.Name}' is not mapped to a relational table.");
    }

    private IProperty GetProperty(IEntityType entityType, string propertyName)
    {
        EnsureExposed(entityType);
        if (string.IsNullOrWhiteSpace(propertyName) || propertyName.Contains('.', StringComparison.Ordinal) ||
            !_metadata[entityType].Properties.TryGetValue(propertyName, out var property))
        {
            throw new PropertyNotAllowedException(
                $"Property '{propertyName}' is not exposed on entity '{entityType.ClrType.Name}'.");
        }

        return property;
    }

    private INavigation GetNavigation(IEntityType entityType, string navigationName)
    {
        EnsureExposed(entityType);
        if (string.IsNullOrWhiteSpace(navigationName) ||
            !_metadata[entityType].Navigations.TryGetValue(navigationName, out var navigation))
        {
            throw new PropertyNotAllowedException(
                $"Navigation '{navigationName}' is not exposed on entity '{entityType.ClrType.Name}'.");
        }

        return navigation;
    }

    private void EnsureExposed(IEntityType entityType)
    {
        if (!_metadata.ContainsKey(entityType))
        {
            throw new EntityNotAllowedException(
                $"Entity '{entityType.ClrType.Name}' is not exposed to low-code access.");
        }
    }

    private void AddEntityAlias(string? alias, IEntityType entityType)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        if (_entities.TryGetValue(alias, out var existing) && existing != entityType)
        {
            throw new InvalidOperationException($"Exposed entity alias '{alias}' is ambiguous.");
        }

        _entities[alias] = entityType;
    }

    private static HashSet<IEntityType> ResolveAllowList(
        IReadOnlyList<IEntityType> modelEntities,
        IReadOnlyCollection<string>? names)
    {
        var allowedTypes = new HashSet<IEntityType>();
        if (names is null)
        {
            return allowedTypes;
        }

        foreach (var name in names)
        {
            var matches = modelEntities.Where(entityType => MatchesEntityName(entityType, name)).ToArray();
            if (string.IsNullOrWhiteSpace(name) || matches.Length != 1)
            {
                throw new InvalidOperationException(
                    matches.Length == 0
                        ? $"Configured low-code entity '{name}' is not present in the EF model."
                        : $"Configured low-code entity name '{name}' is ambiguous in the EF model.");
            }

            allowedTypes.Add(matches[0]);
        }

        return allowedTypes;
    }

    private static bool MatchesEntityName(IEntityType entityType, string name) =>
        string.Equals(entityType.Name, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(entityType.ClrType.Name, name, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(entityType.ClrType.FullName, name, StringComparison.OrdinalIgnoreCase);

    private sealed record EntityMetadata(
        Dictionary<string, IProperty> Properties,
        Dictionary<string, INavigation> Navigations);
}
