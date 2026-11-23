namespace Ghostrig.Kernel.Errors;

/// <summary>
/// Root of every fault the simulated operating system can raise. Each fault carries an
/// authentic, stable error code that is surfaced to CScript code, the System Profiler and logs
/// (Doc 2 §4.1: explicit error handling, no silent fallbacks).
/// </summary>
public abstract class GhostrigException : Exception
{
    protected GhostrigException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Stable machine-readable error code (e.g. <c>403</c>, <c>NXDOMAIN</c>).</summary>
    public string Code { get; }

    public override string ToString() => $"[{Code}] {Message}";
}

#region Hardware

public abstract class HardwareException : GhostrigException
{
    protected HardwareException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException) { }
}

/// <summary>The rig's component configuration is invalid (missing slot, unknown part, slot full).</summary>
public sealed class HardwareConfigurationException(string message)
    : HardwareException("HW_CONFIG", message);

/// <summary>A driver submitted a command the device cannot accept (e.g. batch larger than <c>max_batch</c>).</summary>
public sealed class HardwareProtocolException(string message)
    : HardwareException("HW_PROTOCOL", message);

/// <summary>An operation is invalid in the bay's current power state.</summary>
public sealed class HardwareStateException(string message)
    : HardwareException("HW_STATE", message);

/// <summary>The kernel halted after a hardware protection fault (thermal cascade).</summary>
public sealed class KernelPanicException(string message)
    : HardwareException("KERNEL_PANIC", message);

/// <summary>Simulated RAM is exhausted.</summary>
public sealed class SimulatedOutOfMemoryException(string message)
    : HardwareException("ENOMEM", message);

#endregion

#region Virtual filesystem

public abstract class VfsException : GhostrigException
{
    protected VfsException(string code, string path, string message)
        : base(code, message)
    {
        Path = path;
    }

    /// <summary>The path the failing operation targeted.</summary>
    public string Path { get; }
}

/// <summary>The active session's privilege tier does not permit the operation.</summary>
public sealed class AccessDeniedException(string path, string message)
    : VfsException("403", path, message);

public sealed class VfsNotFoundException(string path)
    : VfsException("ENOENT", path, $"No such file or directory: {path}");

public sealed class VfsConflictException(string path)
    : VfsException("EEXIST", path, $"File exists: {path}");

public sealed class InvalidVfsPathException(string path, string reason)
    : VfsException("EINVAL", path, $"Invalid path '{path}': {reason}");

public sealed class VfsTypeMismatchException(string path, string code, string message)
    : VfsException(code, path, message);

#endregion

#region Network

public abstract class NetworkException : GhostrigException
{
    protected NetworkException(string code, string message)
        : base(code, message) { }
}

public sealed class DnsResolutionException(string hostname)
    : NetworkException("NXDOMAIN", $"Could not resolve host '{hostname}'.")
{
    public string Hostname { get; } = hostname;
}

public sealed class HostUnreachableException(string address)
    : NetworkException("EHOSTUNREACH", $"Host {address} is unreachable.");

public sealed class ConnectionRefusedException(string address, int port)
    : NetworkException("ECONNREFUSED", $"Connection refused by {address}:{port}.");

public sealed class AuthenticationFailedException(string address, string userName)
    : NetworkException("401", $"Authentication failed for '{userName}' at {address}.");

public sealed class NetworkProtocolException(string message)
    : NetworkException("EPROTO", message);

public sealed class SessionClosedException()
    : NetworkException("ESHUTDOWN", "The session has been closed.");

#endregion

#region Content

/// <summary>Authored content (hardware catalog, world manifest) failed validation.</summary>
public sealed class ContentValidationException(string source, string message)
    : GhostrigException("CONTENT", $"{source}: {message}")
{
    /// <summary>The content file or logical source that failed validation.</summary>
    public string ContentSource { get; } = source;
}

#endregion
