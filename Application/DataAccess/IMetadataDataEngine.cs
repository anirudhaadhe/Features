namespace Features.Application.DataAccess;

public interface IMetadataDataEngine
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteQueryAsync(
        QueryRequest request,
        CancellationToken cancellationToken = default);

    Task<CommandResult> ExecuteCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default);
}
