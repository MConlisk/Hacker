using System.Globalization;
using System.Text;
using Ghostrig.CScript.CodeGen;
using Ghostrig.CScript.Runtime;
using Ghostrig.CScript.Vm;

namespace Ghostrig.CScript.Natives;

/// <summary>Host implementations of every native declaration in <c>Stdlib/Core.csd</c> (Doc 7 §13.1).</summary>
public static class CoreNatives
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Register(NativeRegistry r)
    {
        void F(string key, NativeFunction function) => r.Add("Core." + key, function);

        RegisterObject(F);
        RegisterScalars(F);
        RegisterString(F);
        RegisterBytes(F);
        RegisterTime(F);
        RegisterArray(F);
        RegisterTasks(F);
        RegisterArgsOptionResult(F);
        RegisterErrors(F);
    }

    #region object and scalars

    private static void RegisterObject(Action<string, NativeFunction> F)
    {
        F("object.ToString/0", (_, a) => Value.FromString(ValueText.Describe(a[0])));
        F("object.HashCode/0", (c, a) => Value.FromInt(c.Hash(a[0])));
        F("object.Equals/1", (_, a) => Value.FromBool(VirtualMachine.ValuesEqual(a[0], a[1])));
    }

    private static void RegisterScalars(Action<string, NativeFunction> F)
    {
        void Common(string type)
        {
            F($"{type}.ToString/0", (_, a) => Value.FromString(VirtualMachine.Format(a[0], null)));
            F($"{type}.Equals/1", (_, a) => Value.FromBool(VirtualMachine.ValuesEqual(a[0], a[1])));
            F($"{type}.HashCode/0", (c, a) => Value.FromInt(c.Hash(a[0])));
        }

        foreach (var type in new[] { "bool", "int", "float", "byte", "char" })
        {
            Common(type);
        }

        F("bool.Parse/1", (c, a) => bool.TryParse(a[0].AsString, out var b) ? Value.FromBool(b) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not a bool."));

        F("int.Parse/1", (c, a) => long.TryParse(a[0].AsString.Trim(), NumberStyles.AllowLeadingSign, Invariant, out var v)
            ? Value.FromInt(v) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not an integer."));
        F("int.TryParse/1", (_, a) => long.TryParse(a[0].AsString.Trim(), NumberStyles.AllowLeadingSign, Invariant, out var v) ? Value.FromInt(v) : Value.Null);
        F("int.ParseHex/1", (c, a) =>
        {
            var text = a[0].AsString.Trim();
            text = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, Invariant, out var v) ? Value.FromInt((long)v) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not hexadecimal.");
        });
        F("int.ToString/1", (_, a) => Value.FromString(VirtualMachine.Format(a[0], a[1].AsString)));
        F("int.ToHex/0", (_, a) => Value.FromString(a[0].Bits.ToString("x", Invariant)));
        F("int.CompareTo/1", (_, a) => Value.FromInt(a[0].Bits.CompareTo(a[1].Bits)));

        F("float.Parse/1", (c, a) => double.TryParse(a[0].AsString, NumberStyles.Float, Invariant, out var v)
            ? Value.FromFloat(v) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not a number."));
        F("float.TryParse/1", (_, a) => double.TryParse(a[0].AsString, NumberStyles.Float, Invariant, out var v) ? Value.FromFloat(v) : Value.Null);
        F("float.get_NaN/0", (_, _) => Value.FromFloat(double.NaN));
        F("float.get_PositiveInfinity/0", (_, _) => Value.FromFloat(double.PositiveInfinity));
        F("float.get_NegativeInfinity/0", (_, _) => Value.FromFloat(double.NegativeInfinity));
        F("float.IsNaN/0", (_, a) => Value.FromBool(double.IsNaN(a[0].AsFloat)));
        F("float.IsFinite/0", (_, a) => Value.FromBool(double.IsFinite(a[0].AsFloat)));
        F("float.ToString/1", (_, a) => Value.FromString(VirtualMachine.Format(a[0], a[1].AsString)));
        F("float.CompareTo/1", (_, a) => Value.FromInt(a[0].AsFloat.CompareTo(a[1].AsFloat)));

        F("byte.Parse/1", (c, a) => byte.TryParse(a[0].AsString.Trim(), NumberStyles.None, Invariant, out var v)
            ? Value.FromByte(v) : throw c.Throw("FormatError", $"'{a[0].AsString}' is not a byte."));
        F("byte.ToHex/0", (_, a) => Value.FromString(a[0].Bits.ToString("x2", Invariant)));
        F("byte.CompareTo/1", (_, a) => Value.FromInt(a[0].Bits.CompareTo(a[1].Bits)));

        static bool Is(Value v, Func<Rune, bool> test) => Rune.IsValid((int)v.Bits) && test(new Rune((int)v.Bits));
        F("char.IsDigit/1", (_, a) => Value.FromBool(Is(a[0], Rune.IsDigit)));
        F("char.IsLetter/1", (_, a) => Value.FromBool(Is(a[0], Rune.IsLetter)));
        F("char.IsLetterOrDigit/1", (_, a) => Value.FromBool(Is(a[0], Rune.IsLetterOrDigit)));
        F("char.IsWhiteSpace/1", (_, a) => Value.FromBool(Is(a[0], Rune.IsWhiteSpace)));
        F("char.IsUpper/1", (_, a) => Value.FromBool(Is(a[0], Rune.IsUpper)));
        F("char.IsLower/1", (_, a) => Value.FromBool(Is(a[0], Rune.IsLower)));
        F("char.ToUpper/1", (_, a) => Rune.IsValid((int)a[0].Bits) ? Value.FromChar(Rune.ToUpperInvariant(new Rune((int)a[0].Bits)).Value) : a[0]);
        F("char.ToLower/1", (_, a) => Rune.IsValid((int)a[0].Bits) ? Value.FromChar(Rune.ToLowerInvariant(new Rune((int)a[0].Bits)).Value) : a[0]);
        F("char.FromCode/1", (c, a) => a[0].Bits is >= 0 and <= 0x10FFFF ? Value.FromChar(a[0].Bits) : throw c.Throw("ArgumentError", $"{a[0].Bits} is not a valid character code."));
        F("char.get_Code/0", (_, a) => Value.FromInt(a[0].Bits));
        F("char.CompareTo/1", (_, a) => Value.FromInt(a[0].Bits.CompareTo(a[1].Bits)));
    }

    #endregion

    #region string

    private static void RegisterString(Action<string, NativeFunction> F)
    {
        static string S(Value v) => v.Object as string ?? string.Empty;
        static Value Str(NativeContext c, string s)
        {
            c.Charge(s.Length / 32);
            return Value.FromString(s);
        }

        F("string.get_Empty/0", (_, _) => Value.FromString(string.Empty));
        F("string.IsNullOrEmpty/1", (_, a) => Value.FromBool(string.IsNullOrEmpty(a[0].Object as string)));
        F("string.IsNullOrWhiteSpace/1", (_, a) => Value.FromBool(string.IsNullOrWhiteSpace(a[0].Object as string)));
        F("string.Join/2", (c, a) => Str(c, string.Join(S(a[0]), Iterate(c, a[1]).Select(v => S(v)))));
        F("string.Repeat/2", (c, a) => a[1].Bits is >= 0 and <= 1_000_000
            ? Str(c, string.Concat(Enumerable.Repeat(S(a[0]), (int)a[1].Bits)))
            : throw c.Throw("ArgumentError", "Repeat count out of range."));
        F("string.FromChars/1", (c, a) => Str(c, string.Concat(a[0].As<ArrayObject>().Items.Select(v => char.ConvertFromUtf32((int)v.Bits)))));
        F("string.get_Length/0", (_, a) => Value.FromInt(S(a[0]).Length));
        F("string.get_Item/1", (c, a) =>
        {
            var s = S(a[0]);
            return a[1].Bits >= 0 && a[1].Bits < s.Length ? Value.FromChar(s[(int)a[1].Bits]) : throw c.Throw("IndexError", $"Index {a[1].Bits} is out of range for length {s.Length}.");
        });
        F("string.get_IsEmpty/0", (_, a) => Value.FromBool(S(a[0]).Length == 0));
        F("string.Substring/1", (c, a) => Str(c, Substring(c, S(a[0]), a[1].Bits, S(a[0]).Length - a[1].Bits)));
        F("string.Substring/2", (c, a) => Str(c, Substring(c, S(a[0]), a[1].Bits, a[2].Bits)));
        F("string.IndexOf/1", (c, a) => { c.Charge(S(a[0]).Length / 16); return Value.FromInt(S(a[0]).IndexOf(S(a[1]), StringComparison.Ordinal)); });
        F("string.IndexOf/2", (c, a) =>
        {
            var s = S(a[0]);
            c.Charge(s.Length / 16);
            return a[2].Bits >= 0 && a[2].Bits <= s.Length ? Value.FromInt(s.IndexOf(S(a[1]), (int)a[2].Bits, StringComparison.Ordinal)) : throw c.Throw("IndexError", "Start index out of range.");
        });
        F("string.LastIndexOf/1", (c, a) => { c.Charge(S(a[0]).Length / 16); return Value.FromInt(S(a[0]).LastIndexOf(S(a[1]), StringComparison.Ordinal)); });
        F("string.Contains/1", (c, a) => { c.Charge(S(a[0]).Length / 16); return Value.FromBool(S(a[0]).Contains(S(a[1]), StringComparison.Ordinal)); });
        F("string.StartsWith/1", (_, a) => Value.FromBool(S(a[0]).StartsWith(S(a[1]), StringComparison.Ordinal)));
        F("string.EndsWith/1", (_, a) => Value.FromBool(S(a[0]).EndsWith(S(a[1]), StringComparison.Ordinal)));
        F("string.Split/1", (c, a) => Strings(c, S(a[0]).Split(S(a[1]))));
        F("string.Split/2", (c, a) => a[2].Bits >= 1 ? Strings(c, S(a[0]).Split(S(a[1]), (int)Math.Min(a[2].Bits, int.MaxValue))) : throw c.Throw("ArgumentError", "maxParts must be at least 1."));
        F("string.Lines/0", (c, a) => Strings(c, S(a[0]).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')));
        F("string.Trim/0", (c, a) => Str(c, S(a[0]).Trim()));
        F("string.TrimStart/0", (c, a) => Str(c, S(a[0]).TrimStart()));
        F("string.TrimEnd/0", (c, a) => Str(c, S(a[0]).TrimEnd()));
        F("string.ToUpper/0", (c, a) => Str(c, S(a[0]).ToUpperInvariant()));
        F("string.ToLower/0", (c, a) => Str(c, S(a[0]).ToLowerInvariant()));
        F("string.Replace/2", (c, a) => S(a[1]).Length == 0 ? throw c.Throw("ArgumentError", "Cannot replace an empty string.") : Str(c, S(a[0]).Replace(S(a[1]), S(a[2]), StringComparison.Ordinal)));
        F("string.PadLeft/1", (c, a) => Str(c, S(a[0]).PadLeft(Width(c, a[1]))));
        F("string.PadLeft/2", (c, a) => Str(c, S(a[0]).PadLeft(Width(c, a[1]), (char)a[2].Bits)));
        F("string.PadRight/1", (c, a) => Str(c, S(a[0]).PadRight(Width(c, a[1]))));
        F("string.PadRight/2", (c, a) => Str(c, S(a[0]).PadRight(Width(c, a[1]), (char)a[2].Bits)));
        F("string.Reverse/0", (c, a) => { var chars = S(a[0]).ToCharArray(); Array.Reverse(chars); return Str(c, new string(chars)); });
        F("string.ToChars/0", (c, a) => { c.Charge(S(a[0]).Length / 8); return c.NewArray([.. S(a[0]).Select(ch => Value.FromChar(ch))]); });
        F("string.ToBytes/0", (c, a) => { c.Charge(S(a[0]).Length / 16); return Value.FromObject(new BytesObject(Encoding.UTF8.GetBytes(S(a[0])))); });
        F("string.GetIterator/0", (c, a) => c.NewIterator(S(a[0]).Select(ch => Value.FromChar(ch))));
        F("string.ToString/0", (_, a) => a[0]);
        F("string.CompareTo/1", (_, a) => Value.FromInt(Math.Sign(string.CompareOrdinal(S(a[0]), S(a[1])))));
        F("string.Equals/1", (_, a) => Value.FromBool(string.Equals(S(a[0]), S(a[1]), StringComparison.Ordinal)));
        F("string.HashCode/0", (_, a) => Value.FromInt(StableHash.Of(S(a[0]))));
    }

    private static string Substring(NativeContext c, string s, long start, long length)
    {
        if (start < 0 || length < 0 || start + length > s.Length)
        {
            throw c.Throw("IndexError", $"Substring({start}, {length}) is out of range for length {s.Length}.");
        }

        return s.Substring((int)start, (int)length);
    }

    private static int Width(NativeContext c, Value v) =>
        v.Bits is >= 0 and <= 1_000_000 ? (int)v.Bits : throw c.Throw("ArgumentError", "Width out of range.");

    private static Value Strings(NativeContext c, string[] parts)
    {
        c.Charge(parts.Length);
        return c.NewArray([.. parts.Select(Value.FromString)]);
    }

    /// <summary>Enumerates any Iterable value (arrays, strings, bytes, natives, generators) through the VM.</summary>
    public static IEnumerable<Value> Iterate(NativeContext c, Value iterable)
    {
        switch (iterable.Object)
        {
            case ArrayObject array:
                foreach (var item in array.Items)
                {
                    yield return item;
                }

                yield break;
            case string s:
                foreach (var ch in s)
                {
                    yield return Value.FromChar(ch);
                }

                yield break;
            case BytesObject b:
                foreach (var x in b.Data)
                {
                    yield return Value.FromByte(x);
                }

                yield break;
            case null:
                throw c.Throw("NullError", "Cannot iterate a null value.");
        }

        var wk = c.Image.Linker.Compilation.WellKnown;
        var getIterator = c.Image.Linker.SlotOf(wk.Iterable!.GetDeclaredMembers("GetIterator").OfType<Semantics.MethodSymbol>().Single());
        var moveNext = c.Image.Linker.SlotOf(wk.Iterator!.GetDeclaredMembers("MoveNext").OfType<Semantics.MethodSymbol>().Single());
        var current = c.Image.Linker.SlotOf(wk.Iterator.GetDeclaredMembers("Current").OfType<Semantics.PropertySymbol>().Single().Getter!);
        var iterator = c.InvokeVirtual(getIterator, iterable);
        while (c.InvokeVirtual(moveNext, iterator).AsBool)
        {
            yield return c.InvokeVirtual(current, iterator);
        }
    }

    #endregion

    #region bytes

    private static void RegisterBytes(Action<string, NativeFunction> F)
    {
        static byte[] B(Value v) => v.As<BytesObject>().Data;
        static Value Of(NativeContext c, byte[] data)
        {
            c.Charge(data.Length / 64);
            return Value.FromObject(new BytesObject(data));
        }

        F("bytes.get_Empty/0", (_, _) => Value.FromObject(BytesObject.Empty));
        F("bytes.FromHex/1", (c, a) =>
        {
            try
            {
                return Of(c, Convert.FromHexString(a[0].AsString));
            }
            catch (FormatException)
            {
                throw c.Throw("FormatError", "Invalid hexadecimal text.");
            }
        });
        F("bytes.FromText/1", (c, a) => Of(c, Encoding.UTF8.GetBytes(a[0].AsString)));
        F("bytes.FromArray/1", (c, a) => Of(c, [.. a[0].As<ArrayObject>().Items.Select(v => (byte)v.Bits)]));
        F("bytes.Concat/1", (c, a) => Of(c, [.. a[0].As<ArrayObject>().Items.SelectMany(v => B(v))]));
        F("bytes.get_Length/0", (_, a) => Value.FromInt(B(a[0]).Length));
        F("bytes.get_Item/1", (c, a) =>
        {
            var data = B(a[0]);
            return a[1].Bits >= 0 && a[1].Bits < data.Length ? Value.FromByte(data[a[1].Bits]) : throw c.Throw("IndexError", $"Index {a[1].Bits} is out of range for length {data.Length}.");
        });
        F("bytes.Slice/2", (c, a) => Of(c, Slice(c, B(a[0]), a[1].Bits, a[2].Bits)));
        F("bytes.Slice/1", (c, a) => Of(c, Slice(c, B(a[0]), a[1].Bits, B(a[0]).Length - a[1].Bits)));
        F("bytes.IndexOf/1", (c, a) => { c.Charge(B(a[0]).Length / 16); return Value.FromInt(B(a[0]).AsSpan().IndexOf(B(a[1]))); });
        F("bytes.StartsWith/1", (_, a) => Value.FromBool(B(a[0]).AsSpan().StartsWith(B(a[1]))));
        F("bytes.ToHex/0", (c, a) => { c.Charge(B(a[0]).Length / 32); return Value.FromString(Convert.ToHexString(B(a[0])).ToLowerInvariant()); });
        F("bytes.ToText/0", (c, a) => { c.Charge(B(a[0]).Length / 32); return Value.FromString(Encoding.UTF8.GetString(B(a[0]))); });
        F("bytes.ToArray/0", (c, a) => c.NewArray([.. B(a[0]).Select(x => Value.FromByte(x))]));
        F("bytes.GetIterator/0", (c, a) => c.NewIterator(B(a[0]).Select(x => Value.FromByte(x))));
        F("bytes.ToString/0", (_, a) => Value.FromString(ValueText.Describe(a[0])));
        F("bytes.Equals/1", (_, a) => Value.FromBool(VirtualMachine.ValuesEqual(a[0], a[1])));
        F("bytes.HashCode/0", (_, a) => Value.FromInt(StableHash.Of(B(a[0]))));
    }

    private static byte[] Slice(NativeContext c, byte[] data, long start, long count)
    {
        if (start < 0 || count < 0 || start + count > data.Length)
        {
            throw c.Throw("IndexError", $"Slice({start}, {count}) is out of range for length {data.Length}.");
        }

        return data.AsSpan((int)start, (int)count).ToArray();
    }

    #endregion

    #region time

    private static void RegisterTime(Action<string, NativeFunction> F)
    {
        F("Duration.get_Zero/0", (_, _) => Value.FromDuration(0));
        F("Duration.FromNanoseconds/1", (_, a) => Value.FromDuration(a[0].Bits));
        F("Duration.FromMicroseconds/1", (_, a) => Value.FromDuration(unchecked(a[0].Bits * 1_000)));
        F("Duration.FromMilliseconds/1", (_, a) => Value.FromDuration(unchecked(a[0].Bits * 1_000_000)));
        F("Duration.FromSeconds/1", (_, a) => Value.FromDuration(unchecked(a[0].Bits * 1_000_000_000)));
        F("Duration.FromMinutes/1", (_, a) => Value.FromDuration(unchecked(a[0].Bits * 60_000_000_000)));
        F("Duration.FromHours/1", (_, a) => Value.FromDuration(unchecked(a[0].Bits * 3_600_000_000_000)));
        F("Duration.get_TotalNanoseconds/0", (_, a) => Value.FromInt(a[0].Bits));
        F("Duration.get_TotalMilliseconds/0", (_, a) => Value.FromFloat(a[0].Bits / 1e6));
        F("Duration.get_TotalSeconds/0", (_, a) => Value.FromFloat(a[0].Bits / 1e9));
        F("Duration.ToString/0", (_, a) => Value.FromString(ValueFormatting.FormatDuration(a[0].Bits)));
        F("Duration.CompareTo/1", (_, a) => Value.FromInt(a[0].Bits.CompareTo(a[1].Bits)));
        F("Duration.Equals/1", (_, a) => Value.FromBool(a[0].Bits == a[1].Bits));
        F("Duration.HashCode/0", (c, a) => Value.FromInt(c.Hash(a[0])));
        F("Instant.get_Nanoseconds/0", (_, a) => Value.FromInt(a[0].Bits));
        F("Instant.ToString/0", (_, a) => Value.FromString(a[0].ToString()));
        F("Instant.CompareTo/1", (_, a) => Value.FromInt(a[0].Bits.CompareTo(a[1].Bits)));
        F("Instant.Equals/1", (_, a) => Value.FromBool(a[0].Bits == a[1].Bits));
        F("Instant.HashCode/0", (c, a) => Value.FromInt(c.Hash(a[0])));
    }

    #endregion

    #region arrays

    private static void RegisterArray(Action<string, NativeFunction> F)
    {
        static Value[] A(Value v) => v.As<ArrayObject>().Items;
        static int Index(NativeContext c, Value[] items, long i) =>
            i >= 0 && i < items.Length ? (int)i : throw c.Throw("IndexError", $"Index {i} is out of range for length {items.Length}.");

        F("Array.get_Length/0", (_, a) => Value.FromInt(A(a[0]).Length));
        F("Array.get_Item/1", (c, a) => A(a[0])[Index(c, A(a[0]), a[1].Bits)]);
        F("Array.set_Item/2", (c, a) =>
        {
            A(a[0])[Index(c, A(a[0]), a[1].Bits)] = a[2];
            return Value.Null;
        });
        F("Array.Slice/2", (c, a) => SliceArray(c, A(a[0]), a[1].Bits, a[2].Bits));
        F("Array.Slice/1", (c, a) => SliceArray(c, A(a[0]), a[1].Bits, A(a[0]).Length - a[1].Bits));
        F("Array.Copy/0", (c, a) => SliceArray(c, A(a[0]), 0, A(a[0]).Length));
        F("Array.IndexOf/1", (c, a) =>
        {
            c.Charge(A(a[0]).Length);
            var target = a[1];
            return Value.FromInt(Array.FindIndex(A(a[0]), v => VirtualMachine.ValuesEqual(v, target)));
        });
        F("Array.Contains/1", (c, a) =>
        {
            c.Charge(A(a[0]).Length);
            var target = a[1];
            return Value.FromBool(Array.Exists(A(a[0]), v => VirtualMachine.ValuesEqual(v, target)));
        });
        F("Array.Fill/1", (c, a) =>
        {
            c.Charge(A(a[0]).Length / 4);
            Array.Fill(A(a[0]), a[1]);
            return Value.Null;
        });
        F("Array.GetIterator/0", (c, a) => c.NewIterator(A(a[0])));
    }

    private static Value SliceArray(NativeContext c, Value[] items, long start, long count)
    {
        if (start < 0 || count < 0 || start + count > items.Length)
        {
            throw c.Throw("IndexError", $"Slice({start}, {count}) is out of range for length {items.Length}.");
        }

        c.Charge(count / 8);
        return c.NewArray(items.AsSpan((int)start, (int)count).ToArray());
    }

    #endregion

    #region tasks

    private static void RegisterTasks(Action<string, NativeFunction> F)
    {
        F("Task.get_CompletedTask/0", (c, _) => Value.FromObject(c.CompletedTask(Value.Null, generic: false)));
        F("Task.Delay/1", (c, a) =>
        {
            var task = c.CreateTask(generic: false);
            var duration = a[0].Bits;
            if (duration <= 0)
            {
                task.TryComplete(Value.Null);
            }
            else
            {
                c.ScheduleAt(c.Now + duration, () => task.TryComplete(Value.Null));
            }

            return Value.FromObject(task);
        });
        F("Task.WhenAll/1", (c, a) =>
        {
            var tasks = a[0].As<ArrayObject>().Items.Select(v => v.As<TaskObject>()).ToList();
            var all = c.CreateTask(generic: false);
            var remaining = tasks.Count(t => !t.IsCompleted);
            void Check()
            {
                var faulted = tasks.FirstOrDefault(t => t.Status == Runtime.TaskStatus.Faulted);
                if (faulted is not null)
                {
                    faulted.FaultObserved = true;
                    all.TryFault(faulted.Fault);
                }
                else if (tasks.All(t => t.IsCompleted))
                {
                    all.TryComplete(Value.Null);
                }
            }

            foreach (var task in tasks.Where(t => !t.IsCompleted))
            {
                task.Continuations.Add(_ => Check());
            }

            if (remaining == 0)
            {
                Check();
            }

            return Value.FromObject(all);
        });
        F("Task.WhenAny/1", (c, a) =>
        {
            var tasks = a[0].As<ArrayObject>().Items.Select(v => v.As<TaskObject>()).ToList();
            var any = c.CreateTask(generic: true);
            for (var i = 0; i < tasks.Count; i++)
            {
                var index = i;
                if (tasks[i].IsCompleted)
                {
                    any.TryComplete(Value.FromInt(index));
                    break;
                }

                tasks[i].Continuations.Add(_ => any.TryComplete(Value.FromInt(index)));
            }

            if (tasks.Count == 0)
            {
                throw c.Throw("ArgumentError", "WhenAny needs at least one task.");
            }

            return Value.FromObject(any);
        });
        F("Task.get_IsCompleted/0", (_, a) => Value.FromBool(a[0].As<TaskObject>().IsCompleted));
        F("Task.get_IsFaulted/0", (_, a) => Value.FromBool(a[0].As<TaskObject>().Status == Runtime.TaskStatus.Faulted));
        F("Task.get_Fault/0", (_, a) => a[0].As<TaskObject>() is { Status: Runtime.TaskStatus.Faulted } t ? t.Fault : Value.Null);
        F("Task.FromResult/1", (c, a) => Value.FromObject(c.CompletedTask(a[0])));
        F("Task.get_Result/0", (c, a) =>
        {
            var task = a[0].As<TaskObject>();
            return task.Status switch
            {
                Runtime.TaskStatus.Completed => task.Result,
                Runtime.TaskStatus.Faulted => throw new ScriptException(task.Fault, "task faulted"),
                _ => throw c.Throw("Error", "The task has not completed; await it instead."),
            };
        });

        F("CancellationSource.CancellationSource/0", (_, a) =>
        {
            a[0].As<ObjectInstance>().NativeState = new CancellationState();
            return Value.Null;
        });
        F("CancellationSource.get_Token/0", (c, a) => Token(c, (CancellationState)a[0].As<ObjectInstance>().NativeState!));
        F("CancellationSource.Cancel/0", (_, a) =>
        {
            ((CancellationState)a[0].As<ObjectInstance>().NativeState!).Cancelled = true;
            return Value.Null;
        });
        F("CancellationSource.CancelAfter/1", (c, a) =>
        {
            var state = (CancellationState)a[0].As<ObjectInstance>().NativeState!;
            c.ScheduleAt(c.Now + Math.Max(0, a[1].Bits), () => state.Cancelled = true);
            return Value.Null;
        });
        F("CancellationToken.get_None/0", (c, _) => Token(c, new CancellationState()));
        F("CancellationToken.get_IsCancellationRequested/0", (_, a) => Value.FromBool(((CancellationState)a[0].As<NativeObject>().State).Cancelled));
        F("CancellationToken.ThrowIfCancelled/0", (c, a) =>
            ((CancellationState)a[0].As<NativeObject>().State).Cancelled ? throw c.Throw("CancelledError", "The operation was cancelled.") : Value.Null);
    }

    private sealed class CancellationState
    {
        public bool Cancelled { get; set; }
    }

    private static Value Token(NativeContext c, CancellationState state) =>
        Value.FromObject(new NativeObject(c.Image.CoreClass("CancellationToken"), state));

    #endregion

    #region Args, Option, Result

    private sealed record ArgsState(string[] Arguments);

    private sealed record OptionState(bool HasValue, Value Value);

    private sealed record ResultState(bool IsOk, Value Value, Value Error);

    /// <summary>Builds the <c>Args</c> object passed to an application's Main.</summary>
    public static Value CreateArgs(ProgramImage image, params string[] arguments) =>
        Value.FromObject(new NativeObject(image.CoreClass("Args"), new ArgsState(arguments)));

    private static void RegisterArgsOptionResult(Action<string, NativeFunction> F)
    {
        static string[] Args(Value v) => ((ArgsState)v.As<NativeObject>().State).Arguments;
        F("Args.get_Count/0", (_, a) => Value.FromInt(Args(a[0]).Length));
        F("Args.get_Item/1", (c, a) =>
        {
            var args = Args(a[0]);
            return a[1].Bits >= 0 && a[1].Bits < args.Length ? Value.FromString(args[a[1].Bits]) : throw c.Throw("IndexError", $"Argument {a[1].Bits} does not exist.");
        });
        F("Args.get_Positional/0", (c, a) => c.NewArray([.. Args(a[0]).Where(x => !x.StartsWith("--", StringComparison.Ordinal)).Select(Value.FromString)]));
        F("Args.Flag/1", (_, a) => Value.FromBool(Args(a[0]).Contains("--" + a[1].AsString, StringComparer.Ordinal)));
        F("Args.Option/1", (_, a) =>
        {
            var args = Args(a[0]);
            var prefix = "--" + a[1].AsString + "=";
            var match = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
            if (match is not null)
            {
                return Value.FromString(match[prefix.Length..]);
            }

            var index = Array.IndexOf(args, "--" + a[1].AsString);
            return index >= 0 && index + 1 < args.Length ? Value.FromString(args[index + 1]) : Value.Null;
        });
        F("Args.GetIterator/0", (c, a) => c.NewIterator(Args(a[0]).Select(Value.FromString)));

        static Value Option(NativeContext c, bool has, Value v) => Value.FromObject(new NativeObject(c.Image.CoreClass("Option"), new OptionState(has, v)));
        static OptionState Opt(Value v) => (OptionState)v.As<NativeObject>().State;
        F("Option.Some/1", (c, a) => Option(c, true, a[0]));
        F("Option.get_None/0", (c, _) => Option(c, false, Value.Null));
        F("Option.get_HasValue/0", (_, a) => Value.FromBool(Opt(a[0]).HasValue));
        F("Option.Unwrap/0", (c, a) => Opt(a[0]) is { HasValue: true } o ? o.Value : throw c.Throw("NullError", "Unwrap called on None."));
        F("Option.UnwrapOr/1", (_, a) => Opt(a[0]) is { HasValue: true } o ? o.Value : a[1]);

        static Value Result(NativeContext c, bool ok, Value v, Value e) => Value.FromObject(new NativeObject(c.Image.CoreClass("Result"), new ResultState(ok, v, e)));
        static ResultState Res(Value v) => (ResultState)v.As<NativeObject>().State;
        F("Result.Ok/1", (c, a) => Result(c, true, a[0], Value.Null));
        F("Result.Fail/1", (c, a) => Result(c, false, Value.Null, a[0]));
        F("Result.get_IsOk/0", (_, a) => Value.FromBool(Res(a[0]).IsOk));
        F("Result.Unwrap/0", (c, a) => Res(a[0]) is { IsOk: true } r ? r.Value
            : Res(a[0]).Error.Object is ObjectInstance { NativeState: ErrorState } ? throw new ScriptException(Res(a[0]).Error, "Unwrap on a failed result")
            : throw c.Throw("Error", $"Unwrap called on a failed result: {ValueText.Describe(Res(a[0]).Error)}"));
        F("Result.UnwrapOr/1", (_, a) => Res(a[0]) is { IsOk: true } r ? r.Value : a[1]);
        F("Result.get_ErrorValue/0", (_, a) => Res(a[0]).Error);
    }

    #endregion

    #region errors

    private static void RegisterErrors(Action<string, NativeFunction> F)
    {
        static ErrorState State(Value v) => v.As<ObjectInstance>().NativeState as ErrorState ?? new ErrorState("ERROR", string.Empty);

        F("Error.Error/1", (_, a) =>
        {
            var instance = a[0].As<ObjectInstance>();
            instance.NativeState = new ErrorState(CodeFor(instance.Class), a[1].Object as string ?? string.Empty);
            return Value.Null;
        });
        F("Error.Error/2", (_, a) =>
        {
            a[0].As<ObjectInstance>().NativeState = new ErrorState(a[2].Object as string ?? "ERROR", a[1].Object as string ?? string.Empty);
            return Value.Null;
        });
        F("Error.get_Code/0", (_, a) => Value.FromString(State(a[0]).Code));
        F("Error.get_Message/0", (_, a) => Value.FromString(State(a[0]).Message));
        F("Error.get_StackTrace/0", (_, a) => Value.FromString(State(a[0]).StackTrace));
        F("Error.ToString/0", (_, a) => Value.FromString($"{a[0].As<ObjectInstance>().Class.Name}: {State(a[0]).Message}"));

        foreach (var name in ErrorCodes.ErrorClasses.Where(n => n is not ("StepBudgetExceeded" or "KernelPanic")))
        {
            F($"{name}.{name}/1", (_, a) =>
            {
                var instance = a[0].As<ObjectInstance>();
                instance.NativeState = new ErrorState(CodeFor(instance.Class), a[1].Object as string ?? string.Empty);
                return Value.Null;
            });
        }
    }

    /// <summary>The code of the nearest Core error class in the hierarchy (user errors inherit their base's code).</summary>
    private static string CodeFor(RuntimeClass runtimeClass)
    {
        for (var c = runtimeClass; c is not null; c = c.Base)
        {
            if (c.Symbol?.Module?.Name == "Core")
            {
                return ErrorCodes.For(c.Name);
            }
        }

        return "ERROR";
    }

    #endregion
}
