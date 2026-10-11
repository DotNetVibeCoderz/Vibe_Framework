namespace Rustzor.Game;

/// <summary>An entity: an id plus a generation, so a stale reference to a destroyed entity is detectable.</summary>
public readonly record struct Entity(int Id, int Generation)
{
    public static readonly Entity None = new(-1, 0);
    public bool IsNone => Id < 0;
}

internal interface IComponentStore
{
    bool Has(int id);
    void Remove(int id);
    int Count { get; }
}

/// <summary>
/// Components of one type in a sparse set: a dense array of values and the
/// entity owning each, so iterating a component type touches only entities
/// that have it, in contiguous memory.
/// </summary>
public sealed class ComponentStore<T> : IComponentStore
{
    private int[] _sparse = new int[64];
    private int[] _owners = new int[16];
    private T[] _values = new T[16];

    public ComponentStore() => Array.Fill(_sparse, -1);

    public int Count { get; private set; }

    public bool Has(int id) => id < _sparse.Length && _sparse[id] >= 0;

    public void Set(int id, T value)
    {
        EnsureSparse(id);
        var slot = _sparse[id];
        if (slot >= 0)
        {
            _values[slot] = value;
            return;
        }
        if (Count == _values.Length)
        {
            Array.Resize(ref _values, Count * 2);
            Array.Resize(ref _owners, Count * 2);
        }
        _values[Count] = value;
        _owners[Count] = id;
        _sparse[id] = Count;
        Count++;
    }

    public T Get(int id) => _values[_sparse[id]];

    public bool TryGet(int id, out T value)
    {
        if (Has(id))
        {
            value = _values[_sparse[id]];
            return true;
        }
        value = default!;
        return false;
    }

    public void Remove(int id)
    {
        if (!Has(id)) return;
        var slot = _sparse[id];
        var last = Count - 1;
        _values[slot] = _values[last];
        _owners[slot] = _owners[last];
        _sparse[_owners[slot]] = slot;
        _values[last] = default!;
        _sparse[id] = -1;
        Count--;
    }

    /// <summary>The owner of the component at a dense index, for iteration.</summary>
    public int OwnerAt(int index) => _owners[index];
    public T ValueAt(int index) => _values[index];
    public void SetAt(int index, T value) => _values[index] = value;

    private void EnsureSparse(int id)
    {
        if (id < _sparse.Length) return;
        var old = _sparse.Length;
        var size = old;
        while (size <= id) size *= 2;
        Array.Resize(ref _sparse, size);
        for (var i = old; i < size; i++) _sparse[i] = -1;
    }
}

/// <summary>A system runs every fixed step over the world's components.</summary>
public interface ISystem
{
    void Update(World world, double dt);
}

/// <summary>
/// The entity-component-system world. Components are plain values (structs or
/// classes); systems query them by type.
/// <code>
/// var e = world.Spawn().With(new Position(10, 20)).With(new Velocity(1, 0));
/// world.Each&lt;Position, Velocity&gt;((id, p, v) =&gt; world.Set(id, new Position(p.X + v.X, p.Y + v.Y)));
/// </code>
/// </summary>
public sealed class World
{
    private readonly List<int> _generations = new();
    private readonly Stack<int> _free = new();
    private readonly Dictionary<Type, IComponentStore> _stores = new();
    private readonly List<ISystem> _systems = new();
    private readonly List<Entity> _doomed = new();

    public int EntityCount { get; private set; }

    /// <summary>Shared state for systems: score, level, settings.</summary>
    public Dictionary<string, object?> Resources { get; } = new();

    public EntityBuilder Spawn()
    {
        int id;
        if (_free.Count > 0) id = _free.Pop();
        else
        {
            id = _generations.Count;
            _generations.Add(0);
        }
        EntityCount++;
        return new EntityBuilder(this, new Entity(id, _generations[id]));
    }

    public bool IsAlive(Entity e) => e.Id >= 0 && e.Id < _generations.Count && _generations[e.Id] == e.Generation;

    /// <summary>The live entity with this id.</summary>
    public Entity EntityOf(int id) => new(id, _generations[id]);

