using System.Collections.Immutable;
using System.Text;

namespace Ghostrig.Kernel.Vfs;

/// <summary>Role tiers for role-based access control (Doc 3 §5.1). Ordered: higher tiers include lower ones.</summary>
public enum PrivilegeTier
{
    Guest = 0,
    User = 1,
    Operator = 2,
    Root = 3,
}

[Flags]
public enum Permissions
{
    None = 0,
    Read = 1,
    Write = 2,

    /// <summary>Traverse a directory, or execute a CScript file.</summary>
    Execute = 4,
    ReadExecute = Read | Execute,
    All = Read | Write | Execute,
}

/// <summary>The identity an operation is performed as.</summary>
public sealed record Principal(string UserName, PrivilegeTier Tier)
{
    public static readonly Principal Anonymous = new("anonymous", PrivilegeTier.Guest);

    /// <summary>The kernel itself (log appends, world loading). Never handed to CScript code.</summary>
    public static readonly Principal System = new("root", PrivilegeTier.Root);

    public bool IsRoot => Tier == PrivilegeTier.Root;
}

/// <summary>Who an ACL entry applies to.</summary>
public abstract record AclSubject
{
    public abstract bool Matches(Principal principal);

    /// <summary>Everyone at or above a tier.</summary>
    public sealed record TierAtLeast(PrivilegeTier Tier) : AclSubject
    {
        public override bool Matches(Principal principal) => principal.Tier >= Tier;
        public override string ToString() => Tier.ToString();
    }

    /// <summary>One named account.</summary>
    public sealed record User(string UserName) : AclSubject
    {
        public override bool Matches(Principal principal) => string.Equals(principal.UserName, UserName, StringComparison.Ordinal);
        public override string ToString() => $"User ({UserName})";
    }
}

public sealed record AclEntry(AclSubject Subject, Permissions Permissions)
{
    public override string ToString() => $"{Subject} - {Describe(Permissions)}";

    internal static string Describe(Permissions p)
    {
        if (p == Permissions.None)
        {
            return "None";
        }

        var parts = new List<string>(3);
        if (p.HasFlag(Permissions.Read)) parts.Add("Read");
        if (p.HasFlag(Permissions.Write)) parts.Add("Write");
        if (p.HasFlag(Permissions.Execute)) parts.Add("Execute");
        return string.Join('/', parts);
    }
}

/// <summary>
/// Access control list carrying owner/group identity and permission entries. Root is always permitted;
/// otherwise access is granted when any matching entry carries every requested permission flag.
/// </summary>
public sealed record Acl(string Owner, string Group, ImmutableArray<AclEntry> Entries)
{
    public static Acl ForTier(PrivilegeTier tier, Permissions permissions, string owner = "root", string group = "root") =>
        new(owner, group, [new AclEntry(new AclSubject.TierAtLeast(tier), permissions)]);

    public static Acl ForUser(string userName, Permissions permissions, string? owner = null) =>
        new(owner ?? userName, owner ?? userName, [new AclEntry(new AclSubject.User(userName), permissions)]);

    /// <summary>Only root may touch the entry.</summary>
    public static Acl RootOnly => new("root", "root", []);

    public bool Allows(Principal principal, Permissions requested)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.IsRoot || requested == Permissions.None)
        {
            return true;
        }

        var granted = Permissions.None;
        foreach (var entry in Entries)
        {
            if (entry.Subject.Matches(principal))
            {
                granted |= entry.Permissions;
            }
        }

        if (string.Equals(principal.UserName, Owner, StringComparison.Ordinal))
        {
            // Owners always retain control of their own entries.
            granted |= Permissions.All;
        }

        return (granted & requested) == requested;
    }

    public Acl WithOwner(string owner) => this with { Owner = owner, Group = owner };

    public Acl With(AclEntry entry) => this with { Entries = Entries.Add(entry) };

    public override string ToString()
    {
        var sb = new StringBuilder("[ACL: ");
        sb.Append(Entries.IsEmpty ? "Root" : string.Join(", ", Entries));
        sb.Append(']');
        return sb.ToString();
    }

    public bool Equals(Acl? other) =>
        other is not null && Owner == other.Owner && Group == other.Group && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => HashCode.Combine(Owner, Group, Entries.Length);
}
