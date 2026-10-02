namespace Features.Infrastructure.DataAccess;

public sealed class MetadataDataAccessOptions
{
    public const string SectionName = "MetadataDataAccess";

    public List<string> ExposedEntities { get; set; } = [];
}
