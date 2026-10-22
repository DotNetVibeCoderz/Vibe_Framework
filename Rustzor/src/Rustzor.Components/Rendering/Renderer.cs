using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Rustzor.Components;
using Rustzor.Interop;

namespace Microsoft.AspNetCore.Components;

/// <summary>
/// Renders components to the DOM: runs their render fragments, diffs the new
/// virtual nodes against the last ones, and sends rustzor.js one batch of DOM
/// operations per update.
/// </summary>
internal sealed class Renderer
{
    // DOM operation codes; rustzor.js's `dom.patch` reads the same numbers.
    private const int OpCreateElement = 1;
    private const int OpCreateText = 2;
    private const int OpCreateMarker = 3;
    private const int OpCreateMarkup = 4;
    private const int OpInsert = 5;
    private const int OpRemove = 6;
    private const int OpSetAttribute = 7;
    private const int OpRemoveAttribute = 8;
    private const int OpSetText = 9;
    private const int OpListen = 10;
    private const int OpUnlisten = 11;
    private const int OpMount = 12;

    public static Renderer Current { get; private set; } = null!;

    private readonly ServiceProvider _services;
    private readonly Dictionary<int, ComponentState> _components = new();
    private readonly Dictionary<int, EventCallback> _handlers = new();
    private readonly List<(int ComponentId, RenderFragment Fragment)> _queue = new();
    private readonly List<ComponentState> _renderedThisBatch = new();
    private JsonWriter _ops = new();
    private int _opCount;
    private int _nextDomId = 1;
    private int _nextComponentId = 1;
    private int _nextHandlerId = 1;
    private int _depth;
    private bool _rendering;

    public Renderer(ServiceProvider services)
    {
        _services = services;
        Current = this;
        BeginBatch();
    }

    public ServiceProvider Services => _services;

    /// <summary>Renders <paramref name="componentType"/> into the element matching <paramref name="selector"/>.</summary>
    /// <remarks>
    /// Does not wait for the component's async initialisation: <c>Main</c> has to
    /// return for the browser to get its thread back, and a pending
    /// <c>OnInitializedAsync</c> finishes later, from the event loop, and
    /// re-renders then.
    /// </remarks>
    public void AddRootComponent(Type componentType, string selector, ParameterView parameters)
    {
        var rootId = _nextDomId++;
        Op(OpMount, rootId, selector);
        var state = Instantiate(componentType, null, rootId, 0, null);
        _depth++;
        try
        {
            SupplyParameters(state, parameters.Values);
        }
        finally
        {
            _depth--;
        }
        ProcessQueue();
    }

    // ── the render queue ──────────────────────────────────────────────────

    public void Enqueue(int componentId, RenderFragment fragment)
    {
        _queue.Add((componentId, fragment));
        if (_depth == 0 && !_rendering) ProcessQueue();
    }

    private void ProcessQueue()
    {
        if (_rendering) return;
        _rendering = true;
        try
        {
            // A render can enqueue more renders (a parent passing new parameters
            // to its children), so this drains until nothing is left.
            for (var i = 0; i < _queue.Count; i++)
            {
                var (id, fragment) = _queue[i];
                if (_components.TryGetValue(id, out var state) && !state.Disposed) RenderComponent(state, fragment);
            }
            _queue.Clear();
            Flush();
        }
        finally
        {
            _rendering = false;
        }
        NotifyAfterRender();
    }

    private void RenderComponent(ComponentState state, RenderFragment fragment)
    {
        var builder = new RenderTreeBuilder();
        try
        {
            fragment(builder);
        }
        catch (Exception e)
        {
            Callbacks.UnhandledException?.Invoke(e);
            if (Callbacks.UnhandledException is null) Js.Log(2, "Render failed in " + state.Component.GetType().Name + ": " + Callbacks.Describe(e));
            return;
        }
        var newTree = builder.Roots;
        DiffChildren(state, state.ParentDomId, state.Tree, newTree, state.MarkerId);
        state.Tree = newTree;
        if (!_renderedThisBatch.Contains(state)) _renderedThisBatch.Add(state);
    }

    private void NotifyAfterRender()
    {
        if (_renderedThisBatch.Count == 0) return;
        var rendered = _renderedThisBatch.ToArray();
        _renderedThisBatch.Clear();
        foreach (var state in rendered)
        {
            if (state.Disposed || state.Component is not IHandleAfterRender after) continue;
            try
            {
                var task = after.OnAfterRenderAsync();
                if (!task.IsCompleted) Observe(task);
            }
            catch (Exception e)
            {
                Report(e);
            }
        }
    }

