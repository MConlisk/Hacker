namespace Ghostrig.CScript.Natives.Std;

/// <summary>Registers every <c>Std.*</c> namespace's host implementations (Doc 7 §13.2–13.14).</summary>
public static class StdNatives
{
    public static void Register(NativeRegistry registry)
    {
        CollectionNatives.Register(registry);
        CacheNatives.Register(registry);
        SeqNatives.Register(registry);
        AlgoNatives.Register(registry);
        RandomNatives.Register(registry);
    }
}
