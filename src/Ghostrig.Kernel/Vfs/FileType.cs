using System.Collections.Frozen;
using Ghostrig.Kernel.Errors;

namespace Ghostrig.Kernel.Vfs;

/// <summary>The six file types every VFS file belongs to (Doc 5 §4.1).</summary>
public enum FileType
{
    PlainText,
    Encrypted,
    CScript,
    Image,
    Audio,
    Video,
}

public static class FileTypes
{
    private static readonly FrozenDictionary<string, FileType> ByExtension = new Dictionary<string, FileType>
    {
        [".txt"] = FileType.PlainText,
        [".log"] = FileType.PlainText,
        [".cfg"] = FileType.PlainText,
        [".csv"] = FileType.PlainText,
        [".md"] = FileType.PlainText,
        [".enc"] = FileType.Encrypted,
        [".vault"] = FileType.Encrypted,
        [".dat"] = FileType.Encrypted,
        [".csc"] = FileType.CScript,
        [".drv"] = FileType.CScript,
        [".cform"] = FileType.CScript,
        [".png"] = FileType.Image,
        [".jpg"] = FileType.Image,
        [".wav"] = FileType.Audio,
        [".ogg"] = FileType.Audio,
        [".vid"] = FileType.Video,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlyCollection<string> SupportedExtensions => ByExtension.Keys;

    public static bool TryFromPath(VfsPath path, out FileType type) => ByExtension.TryGetValue(path.Extension, out type);

    /// <summary>Classifies a file by extension; unsupported extensions are rejected (strict six-type rule).</summary>
    public static FileType FromPath(VfsPath path) =>
        TryFromPath(path, out var type)
            ? type
            : throw new InvalidVfsPathException(path.Value,
                $"unsupported file type '{(path.Extension.Length == 0 ? "(none)" : path.Extension)}'.");
}