    private static async void Observe(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    private static void Report(Exception e)
    {
        if (Callbacks.UnhandledException is not null) Callbacks.UnhandledException(e);
        else Js.Log(2, "Unhandled exception in a component: " + Callbacks.Describe(e));
    }

    // ── events ───────────────────────────────────────────────────────────

    /// <summary>An event fired in the browser: run its callback, then re-render.</summary>
    public void DispatchEvent(int handlerId, string type, JsonValue payload)
    {
        if (!_handlers.TryGetValue(handlerId, out var callback)) return;
        var args = EventArgsFactory.Create(type, payload);
        _depth++;
        try
        {
            var task = callback.InvokeAsync(args);
            if (!task.IsCompleted) Observe(task);
            else if (task.IsFaulted && task.Exception is not null) Report(task.Exception);
        }
        catch (Exception e)
        {
            Report(e);
        }
        finally
        {
            _depth--;
        }
        ProcessQueue();
    }

    // ── components ───────────────────────────────────────────────────────

    private ComponentState Instantiate(Type type, ComponentState? parent, int parentDomId, int beforeId, VComponent? node)
    {
        var instance = Activator.CreateInstance(type) as IComponent
            ?? throw new InvalidOperationException(type.FullName + " is not a component.");
        var state = new ComponentState(_nextComponentId++, instance, parent)
        {
            ParentDomId = parentDomId,
            MarkerId = _nextDomId++,
        };
        Op(OpCreateMarker, state.MarkerId);
        Op(OpInsert, parentDomId, state.MarkerId, beforeId);
        _components[state.Id] = state;
        _services.InjectProperties(instance);
        instance.Attach(new RenderHandle(this, state.Id));
        if (node is not null)
        {
            node.State = state;
            node.DomId = state.MarkerId;
            node.ReferenceCapture?.Invoke(instance);
        }
        return state;
    }

    private void SupplyParameters(ComponentState state, List<ParameterValue> parameters)
    {
        state.LastParameters = parameters;
        try
        {
            var task = state.Component.SetParametersAsync(WithCascading(state, parameters));
            if (!task.IsCompleted) Observe(task);
            else if (task.IsFaulted && task.Exception is not null) Report(task.Exception);
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    /// <summary>Adds the values that ancestors' <c>CascadingValue</c>s supply.</summary>
    private static ParameterView WithCascading(ComponentState state, List<ParameterValue> parameters)
    {
        var descriptor = ComponentDescriptor.For(state.Component.GetType());
        if (descriptor.Cascading.Count == 0) return new ParameterView(parameters);
        var all = new List<ParameterValue>(parameters);
        foreach (var (property, name) in descriptor.Cascading)
        {
            for (var a = state.Parent; a is not null; a = a.Parent)
            {
                if (a.Component is not ICascadingValueComponent supplier) continue;
                if (!supplier.CanSupply(property.PropertyType, name)) continue;
                all.Add(new ParameterValue(property.Name, supplier.CurrentValue, cascading: true));
                break;
            }
        }
        return new ParameterView(all);
    }

    private void Dispose(ComponentState state)
    {
        if (state.Disposed) return;
        state.Disposed = true;
        foreach (var child in state.Tree) ReleaseTree(child);
        _components.Remove(state.Id);
        try
        {
            if (state.Component is IAsyncDisposable ad)
            {
                var vt = ad.DisposeAsync();
            }
            else if (state.Component is IDisposable d) d.Dispose();
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    // ── diffing ──────────────────────────────────────────────────────────

    private void DiffChildren(ComponentState owner, int parentDomId, List<VNode> oldList, List<VNode> newList, int boundaryId)
    {
        if (oldList.Count == 0)
        {
            foreach (var n in newList) Create(owner, n, parentDomId, boundaryId);
            return;
        }
        if (newList.Count == 0)
        {
            foreach (var o in oldList) Remove(o);
            return;
        }

        // Most renders keep the same nodes in the same order (only attributes and
        // text change): patch them pairwise, with no matching tables to build.
        if (oldList.Count == newList.Count)
        {
            var same = true;
            for (var i = 0; i < newList.Count; i++)
            {
                if (!SameIdentity(oldList[i], newList[i]))
                {
                    same = false;
                    break;
                }
            }
            if (same)
            {
                for (var i = 0; i < newList.Count; i++) Patch(owner, oldList[i], newList[i], parentDomId);
                return;
            }
        }

        // Match old nodes to new by identity: key when there is one, else
        // sequence number, kind and tag. A sequence number repeats in a loop,
        // so equal identities match in order.
        var pool = new Dictionary<string, Queue<int>>();
        for (var i = 0; i < oldList.Count; i++)
        {
            var id = IdentityOf(oldList[i]);
            if (!pool.TryGetValue(id, out var q)) pool[id] = q = new Queue<int>();
            q.Enqueue(i);
        }
        var matchedOld = new int[newList.Count];
        var used = new bool[oldList.Count];
        for (var i = 0; i < newList.Count; i++)
        {
            matchedOld[i] = -1;
            if (pool.TryGetValue(IdentityOf(newList[i]), out var q) && q.Count > 0)
            {
                var o = q.Dequeue();
                matchedOld[i] = o;
                used[o] = true;
            }
        }
        for (var i = 0; i < oldList.Count; i++)
            if (!used[i]) Remove(oldList[i]);

        // Walk backwards so every node knows what it goes before. A matched
        // node whose old position is still in order stays put; anything else
        // is (re)inserted, which moves it.
        var before = boundaryId;
        var lowestOld = int.MaxValue;
        for (var i = newList.Count - 1; i >= 0; i--)
        {
            var n = newList[i];
            var o = matchedOld[i];
            if (o < 0)
            {
                Create(owner, n, parentDomId, before);
            }
            else
            {
                Patch(owner, oldList[o], n, parentDomId);
                if (o < lowestOld) lowestOld = o;
                else Move(n, parentDomId, before);
            }
            before = FirstDomId(n);
        }
    }

    /// <summary>What <see cref="IdentityOf"/> compares, without building strings.</summary>
    private static bool SameIdentity(VNode a, VNode b)
    {
        if (a.Sequence != b.Sequence || !Equals(a.Key, b.Key)) return false;
        return (a, b) switch
        {
            (VElement x, VElement y) => x.Tag == y.Tag,
            (VComponent x, VComponent y) => x.Type == y.Type,
            (VText, VText) or (VMarkup, VMarkup) or (VRegion, VRegion) => true,
            _ => false,
        };
    }

    private static string IdentityOf(VNode n) =>
        n.Key is not null ? "k:" + n.Key + ":" + n.Identity : n.Sequence + ":" + n.Identity;

    private int FirstDomId(VNode n)
    {
        switch (n)
        {
            case VComponent c:
                if (c.State is not null && c.State.Tree.Count > 0) return FirstDomId(c.State.Tree[0]);
                return c.DomId;
            case VRegion r:
                return r.Children.Count > 0 ? FirstDomId(r.Children[0]) : r.DomId;
            default:
                return n.DomId;
        }
    }

    private void Create(ComponentState owner, VNode n, int parentDomId, int beforeId)
    {
        switch (n)
        {
            case VText t:
                t.DomId = _nextDomId++;
                Op(OpCreateText, t.DomId, t.Text);
                Op(OpInsert, parentDomId, t.DomId, beforeId);
                break;
            case VMarkup m:
                m.DomId = _nextDomId++;
                Op(OpCreateMarkup, m.DomId, m.Html);
                Op(OpInsert, parentDomId, m.DomId, beforeId);
                break;
            case VElement e:
                e.DomId = _nextDomId++;
                Op(OpCreateElement, e.DomId, e.Tag);
                foreach (var a in e.Attributes) SetAttribute(e, a);
                ApplyBareFlags(null, e);
                foreach (var c in e.Children) Create(owner, c, e.DomId, 0);
                Op(OpInsert, parentDomId, e.DomId, beforeId);
                e.ReferenceCapture?.Invoke(new ElementReference(e.DomId.ToString()));
                break;
            case VRegion r:
                r.DomId = _nextDomId++;
                Op(OpCreateMarker, r.DomId);
                Op(OpInsert, parentDomId, r.DomId, beforeId);
                foreach (var c in r.Children) Create(owner, c, parentDomId, r.DomId);
                break;
            case VComponent vc:
                var state = Instantiate(vc.Type, owner, parentDomId, beforeId, vc);
                SupplyParameters(state, vc.Parameters);
                break;
        }
    }

    private void Patch(ComponentState owner, VNode oldNode, VNode newNode, int parentDomId)
    {
        newNode.DomId = oldNode.DomId;
        switch (newNode)
        {
            case VText t:
                if (((VText)oldNode).Text != t.Text) Op(OpSetText, t.DomId, t.Text);
                break;
            case VMarkup m:
                if (((VMarkup)oldNode).Html != m.Html)
                {
                    var replacement = _nextDomId++;
                    Op(OpCreateMarkup, replacement, m.Html);
                    Op(OpInsert, parentDomId, replacement, m.DomId);
                    Op(OpRemove, m.DomId);
                    m.DomId = replacement;
                }
                break;
            case VElement e:
                var old = (VElement)oldNode;
                DiffAttributes(old, e);
                ApplyBareFlags(old, e);
                DiffChildren(owner, e.DomId, old.Children, e.Children, 0);
                e.ReferenceCapture?.Invoke(new ElementReference(e.DomId.ToString()));
                break;
            case VRegion r:
                DiffChildren(owner, parentDomId, ((VRegion)oldNode).Children, r.Children, r.DomId);
                break;
            case VComponent c:
                var state = ((VComponent)oldNode).State!;
                c.State = state;
                if (ParametersChanged(state.LastParameters, c.Parameters)) SupplyParameters(state, c.Parameters);
                break;
        }
    }

    private void Move(VNode n, int parentDomId, int beforeId)
    {
        switch (n)
        {
            case VComponent c when c.State is not null:
                foreach (var child in c.State.Tree) Move(child, parentDomId, beforeId);
                Op(OpInsert, parentDomId, c.DomId, beforeId);
                break;
            case VRegion r:
                foreach (var child in r.Children) Move(child, parentDomId, beforeId);
                Op(OpInsert, parentDomId, r.DomId, beforeId);
                break;
            default:
                Op(OpInsert, parentDomId, n.DomId, beforeId);
                break;
        }
    }

    private void Remove(VNode n)
    {
        switch (n)
        {
            case VComponent c:
                if (c.State is not null)
                {
                    foreach (var child in c.State.Tree) Remove(child);
                    Dispose(c.State);
                }
                Op(OpRemove, c.DomId);
                break;
            case VRegion r:
                foreach (var child in r.Children) Remove(child);
                Op(OpRemove, r.DomId);
                break;
            default:
                // Removing an element removes its subtree from the DOM in one
                // operation; the renderer still has to release what it tracked.
                ReleaseTree(n);
                Op(OpRemove, n.DomId);
                break;
        }
    }

    /// <summary>Forgets handlers and disposes components under a node, without DOM operations.</summary>
    private void ReleaseTree(VNode n)
    {
        switch (n)
        {
            case VElement e:
                foreach (var a in e.Attributes)
                    if (a.HandlerId != 0) _handlers.Remove(a.HandlerId);
                foreach (var c in e.Children) ReleaseTree(c);
                break;
            case VRegion r:
                foreach (var c in r.Children) ReleaseTree(c);
                break;
            case VComponent vc when vc.State is not null:
                Dispose(vc.State);
                break;
        }
    }

    private static bool ParametersChanged(List<ParameterValue>? oldParams, List<ParameterValue> newParams)
    {
        if (oldParams is null || oldParams.Count != newParams.Count) return true;
        for (var i = 0; i < newParams.Count; i++)
        {
            var o = oldParams[i];
            var n = newParams[i];
            if (o.Name != n.Name) return true;
            // Only values known to be immutable can be compared. Anything else,
            // a list or a model object, may have changed inside, so the child
            // re-renders, as Blazor does.
            if (!IsImmutable(n.Value) || !Equals(o.Value, n.Value)) return true;
        }
        return false;
    }

    private static bool IsImmutable(object? v) =>
        v is null || v is string || v is int || v is long || v is double || v is float || v is bool || v is char
        || v is DateTime || v is Enum || v is short || v is byte || v is Type;

    // ── attributes and events ────────────────────────────────────────────

    private void SetAttribute(VElement e, VAttribute a)
    {
        if (a.Value is EventCallback callback)
        {
            a.HandlerId = _nextHandlerId++;
            _handlers[a.HandlerId] = callback;
            Op(OpListen, e.DomId, EventName(a.Name), a.HandlerId, FlagsFor(e, a.Name));
            return;
        }
        Op(OpSetAttribute, e.DomId, a.Name, (string)a.Value);
    }

    private void DiffAttributes(VElement oldEl, VElement newEl)
    {
        foreach (var a in newEl.Attributes)
        {
            VAttribute? match = null;
            foreach (var o in oldEl.Attributes)
            {
                if (o.Name == a.Name) { match = o; break; }
            }
            if (match is null)
            {
                SetAttribute(newEl, a);
                continue;
            }
            if (a.Value is EventCallback cb)
            {
                if (match.HandlerId != 0)
                {
                    // Same element, same event: keep the listener, swap the delegate.
                    a.HandlerId = match.HandlerId;
                    _handlers[a.HandlerId] = cb;
                    if (FlagsFor(oldEl, a.Name) != FlagsFor(newEl, a.Name))
                        Op(OpListen, newEl.DomId, EventName(a.Name), a.HandlerId, FlagsFor(newEl, a.Name));
                }
                else
                {
                    SetAttribute(newEl, a);
                }
                continue;
            }
            if (match.HandlerId != 0)
            {
                _handlers.Remove(match.HandlerId);
                Op(OpUnlisten, newEl.DomId, EventName(match.Name));
                SetAttribute(newEl, a);
                continue;
            }
            // `value` is always re-sent: the user may have typed since, and the
            // DOM property is what has to match the model.
            if ((string)match.Value != (string)a.Value || a.Name == "value" || a.Name == "checked")
                Op(OpSetAttribute, newEl.DomId, a.Name, (string)a.Value);
        }
        foreach (var o in oldEl.Attributes)
        {
            var still = false;
            foreach (var a in newEl.Attributes)
            {
                if (a.Name == o.Name) { still = true; break; }
            }
            if (still) continue;
            if (o.HandlerId != 0)
            {
                _handlers.Remove(o.HandlerId);
                Op(OpUnlisten, newEl.DomId, EventName(o.Name));
            }
            else
            {
                Op(OpRemoveAttribute, newEl.DomId, o.Name);
            }
        }
    }

    /// <summary>
    /// <c>@onclick:stopPropagation</c> on an element with no <c>@onclick</c> of
    /// its own, as on a table cell holding buttons inside a clickable row. The
    /// flags still apply in the DOM, through a listener that dispatches nothing
    /// (handler 0).
    /// </summary>
    private void ApplyBareFlags(VElement? oldEl, VElement newEl)
    {
        foreach (var kv in newEl.Flags)
        {
            if (HasAttribute(newEl, kv.Key)) continue;
            var before = oldEl is not null && !HasAttribute(oldEl, kv.Key) ? FlagsFor(oldEl, kv.Key) : 0;
            if (before != kv.Value) Op(OpListen, newEl.DomId, EventName(kv.Key), 0, kv.Value);
        }
        if (oldEl is null) return;
        foreach (var kv in oldEl.Flags)
        {
            if (HasAttribute(oldEl, kv.Key) || HasAttribute(newEl, kv.Key) || newEl.Flags.ContainsKey(kv.Key)) continue;
            Op(OpUnlisten, newEl.DomId, EventName(kv.Key));
        }
    }

    private static bool HasAttribute(VElement e, string name)
    {
        foreach (var a in e.Attributes)
            if (a.Name == name) return true;
        return false;
    }

    private static string EventName(string attribute) => attribute.StartsWith("on") ? attribute.Substring(2) : attribute;

    private static int FlagsFor(VElement e, string attribute) => e.Flags.TryGetValue(attribute, out var f) ? f : 0;

    // ── the batch ────────────────────────────────────────────────────────

    private void BeginBatch()
    {
        _ops = new JsonWriter();
        _ops.BeginArray();
        _opCount = 0;
    }

    private void Flush()
    {
        if (_opCount == 0) return;
        _ops.EndArray();
        var json = _ops.ToString();
        BeginBatch();
        Js.InvokeVoid("dom.patch", json);
    }

    private void Op(int code, int a)
    {
        _ops.BeginArray().Number(code).Number(a).EndArray();
        _opCount++;
    }

    private void Op(int code, int a, string b)
    {
        _ops.BeginArray().Number(code).Number(a).String(b).EndArray();
        _opCount++;
    }

    private void Op(int code, int a, int b, int c)
    {
        _ops.BeginArray().Number(code).Number(a).Number(b).Number(c).EndArray();
        _opCount++;
    }

    private void Op(int code, int a, string b, string c)
    {
        _ops.BeginArray().Number(code).Number(a).String(b).String(c).EndArray();
        _opCount++;
    }

    private void Op(int code, int a, string b, int c, int d)
    {
        _ops.BeginArray().Number(code).Number(a).String(b).Number(c).Number(d).EndArray();
        _opCount++;
    }
}

/// <summary>Implemented by <see cref="CascadingValue{TValue}"/>: what it can supply.</summary>
internal interface ICascadingValueComponent
{
    bool CanSupply(Type type, string? name);
    object? CurrentValue { get; }
}
