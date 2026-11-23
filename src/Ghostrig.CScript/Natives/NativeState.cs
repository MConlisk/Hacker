using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Natives;

/// <summary>
/// Base of every mutable host-side object behind a standard-library instance (collections, caches, buffers, generators).
/// Mutating natives call <see cref="NativeContext.Mutating"/> first, which snapshots the state once per journal segment so a
/// faulted event rolls native state back exactly like fields and arrays (Doc 7 §8.6). <see cref="Version"/> detects
/// modification during iteration.
/// </summary>
public abstract class NativeState
{
    internal long SnapshotEpoch { get; set; }

    /// <summary>Incremented by every structural mutation; live iterators compare it to fail fast.</summary>
    public int Version { get; protected set; }

    /// <summary>Marks a structural change (called by <see cref="NativeContext.Mutating"/>).</summary>
    internal void Touch() => Version++;

    /// <summary>Yields items while failing fast if the state is modified mid-iteration.</summary>
    protected IEnumerable<Value> Guard(NativeContext context, IEnumerable<Value> items)
    {
        var version = Version;
        using var e = items.GetEnumerator();
        while (true)
        {
            if (Version != version)
            {
                throw context.Throw("Error", "The collection was modified during iteration.");
            }

            if (!e.MoveNext())
            {
                yield break;
            }

            yield return e.Current;
        }
    }

    /// <summary>Returns an opaque copy of the mutable state.</summary>
    protected internal abstract object CaptureSnapshot();

    /// <summary>Restores a copy produced by <see cref="CaptureSnapshot"/>.</summary>
    protected internal abstract void RestoreSnapshot(object snapshot);
}

/// <summary>Immutable host state: never journaled.</summary>
public abstract class ImmutableNativeState : NativeState
{
    protected internal sealed override object CaptureSnapshot() => this;

    protected internal sealed override void RestoreSnapshot(object snapshot) { }
}

/// <summary>Iterator over a native collection that faults if the collection changes underneath it.</summary>
public sealed class VersionedIterator(NativeState owner, IEnumerator<Value> inner, long stepsPerItem = 1) : NativeIterator
{
    private readonly int _version = owner.Version;

    public override Value Current => inner.Current;

    public override bool MoveNext(NativeContext context)
    {
        if (owner.Version != _version)
        {
            throw context.Throw("Error", "The collection was modified during iteration.");
        }

        context.Charge(stepsPerItem);
        return inner.MoveNext();
    }
}
