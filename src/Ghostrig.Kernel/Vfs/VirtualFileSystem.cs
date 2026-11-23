using Ghostrig.Kernel.Errors;
using Ghostrig.Kernel.Time;

namespace Ghostrig.Kernel.Vfs;

public enum VfsEntryKind
{
    Directory,
    File,
}

/// <summary>Integrity state of a file relative to its signed stock baseline (Doc 3 §5.1).</summary>
public enum SignatureStatus
{
    Unsigned,
    StockSigned,
    PlayerModified,
}

/// <summary>Immutable description of a VFS entry as visible to a caller.</summary>
public sealed record VfsEntryInfo(
    VfsPath Path,
    VfsEntryKind Kind,
    FileType? Type,
    long Length,
    Acl Acl,
    string? ContentHash,
    SignatureStatus Signature,
    SimTime Modified)
{
    public string Name => Path.Name;
    public bool IsHidden => Path.Name.StartsWith('.');
}

/// <summary>
/// One host's isolated, ACL-enforcing directory tree (Doc 3 §5.1). Every public operation validates the
/// principal: each ancestor directory requires <see cref="Permissions.Execute"/> (traverse), and the target
/// requires the operation's permission. Violations raise authentic <c>403</c> errors.
/// </summary>
public sealed class VirtualFileSystem
{
    private readonly SimClock _clock;
    private readonly DirectoryNode _root;

