using Microsoft.EntityFrameworkCore.Metadata;

namespace Features.Infrastructure.DataAccess;

public interface IEntityMetadataResolver
{
    IEntityType ResolveEntityType(string entityName);

    IProperty ResolveProperty(string entityName, string propertyName);

    IProperty ResolveProperty(IEntityType entityType, string propertyName);

    INavigation ResolveNavigation(string entityName, string navPath);

    ResolvedScalarPath ResolveScalarPath(string entityName, string path);

    IForeignKey ResolveForeignKey(IEntityType childEntityType, IEntityType parentEntityType, string propertyName);

    void EnsureWritable(IEntityType entityType);

    (string Table, string? Schema) GetTable(IEntityType entityType);

    string GetColumn(IProperty property, IEntityType entityType);
}

public sealed record ResolvedScalarPath(
    IEntityType RootEntityType,
    IReadOnlyList<INavigation> Navigations,
    IProperty Property)
{
    public IEntityType PropertyEntityType => Navigations.Count == 0
        ? RootEntityType
        : Navigations[^1].TargetEntityType;
}
