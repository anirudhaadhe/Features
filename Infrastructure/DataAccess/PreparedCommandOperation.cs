using Features.Application.DataAccess;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Features.Infrastructure.DataAccess;

internal sealed record PreparedCommandOperation(
    CommandOperation Operation,
    IEntityType EntityType,
    IReadOnlyDictionary<IProperty, object?> Data,
    IReadOnlyDictionary<IProperty, object?> KeyValues,
    IForeignKey? ParentForeignKey,
    string Path,
    IReadOnlyList<PreparedCommandOperation> Children);
