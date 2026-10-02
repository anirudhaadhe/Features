using Features.Application.DataAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Features.Infrastructure.DataAccess;

internal sealed class MetadataCommandExecutor(DbContext dbContext, IEntityMetadataResolver resolver)
{
    private readonly MetadataCommandPreparer _preparer = new(resolver);

    public async Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var operations = _preparer.Prepare(request);
        var results = Flatten(operations).ToDictionary(
            operation => operation.Path,
            operation => new CommandOperationResult
            {
                Path = operation.Path,
                Entity = operation.EntityType.ClrType.Name,
                OperationType = operation.Operation.OperationType,
                ClientRef = operation.Operation.ClientRef,
                Status = CommandOperationStatus.NotRun
            },
            StringComparer.Ordinal);
        var aliases = new Dictionary<string, EntityEntry>(StringComparer.Ordinal);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var operation in operations)
            {
                await ExecuteOperationAsync(operation, aliases, results, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new CommandResult
            {
                Succeeded = true,
                Operations = OrderResults(operations, results)
            };
        }
        catch (OperationCanceledException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
        catch (Exception exception)
        {
            string? rollbackError = null;
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                rollbackError = rollbackException.Message;
            }

            var failedPath = exception is CommandOperationExecutionException operationException
                ? operationException.Path
                : null;
            var error = exception is CommandOperationExecutionException wrapped
                ? wrapped.InnerException?.Message ?? wrapped.Message
                : exception.Message;
            if (rollbackError is not null)
            {
                error = $"{error} Rollback also failed: {rollbackError}";
            }

            foreach (var (path, result) in results.ToArray())
            {
                results[path] = result.Status switch
                {
                    CommandOperationStatus.Succeeded => CopyResult(
                        result,
                        CommandOperationStatus.RolledBack,
                        "Rolled back because another operation in the command failed."),
                    _ when path == failedPath => CopyResult(result, CommandOperationStatus.Failed, error),
                    _ => result
                };
            }

            return new CommandResult
            {
                Succeeded = false,
                Error = failedPath is null ? error : $"{failedPath}: {error}",
                Operations = OrderResults(operations, results)
            };
        }
    }

    private async Task ExecuteOperationAsync(
        PreparedCommandOperation operation,
        Dictionary<string, EntityEntry> aliases,
        Dictionary<string, CommandOperationResult> results,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (operation.Operation.OperationType)
            {
                case CommandOperationType.Insert:
                    await InsertAsync(operation, aliases, results, cancellationToken);
                    break;
                case CommandOperationType.Update:
                    await UpdateAsync(operation, results, cancellationToken);
                    break;
                case CommandOperationType.Delete:
                    await DeleteAsync(operation, aliases, results, cancellationToken);
                    break;
                default:
                    throw new DataEngineValidationException(
                        $"Operation type at '{operation.Path}' is not supported.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not CommandOperationExecutionException)
        {
            throw new CommandOperationExecutionException(operation.Path, exception);
        }
    }

    private async Task InsertAsync(
        PreparedCommandOperation operation,
        Dictionary<string, EntityEntry> aliases,
        Dictionary<string, CommandOperationResult> results,
        CancellationToken cancellationToken)
    {
        var entity = CreateEntity(operation.EntityType);
        var entry = dbContext.Entry(entity);
        ApplyValues(entry, operation.Data);

        if (operation.ParentForeignKey is not null)
        {
            if (operation.Operation.ParentRef is null || !aliases.TryGetValue(operation.Operation.ParentRef, out var parentEntry))
            {
                throw new DataEngineValidationException(
                    $"Parent reference '{operation.Operation.ParentRef}' at '{operation.Path}' is not available in this command.");
            }

            var foreignKey = operation.ParentForeignKey;
            var principalProperty = foreignKey.PrincipalKey.Properties[0];
            var foreignKeyProperty = foreignKey.Properties[0];
            entry.Property(foreignKeyProperty.Name).CurrentValue = parentEntry.Property(principalProperty.Name).CurrentValue;
        }

        dbContext.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        results[operation.Path] = SucceededResult(operation, entry);

        if (operation.Operation.ClientRef is not null)
        {
            aliases.Add(operation.Operation.ClientRef, entry);
        }

        foreach (var child in operation.Children)
        {
            await ExecuteOperationAsync(child, aliases, results, cancellationToken);
        }
    }

    private async Task UpdateAsync(
        PreparedCommandOperation operation,
        Dictionary<string, CommandOperationResult> results,
        CancellationToken cancellationToken)
    {
        var primaryKey = operation.EntityType.FindPrimaryKey()!;
        var keyValues = primaryKey.Properties
            .Select(property => operation.KeyValues[property])
            .ToArray();
        var entity = await dbContext.FindAsync(
            operation.EntityType.ClrType,
            keyValues,
            cancellationToken);
        if (entity is null)
        {
            throw new DataEngineValidationException(
                $"Entity '{operation.EntityType.ClrType.Name}' at '{operation.Path}' was not found.");
        }

        var entry = dbContext.Entry(entity);
        ApplyValues(entry, operation.Data);
        foreach (var property in operation.Data.Keys)
        {
            entry.Property(property.Name).IsModified = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        results[operation.Path] = SucceededResult(operation, entry);
    }

    private async Task DeleteAsync(
        PreparedCommandOperation operation,
        Dictionary<string, EntityEntry> aliases,
        Dictionary<string, CommandOperationResult> results,
        CancellationToken cancellationToken)
    {
        foreach (var child in operation.Children)
        {
            await ExecuteOperationAsync(child, aliases, results, cancellationToken);
        }

        object entity;
        if (operation.EntityType.GetProperties().Any(property => property.IsConcurrencyToken))
        {
            var primaryKey = operation.EntityType.FindPrimaryKey()!;
            var keyValues = primaryKey.Properties
                .Select(property => operation.KeyValues[property])
                .ToArray();
            entity = await dbContext.FindAsync(operation.EntityType.ClrType, keyValues, cancellationToken)
                ?? throw new DataEngineValidationException(
                    $"Entity '{operation.EntityType.ClrType.Name}' at '{operation.Path}' was not found.");
        }
        else
        {
            entity = CreateEntity(operation.EntityType);
            ApplyValues(dbContext.Entry(entity), operation.KeyValues);
        }

        var entry = dbContext.Entry(entity);
        dbContext.Remove(entity);
        var result = SucceededResult(operation, entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        results[operation.Path] = result;
    }

    private static object CreateEntity(IEntityType entityType) =>
        Activator.CreateInstance(entityType.ClrType, nonPublic: true)
        ?? throw new DataEngineValidationException(
            $"Entity '{entityType.ClrType.Name}' cannot be created by the command engine.");

    private static void ApplyValues(EntityEntry entry, IReadOnlyDictionary<IProperty, object?> values)
    {
        foreach (var (property, value) in values)
        {
            entry.Property(property.Name).CurrentValue = value;
        }
    }

    private static CommandOperationResult SucceededResult(PreparedCommandOperation operation, EntityEntry entry)
    {
        var generatedKeys = operation.EntityType.FindPrimaryKey()!.Properties.ToDictionary(
            property => property.Name,
            property => entry.Property(property.Name).CurrentValue,
            StringComparer.Ordinal);

        return new CommandOperationResult
        {
            Path = operation.Path,
            Entity = operation.EntityType.ClrType.Name,
            OperationType = operation.Operation.OperationType,
            ClientRef = operation.Operation.ClientRef,
            Status = CommandOperationStatus.Succeeded,
            GeneratedKeys = generatedKeys
        };
    }

    private static CommandOperationResult CopyResult(
        CommandOperationResult result,
        CommandOperationStatus status,
        string? error) =>
        new()
        {
            Path = result.Path,
            Entity = result.Entity,
            OperationType = result.OperationType,
            ClientRef = result.ClientRef,
            Status = status,
            Error = error,
            GeneratedKeys = result.GeneratedKeys
        };

    private static IEnumerable<PreparedCommandOperation> Flatten(
        IEnumerable<PreparedCommandOperation> operations)
    {
        foreach (var operation in operations)
        {
            yield return operation;
            foreach (var child in Flatten(operation.Children))
            {
                yield return child;
            }
        }
    }

    private static List<CommandOperationResult> OrderResults(
        IEnumerable<PreparedCommandOperation> operations,
        IReadOnlyDictionary<string, CommandOperationResult> results) =>
        Flatten(operations).Select(operation => results[operation.Path]).ToList();

    private sealed class CommandOperationExecutionException(string path, Exception innerException)
        : Exception(innerException.Message, innerException)
    {
        public string Path { get; } = path;
    }
}
