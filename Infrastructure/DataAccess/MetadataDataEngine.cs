using Features.Application.DataAccess;
using Microsoft.EntityFrameworkCore;

namespace Features.Infrastructure.DataAccess;

internal sealed class MetadataDataEngine : IMetadataDataEngine, ICommandService
{
    private readonly MetadataQueryExecutor _queryExecutor;
    private readonly MetadataCommandExecutor _commandExecutor;

    public MetadataDataEngine(DbContext dbContext, IEntityMetadataResolver resolver)
    {
        _queryExecutor = new MetadataQueryExecutor(dbContext, resolver);
        _commandExecutor = new MetadataCommandExecutor(dbContext, resolver);
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteQueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default) =>
        _queryExecutor.ExecuteAsync(request, cancellationToken);

    public Task<CommandResult> ExecuteCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default) =>
        _commandExecutor.ExecuteAsync(request, cancellationToken);

    public Task<CommandResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default) =>
        _commandExecutor.ExecuteAsync(request, cancellationToken);
}