    public VirtualFileSystem(SimClock clock, Acl rootAcl)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(rootAcl);
        _clock = clock;
        _root = new DirectoryNode(string.Empty, rootAcl, clock.Now);
    }

    /// <summary>Default root: everyone may list and traverse; only root may write.</summary>
    public static Acl DefaultRootAcl => Acl.ForTier(PrivilegeTier.Guest, Permissions.ReadExecute);

    #region Queries

    public bool Exists(VfsPath path) => FindUnchecked(path) is not null;

    public VfsEntryInfo Stat(VfsPath path, Principal principal)
    {
        var node = Locate(path, principal, "stat");
        return Describe(path, node);
    }

    public IReadOnlyList<VfsEntryInfo> List(VfsPath path, Principal principal, bool includeHidden = false)
    {
        var node = Locate(path, principal, "list");
        if (node is not DirectoryNode directory)
        {
            throw new VfsTypeMismatchException(path.Value, "ENOTDIR", $"Not a directory: {path}");
        }

        Demand(directory, principal, Permissions.Read, path, "list");
        return directory.Children.Values
            .Where(child => includeHidden || !child.Name.StartsWith('.'))
            .Select(child => Describe(path.Combine(child.Name), child))
            .ToList();
    }

    public FileContent Read(VfsPath path, Principal principal)
    {
        var file = LocateFile(path, principal, "read");
        Demand(file, principal, Permissions.Read, path, "read");
        return file.Content;
    }

    public string ReadText(VfsPath path, Principal principal) => Read(path, principal).ReadText();

    #endregion

    #region Mutations

    /// <summary>Creates or overwrites a file. New files inherit the parent's ACL with the caller as owner unless <paramref name="acl"/> is given.</summary>
    public VfsEntryInfo Write(VfsPath path, Principal principal, FileContent content, Acl? acl = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var type = FileTypes.FromPath(path);
        var parent = LocateParentDirectory(path, principal, "write");
        if (parent.Children.TryGetValue(path.Name, out var existing))
        {
            if (existing is not FileNode file)
            {
                throw new VfsTypeMismatchException(path.Value, "EISDIR", $"Is a directory: {path}");
            }

            Demand(file, principal, Permissions.Write, path, "write");
            file.Content = content;
            file.Modified = _clock.Now;
            if (acl is not null)
            {
                RequireOwnerOrRoot(file, principal, path);
                file.Acl = acl;
            }

            return Describe(path, file);
        }

        Demand(parent, principal, Permissions.Write, path.Parent, "create");
        var created = new FileNode(path.Name, acl ?? parent.Acl.WithOwner(principal.UserName), _clock.Now, type, content);
        parent.Children.Add(path.Name, created);
        parent.Modified = _clock.Now;
        return Describe(path, created);
    }

    /// <summary>Appends bytes to an inline file (log streams), creating it if absent.</summary>
    public VfsEntryInfo Append(VfsPath path, Principal principal, ReadOnlySpan<byte> data)
    {
        var parent = LocateParentDirectory(path, principal, "append");
        if (!parent.Children.TryGetValue(path.Name, out var existing))
        {
            return Write(path, principal, FileContent.FromBytes(data));
        }

        if (existing is not FileNode file)
        {
            throw new VfsTypeMismatchException(path.Value, "EISDIR", $"Is a directory: {path}");
        }

        Demand(file, principal, Permissions.Write, path, "append");
        if (file.Content is not InlineFileContent inline)
        {
            throw new VfsTypeMismatchException(path.Value, "EINVAL", $"Cannot append to a generated file: {path}");
        }

        file.Content = inline.Append(data);
        file.Modified = _clock.Now;
        return Describe(path, file);
    }

    public VfsEntryInfo CreateDirectory(VfsPath path, Principal principal, Acl? acl = null, bool createParents = false)
    {
        if (path.IsRoot)
        {
            throw new VfsConflictException(path.Value);
        }

        if (createParents && !Exists(path.Parent))
        {
            // Intermediate directories inherit from their own parents, not from the leaf's explicit ACL.
            CreateDirectory(path.Parent, principal, acl: null, createParents: true);
        }

        var parent = LocateParentDirectory(path, principal, "mkdir");
        if (parent.Children.ContainsKey(path.Name))
        {
            throw new VfsConflictException(path.Value);
        }

        Demand(parent, principal, Permissions.Write, path.Parent, "mkdir");
        var directory = new DirectoryNode(path.Name, acl ?? parent.Acl.WithOwner(principal.UserName), _clock.Now);
        parent.Children.Add(path.Name, directory);
        parent.Modified = _clock.Now;
        return Describe(path, directory);
    }

    public void Delete(VfsPath path, Principal principal, bool recursive = false)
    {
        if (path.IsRoot)
        {
            throw new AccessDeniedException(path.Value, "The filesystem root cannot be deleted.");
        }

        var parent = LocateParentDirectory(path, principal, "delete");
        if (!parent.Children.TryGetValue(path.Name, out var target))
        {
            throw new VfsNotFoundException(path.Value);
        }

        Demand(parent, principal, Permissions.Write, path.Parent, "delete");
        if (target is DirectoryNode { Children.Count: > 0 } && !recursive)
        {
            throw new VfsTypeMismatchException(path.Value, "ENOTEMPTY", $"Directory not empty: {path}");
        }

        parent.Children.Remove(path.Name);
        parent.Modified = _clock.Now;
    }

    public VfsEntryInfo Copy(VfsPath source, VfsPath destination, Principal principal) =>
        Write(destination, principal, Read(source, principal));

    public void SetAcl(VfsPath path, Principal principal, Acl acl)
    {
        ArgumentNullException.ThrowIfNull(acl);
        var node = Locate(path, principal, "chmod");
        RequireOwnerOrRoot(node, principal, path);
        node.Acl = acl;
    }

    #endregion

    #region Stock signing

    /// <summary>Marks the current content as the signed stock baseline (root only).</summary>
    public void SignBaseline(VfsPath path, Principal principal)
    {
        var file = LocateFile(path, principal, "sign");
        if (!principal.IsRoot)
        {
            throw new AccessDeniedException(path.Value, $"Only root may sign stock baselines ({path}).");
        }

        file.Baseline = file.Content;
    }

    /// <summary>One-click restoration of a file to its signed baseline.</summary>
    public VfsEntryInfo RestoreBaseline(VfsPath path, Principal principal)
    {
        var file = LocateFile(path, principal, "restore");
        Demand(file, principal, Permissions.Write, path, "restore");
        file.Content = file.Baseline
            ?? throw new VfsTypeMismatchException(path.Value, "ENOSIG", $"No signed baseline exists for {path}.");
        file.Modified = _clock.Now;
        return Describe(path, file);
    }

    #endregion

    #region Serialization support

    /// <summary>Unchecked depth-first enumeration in ordinal path order (persistence and validation only).</summary>
    internal IEnumerable<VfsRecord> Export()
    {
        var stack = new Stack<(VfsPath Path, Node Node)>();
        stack.Push((VfsPath.Root, _root));
        while (stack.Count > 0)
        {
            var (path, node) = stack.Pop();
            yield return node switch
            {
                FileNode f => new VfsRecord(path, VfsEntryKind.File, f.Acl, f.Modified, f.Content, f.Baseline),
                _ => new VfsRecord(path, VfsEntryKind.Directory, node.Acl, node.Modified, null, null),
            };

            if (node is DirectoryNode d)
            {
                foreach (var child in d.Children.Values.Reverse())
                {
                    stack.Push((path.Combine(child.Name), child));
                }
            }
        }
    }

    /// <summary>Unchecked import of a record produced by <see cref="Export"/>; parents must already exist.</summary>
    internal void Import(VfsRecord record)
    {
        if (record.Path.IsRoot)
        {
            _root.Acl = record.Acl;
            _root.Modified = record.Modified;
            return;
        }

        if (FindUnchecked(record.Path.Parent) is not DirectoryNode parent)
        {
            throw new VfsNotFoundException(record.Path.Parent.Value);
        }

        Node node = record.Kind == VfsEntryKind.Directory
            ? new DirectoryNode(record.Path.Name, record.Acl, record.Modified)
            : new FileNode(record.Path.Name, record.Acl, record.Modified, FileTypes.FromPath(record.Path),
                record.Content ?? throw new ArgumentException("File records require content.", nameof(record)))
            {
                Baseline = record.Baseline,
            };
        parent.Children[record.Path.Name] = node;
    }

    #endregion

    #region Internals

    private Node Locate(VfsPath path, Principal principal, string operation)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Node current = _root;
        var walked = VfsPath.Root;
        foreach (var segment in path.Segments)
        {
            if (current is not DirectoryNode directory)
            {
                throw new VfsTypeMismatchException(walked.Value, "ENOTDIR", $"Not a directory: {walked}");
            }

            Demand(directory, principal, Permissions.Execute, walked, operation);
            if (!directory.Children.TryGetValue(segment, out var next))
            {
                throw new VfsNotFoundException(path.Value);
            }

            current = next;
            walked = walked.Combine(segment);
        }

        return current;
    }

    private FileNode LocateFile(VfsPath path, Principal principal, string operation) =>
        Locate(path, principal, operation) as FileNode
        ?? throw new VfsTypeMismatchException(path.Value, "EISDIR", $"Is a directory: {path}");

    private DirectoryNode LocateParentDirectory(VfsPath path, Principal principal, string operation)
    {
        if (path.IsRoot)
        {
            throw new InvalidVfsPathException(path.Value, "the root has no parent.");
        }

        var parent = Locate(path.Parent, principal, operation) as DirectoryNode
                     ?? throw new VfsTypeMismatchException(path.Parent.Value, "ENOTDIR", $"Not a directory: {path.Parent}");
        Demand(parent, principal, Permissions.Execute, path.Parent, operation);
        return parent;
    }

    private Node? FindUnchecked(VfsPath path)
    {
        Node current = _root;
        foreach (var segment in path.Segments)
        {
            if (current is not DirectoryNode directory || !directory.Children.TryGetValue(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static void Demand(Node node, Principal principal, Permissions permission, VfsPath path, string operation)
    {
        if (!node.Acl.Allows(principal, permission))
        {
            throw new AccessDeniedException(path.Value,
                $"403 Access Denied: '{principal.UserName}' ({principal.Tier}) may not {operation} {path} {node.Acl}");
        }
    }

    private static void RequireOwnerOrRoot(Node node, Principal principal, VfsPath path)
    {
        if (!principal.IsRoot && !string.Equals(node.Acl.Owner, principal.UserName, StringComparison.Ordinal))
        {
            throw new AccessDeniedException(path.Value, $"Only the owner or root may change permissions on {path}.");
        }
    }

    private static VfsEntryInfo Describe(VfsPath path, Node node) => node switch
    {
        FileNode f => new VfsEntryInfo(path, VfsEntryKind.File, f.Type, f.Content.Length, f.Acl, f.Content.ContentHash,
            f.Baseline is null ? SignatureStatus.Unsigned
            : ReferenceEquals(f.Baseline, f.Content) || f.Baseline.ContentHash == f.Content.ContentHash ? SignatureStatus.StockSigned
            : SignatureStatus.PlayerModified,
            f.Modified),
        _ => new VfsEntryInfo(path, VfsEntryKind.Directory, null, 0, node.Acl, null, SignatureStatus.Unsigned, node.Modified),
    };

    private abstract class Node(string name, Acl acl, SimTime modified)
    {
        public string Name { get; } = name;
        public Acl Acl { get; set; } = acl;
        public SimTime Modified { get; set; } = modified;
    }

    private sealed class DirectoryNode(string name, Acl acl, SimTime modified) : Node(name, acl, modified)
    {
        public SortedDictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FileNode(string name, Acl acl, SimTime modified, FileType type, FileContent content)
        : Node(name, acl, modified)
    {
        public FileType Type { get; } = type;
        public FileContent Content { get; set; } = content;
        public FileContent? Baseline { get; set; }
    }

    #endregion
}

/// <summary>Flat serialization record for one VFS entry.</summary>
internal sealed record VfsRecord(VfsPath Path, VfsEntryKind Kind, Acl Acl, SimTime Modified, FileContent? Content, FileContent? Baseline);
