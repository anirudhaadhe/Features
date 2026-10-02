namespace Features.Application.DataAccess;

public sealed class PropertyNotAllowedException(string message) : DataEngineValidationException(message);
