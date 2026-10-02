using Features.Domain.Events;

namespace Features.Domain.Products;

public sealed record ProductSampleDataSeededEvent(int RecordCount) : DomainEvent;
