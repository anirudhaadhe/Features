namespace Features.Application.DataAccess;

public interface ICommandService
{
    Task<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default);
}
