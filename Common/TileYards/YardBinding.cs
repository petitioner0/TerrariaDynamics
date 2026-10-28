using System;

namespace TerrariaDynamics.Common.TileYards;

internal interface IYardRegistry
{
    int WorldRevision { get; }
    bool TryGet(Guid id, out Yard yard);
}

// Persistent identity plus a runtime reference. No event subscription holds a
// discarded item alive. Missing IDs are looked up only once per world revision.
internal sealed class YardBinding
{
    private Guid id;
    private Yard yard;
    private IYardRegistry registry;
    private int revision = -1;

    public YardBinding(Guid id = default) => this.id = id;

    public Guid Id
    {
        get {
            if (yard?.State == YardState.Removed) {
                id = Guid.Empty;
                yard = null;
            }
            return id;
        }
    }

    public void Bind(Yard value, IYardRegistry owner)
    {
        id = value.Id;
        yard = value;
        registry = owner;
        revision = owner.WorldRevision;
    }

    public Yard Resolve(IYardRegistry owner)
    {
        if (Id == Guid.Empty)
            return null;
        if (!ReferenceEquals(registry, owner) || revision != owner.WorldRevision) {
            registry = owner;
            revision = owner.WorldRevision;
            owner.TryGet(id, out yard);
        }
        return yard?.State is YardState.Removed or YardState.Unloaded ? null : yard;
    }

    // ModItem clones must not share mutable binding state.
    public YardBinding Clone() => new(Id) {
        yard = yard, registry = registry, revision = revision
    };
}
