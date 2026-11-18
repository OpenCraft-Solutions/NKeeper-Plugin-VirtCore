using NKeeper.Abstractions;

namespace NKeeper.Plugins.Virtualization;

/// <summary>
/// A kind of hypervisor, which a hypervisor plugin (kvm, later proxmox) gives virt-core (docs/architecture.md §7.36).
/// The provider connects through core and knows its hypervisor's commands; virt-core runs the session and its consoles.
/// </summary>
public interface IGuestProvider
{
    /// <summary>The hypervisor plugin's ID, which its audit events start with: <c>kvm.power</c>.</summary>
    string PluginId { get; }

    /// <summary>The hypervisor, for messages: <c>KVM</c>.</summary>
    string Product { get; }

    /// <summary>Connects to the hypervisor, from the plugin's session handler.</summary>
    /// <exception cref="ConnectException">The hypervisor couldn't be reached, refused the login, or isn't trusted yet.</exception>
    Task<IGuestConnection> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken);
}

/// <summary>An open connection to a hypervisor: its guests, their power, and their consoles.</summary>
public interface IGuestConnection : IAsyncDisposable
{
    /// <exception cref="GuestException">The hypervisor refused.</exception>
    Task<IReadOnlyList<Guest>> ListAsync(CancellationToken cancellationToken);

    /// <param name="action">One of the guest's <see cref="Guest.Actions"/>, from <see cref="GuestActions"/>.</param>
    /// <exception cref="GuestException">The hypervisor refused.</exception>
    Task PowerAsync(string guestId, string action, CancellationToken cancellationToken);

    /// <summary>The guest's console, as a stream of its protocol's bytes.</summary>
    /// <exception cref="GuestException">It has no console the browser can open, or it isn't running.</exception>
    Task<GuestConsole> OpenConsoleAsync(string guestId, CancellationToken cancellationToken);
}

/// <summary>A guest of a hypervisor, as it reports it.</summary>
/// <param name="Id">The hypervisor's own ID for it, such as libvirt's UUID.</param>
/// <param name="State">The hypervisor's word for its state: <c>running</c>, <c>shut off</c>…</param>
/// <param name="Console">Its console's protocol, one of <see cref="ConsoleProtocols"/>, or null for none.</param>
/// <param name="Actions">The power actions it takes now, from <see cref="GuestActions"/>.</param>
public sealed record Guest(string Id, string Name, string State, string? Console, IReadOnlyList<string> Actions);

/// <summary>A guest's console: its protocol, its display password if it has one, and its stream. Disposing it closes it.</summary>
public sealed class GuestConsole(string protocol, string? password, Stream stream, Func<ValueTask> close) : IAsyncDisposable
{
    public string Protocol { get; } = protocol;

    /// <summary>The display's own password, which the browser's console client needs (never a user's credential).</summary>
    public string? Password { get; } = password;

    public Stream Stream { get; } = stream;

    public ValueTask DisposeAsync() => close();
}

/// <summary>The console protocols the tab opens.</summary>
public static class ConsoleProtocols
{
    public const string Vnc = "vnc";
    public const string Spice = "spice";
}

/// <summary>The power actions the tab knows, in the order it shows them (from 1.x's <c>KvmActions</c>).</summary>
public static class GuestActions
{
    public const string Start = "start";
    public const string Shutdown = "shutdown";
    public const string Reboot = "reboot";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Hibernate = "hibernate";
    public const string Stop = "stop";
    public const string Reset = "reset";

    public static readonly IReadOnlyList<string> All = [Start, Shutdown, Reboot, Pause, Resume, Hibernate, Stop, Reset];

    public static bool IsKnown(string action) => All.Contains(action, StringComparer.Ordinal);
}

/// <summary>Something the hypervisor refused, or a guest can't do: shown to the user. The session goes on.</summary>
public sealed class GuestException(string message) : Exception(message);
