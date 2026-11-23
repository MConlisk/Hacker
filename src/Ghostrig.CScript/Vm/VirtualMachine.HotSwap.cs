using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Semantics;

namespace Ghostrig.CScript.Vm;

/// <summary>Outcome of <see cref="VirtualMachine.HotSwap"/>.</summary>
public sealed record HotSwapResult(bool Succeeded, string Message, IReadOnlyList<string> PersistedGlobals)
{
    public static HotSwapResult Failed(string message) => new(false, message, []);
}

public sealed partial class VirtualMachine
{
    /// <summary>
    /// Replaces the running program with a recompiled one between events (Doc 7 §8.7). Globals marked <c>@Persist</c> whose
    /// module, name and type are unchanged keep their values; everything else is re-initialized. If the new program fails
    /// to compile, link or initialize, the previous image and globals stay in place.
    /// </summary>
    public HotSwapResult HotSwap(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (compilation.HasErrors)
        {
            return HotSwapResult.Failed("Compilation failed:\n" + string.Join("\n", compilation.Diagnostics.Where(d => d.IsError)));
        }

        ProgramImage next;
        try
        {
            next = ProgramImage.Link(compilation, Image.Natives);
        }
        catch (InvalidOperationException ex)
        {
            return HotSwapResult.Failed("Link failed: " + ex.Message);
        }

        if (!next.LinkErrors.IsEmpty)
        {
            return HotSwapResult.Failed("Link failed:\n" + string.Join("\n", next.LinkErrors));
        }

        var previousImage = Image;
        var previousGlobals = _globals;
        var persisted = PersistedValues(previousImage, next);

        Image = next;
        _globals = new Value[next.GlobalCount];
        try
        {
            Initialize();
        }
        catch (ScriptException ex)
        {
            Image = previousImage;
            _globals = previousGlobals;
            return HotSwapResult.Failed($"Initialization failed, previous image restored: {ex.Message}");
        }

        foreach (var (slot, value, _) in persisted)
        {
            _globals[slot] = value;
        }

        return new HotSwapResult(true, "Hot-swap complete.", [.. persisted.Select(p => p.Name)]);

        List<(int Slot, Value Value, string Name)> PersistedValues(ProgramImage from, ProgramImage to)
        {
            var result = new List<(int, Value, string)>();
            foreach (var oldModule in from.Linker.Compilation.Modules)
            {
                var newModule = to.Linker.Compilation.Modules.FirstOrDefault(m => m.Name == oldModule.Name);
                if (newModule is null)
                {
                    continue;
                }

                foreach (var oldField in oldModule.Members.OfType<FieldSymbol>().Where(f => f.IsPersistent && !f.IsConst))
                {
                    var newField = newModule.Lookup(oldField.Name).OfType<FieldSymbol>().FirstOrDefault();
                    if (newField is { IsPersistent: true } && newField.Type.ToString() == oldField.Type.ToString())
                    {
                        result.Add((to.GlobalSlot(newField), previousGlobals[from.GlobalSlot(oldField)], $"{oldModule.Name}.{oldField.Name}"));
                    }
                }
            }

            return result;
        }
    }
}
