namespace Features.Application.DataAccess;

public sealed class EntityNotAllowedException(string message) : DataEngineValidationException(message);