    /// <summary>Destroys at the end of the current step, so systems can destroy while iterating.</summary>
    public void Destroy(Entity e)
    {
        if (IsAlive(e)) _doomed.Add(e);
    }

    public void DestroyNow(Entity e)
    {
        if (!IsAlive(e)) return;
        foreach (var store in _stores.Values) store.Remove(e.Id);
        _generations[e.Id]++;
        _free.Push(e.Id);
        EntityCount--;
    }

    public ComponentStore<T> Store<T>()
    {
        if (_stores.TryGetValue(typeof(T), out var s)) return (ComponentStore<T>)s;
        var created = new ComponentStore<T>();
        _stores[typeof(T)] = created;
        return created;
    }

    public void Set<T>(int id, T value) => Store<T>().Set(id, value);
    public void Set<T>(Entity e, T value) => Store<T>().Set(e.Id, value);
    public T Get<T>(Entity e) => Store<T>().Get(e.Id);
    public T Get<T>(int id) => Store<T>().Get(id);
    public bool Has<T>(Entity e) => Store<T>().Has(e.Id);
    public bool TryGet<T>(Entity e, out T value) => Store<T>().TryGet(e.Id, out value);
    public void Remove<T>(Entity e) => Store<T>().Remove(e.Id);

    /// <summary>Every entity with a <typeparamref name="T"/>.</summary>
    public void Each<T>(Action<int, T> action)
    {
        var s = Store<T>();
        for (var i = s.Count - 1; i >= 0; i--) action(s.OwnerAt(i), s.ValueAt(i));
    }

    /// <summary>Every entity with both components; iterates the smaller store.</summary>
    public void Each<T1, T2>(Action<int, T1, T2> action)
    {
        var a = Store<T1>();
        var b = Store<T2>();
        if (a.Count <= b.Count)
        {
            for (var i = a.Count - 1; i >= 0; i--)
            {
                var id = a.OwnerAt(i);
                if (b.TryGet(id, out var v2)) action(id, a.ValueAt(i), v2);
            }
        }
        else
        {
            for (var i = b.Count - 1; i >= 0; i--)
            {
                var id = b.OwnerAt(i);
                if (a.TryGet(id, out var v1)) action(id, v1, b.ValueAt(i));
            }
        }
    }

    public void Each<T1, T2, T3>(Action<int, T1, T2, T3> action)
    {
        var c = Store<T3>();
        Each<T1, T2>((id, v1, v2) =>
        {
            if (c.TryGet(id, out var v3)) action(id, v1, v2, v3);
        });
    }

    /// <summary>Ids of the entities with a <typeparamref name="T"/>.</summary>
    public List<int> Query<T>()
    {
        var s = Store<T>();
        var ids = new List<int>(s.Count);
        for (var i = 0; i < s.Count; i++) ids.Add(s.OwnerAt(i));
        return ids;
    }

    public World AddSystem(ISystem system)
    {
        _systems.Add(system);
        return this;
    }

    /// <summary>A system from a lambda.</summary>
    public World AddSystem(Action<World, double> update) => AddSystem(new LambdaSystem(update));

    /// <summary>Runs every system once, then removes entities destroyed during the step.</summary>
    public void Step(double dt)
    {
        foreach (var s in _systems) s.Update(this, dt);
        Flush();
    }

    public void Flush()
    {
        if (_doomed.Count == 0) return;
        foreach (var e in _doomed) DestroyNow(e);
        _doomed.Clear();
    }

    public void Clear()
    {
        for (var id = 0; id < _generations.Count; id++)
            if (!_free.Contains(id)) DestroyNow(EntityOf(id));
        _doomed.Clear();
    }

    private sealed class LambdaSystem(Action<World, double> update) : ISystem
    {
        public void Update(World world, double dt) => update(world, dt);
    }
}

/// <summary>Fluent component assignment for a new entity.</summary>
public readonly struct EntityBuilder
{
    private readonly World _world;

    internal EntityBuilder(World world, Entity entity)
    {
        _world = world;
        Entity = entity;
    }

    public Entity Entity { get; }

    public EntityBuilder With<T>(T component)
    {
        _world.Set(Entity, component);
        return this;
    }

    public static implicit operator Entity(EntityBuilder b) => b.Entity;
}
