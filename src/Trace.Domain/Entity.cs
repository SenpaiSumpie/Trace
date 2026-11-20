namespace Trace.Domain;

public abstract class Entity
{
    public Guid Id { get; private init; } = Guid.CreateVersion7();

    /// <summary>UTC. Stamped by the persistence layer when the entity is first saved.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>UTC. Stamped by the persistence layer on every save that changes the entity.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }
}
