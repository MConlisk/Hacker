using System.Collections.Immutable;
using Ghostrig.Kernel.Errors;

namespace Ghostrig.Kernel.Vfs;

/// <summary>
/// A normalized, absolute, Unix-style VFS path. Construction resolves <c>.</c>/<c>..</c>, collapses duplicate
/// separators and rejects malformed segments, so every instance is canonical and comparable ordinally.
/// </summary>
public readonly struct VfsPath : IEquatable<VfsPath>, IComparable<VfsPath>
{
    public const int MaxSegmentLength = 255;

    private readonly string? _value;

    private VfsPath(string value) => _value = value;

    public static VfsPath Root => default;

    public string Value => _value ?? "/";

    public bool IsRoot => Value == "/";

    public ImmutableArray<string> Segments =>
        IsRoot ? [] : [.. Value[1..].Split('/')];

    public string Name => IsRoot ? string.Empty : Value[(Value.LastIndexOf('/') + 1)..];

    public VfsPath Parent
    {
        get
        {
            if (IsRoot)
            {
                return this;
            }

            var i = Value.LastIndexOf('/');
            return i == 0 ? Root : new VfsPath(Value[..i]);
        }
    }

    /// <summary>Lower-case extension including the dot (e.g. <c>.csv</c>), or empty.</summary>
    public string Extension
    {
        get
        {
            var name = Name;
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? string.Empty : name[dot..].ToLowerInvariant();
        }
    }

    /// <summary>True when any segment is a dot-file / dot-directory (e.g. <c>/.root_archive</c>).</summary>
    public bool IsHidden => Segments.Any(s => s.StartsWith('.'));

    public static VfsPath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path[0] != '/')
        {
            throw new InvalidVfsPathException(path, "paths must be absolute (start with '/').");
        }

        var stack = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "":
                case ".":
                    continue;
                case "..":
                    if (stack.Count == 0)
                    {
                        throw new InvalidVfsPathException(path, "'..' escapes the filesystem root.");
                    }

                    stack.RemoveAt(stack.Count - 1);
                    continue;
                default:
                    ValidateSegment(path, segment);
                    stack.Add(segment);
                    continue;
            }
        }

        return stack.Count == 0 ? Root : new VfsPath("/" + string.Join('/', stack));
    }

    public static bool TryParse(string path, out VfsPath result)
    {
        try
        {
            result = Parse(path);
            return true;
        }
        catch (InvalidVfsPathException)
        {
            result = Root;
            return false;
        }
    }

    /// <summary>Resolves a relative or absolute path against this directory.</summary>
    public VfsPath Resolve(string relativeOrAbsolute) =>
        relativeOrAbsolute.StartsWith('/')
            ? Parse(relativeOrAbsolute)
            : Parse(Value.TrimEnd('/') + "/" + relativeOrAbsolute);

    public VfsPath Combine(string name)
    {
        ValidateSegment(name, name);
        return new VfsPath(IsRoot ? "/" + name : Value + "/" + name);
    }

    /// <summary>Re-roots this path beneath <paramref name="root"/> (chroot-style service mapping).</summary>
    public VfsPath Under(VfsPath root) => root.IsRoot ? this : IsRoot ? root : Parse(root.Value + Value);

    public bool IsAncestorOf(VfsPath other) =>
        !Equals(other) && (IsRoot || other.Value.StartsWith(Value + "/", StringComparison.Ordinal));

    /// <summary>Ancestors from the root down to (but excluding) this path.</summary>
    public IEnumerable<VfsPath> Ancestors()
    {
        var current = Root;
        var segments = Segments;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            yield return current;
            current = current.Combine(segments[i]);
        }

        if (!IsRoot)
        {
            yield return current;
        }
    }

    private static void ValidateSegment(string path, string segment)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            throw new InvalidVfsPathException(path, $"'{segment}' is not a valid name.");
        }

        if (segment.Length > MaxSegmentLength)
        {
            throw new InvalidVfsPathException(path, $"segment exceeds {MaxSegmentLength} characters.");
        }

        foreach (var ch in segment)
        {
            if (ch == '/' || ch == '\\' || char.IsControl(ch))
            {
                throw new InvalidVfsPathException(path, $"segment '{segment}' contains an illegal character.");
            }
        }
    }

    public bool Equals(VfsPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is VfsPath other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public int CompareTo(VfsPath other) => string.CompareOrdinal(Value, other.Value);
    public static bool operator ==(VfsPath a, VfsPath b) => a.Equals(b);
    public static bool operator !=(VfsPath a, VfsPath b) => !a.Equals(b);
    public override string ToString() => Value;

    public static implicit operator VfsPath(string path) => Parse(path);
}
