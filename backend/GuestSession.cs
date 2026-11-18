using NKeeper.Abstractions;

namespace NKeeper.Plugins.Virtualization;

/// <summary>
/// The stream session handler of a hypervisor's guests session type (docs/architecture.md §7.36): the hypervisor plugin
/// contributes <c>new SessionType(id, label, typeof(GuestSessionHandler&lt;TProvider&gt;))</c> and registers it with
/// <see cref="VirtualizationServiceCollectionExtensions.AddGuestProvider{TProvider}"/>.
/// </summary>
public sealed class GuestSessionHandler<TProvider>(TProvider provider, IPluginAudit audit) : IStreamSessionHandler
    where TProvider : class, IGuestProvider
{
    public async Task<IStreamSession> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new GuestSession(provider, await provider.OpenAsync(request, cancellationToken), audit, request.Host.Id);
    }
}

/// <summary>
/// A hypervisor's guests tab (ported from 1.x's VM list and consoles): <c>list</c>, <c>power</c>, and one console at a
/// time, whose bytes ride the session's binary frames both ways (<c>console</c>, <c>console.close</c>).
/// </summary>
internal sealed class GuestSession(IGuestProvider provider, IGuestConnection connection, IPluginAudit audit, Guid hostId) : ToolSession
{
    /// <summary>A guest ID is the hypervisor's: a UUID, a number, a node and number.</summary>
    public const int MaxGuestIdLength = 128;

    private readonly Lock _lock = new();
    private ConsoleRelay? _console;

    protected override async Task HandleAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case "list":
                Background(request.Id, async () => await ReplyAsync(request.Id, new { guests = await Guarded(() => connection.ListAsync(cancellationToken)) }, cancellationToken), cancellationToken);
                break;

            case "power":
                {
                    var guest = request.RequiredText("guest", MaxGuestIdLength);
                    var action = request.RequiredText("action", 32);
                    if (!GuestActions.IsKnown(action))
                    {
                        throw new ToolException($"“{action}” isn't a power action.");
                    }

                    Background(
                        request.Id,
                        async () =>
                        {
                            // The guest's name for the audit trail, as the hypervisor lists it: an ID alone says little.
                            var guests = await Guarded(() => connection.ListAsync(cancellationToken));
                            var named = guests.FirstOrDefault(g => g.Id == guest) ?? throw new ToolException("That guest isn't on this hypervisor any more.");
                            if (!named.Actions.Contains(action, StringComparer.Ordinal))
                            {
                                throw new ToolException($"{named.Name} can't {action} now ({named.State}).");
                            }

                            await Guarded(async () =>
                            {
                                await connection.PowerAsync(guest, action, cancellationToken);
                                return true;
                            });
                            await audit.RecordAsync($"{provider.PluginId}.power", hostId, $"{action} {named.Name}", CancellationToken.None);
                            await ReplyAsync(request.Id, null, cancellationToken);
                        },
                        cancellationToken);
                    break;
                }

            case "console":
                {
                    var guest = request.RequiredText("guest", MaxGuestIdLength);
                    lock (_lock)
                    {
                        if (_console is not null)
                        {
                            throw new ToolException("A console is already open in this tab. Close it first.");
                        }
                    }

                    var console = await Guarded(() => connection.OpenConsoleAsync(guest, cancellationToken));
                    var relay = new ConsoleRelay(console);
                    lock (_lock)
                    {
                        _console = relay;
                    }

                    await ReplyAsync(request.Id, new { protocol = console.Protocol, password = console.Password }, cancellationToken);
                    relay.Pump = Task.Run(() => PumpAsync(relay, cancellationToken), cancellationToken);
                    break;
                }

            case "console.close":
                await CloseConsoleAsync();
                await ReplyAsync(request.Id, null, cancellationToken);
                break;

            default:
                throw new ToolException($"The guests session doesn't know “{request.Type}”.");
        }
    }

    /// <summary>The browser's console bytes, to the guest.</summary>
    protected override async Task BinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ConsoleRelay? relay;
        lock (_lock)
        {
            relay = _console;
        }

        if (relay is null)
        {
            return;
        }

        try
        {
            await relay.Console.Stream.WriteAsync(data, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            await CloseConsoleAsync();
        }
    }

    /// <summary>The guest's console bytes, to the browser, until either side ends it.</summary>
    private async Task PumpAsync(ConsoleRelay relay, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await relay.Console.Stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await SendBinaryAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The console or the session is ending.
        }

        bool ours;
        lock (_lock)
        {
            ours = ReferenceEquals(_console, relay);
            if (ours)
            {
                _console = null;
            }
        }

        if (ours)
        {
            await relay.Console.DisposeAsync();
            if (!cancellationToken.IsCancellationRequested)
            {
                await SendAsync(new { type = "consoleClosed" }, cancellationToken);
            }
        }
    }

    private async Task CloseConsoleAsync()
    {
        ConsoleRelay? relay;
        lock (_lock)
        {
            relay = _console;
            _console = null;
        }

        if (relay is not null)
        {
            await relay.Console.DisposeAsync();
        }
    }

    /// <summary>The hypervisor's refusals answer the request; a lost connection ends the tool.</summary>
    private static async Task<T> Guarded<T>(Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (GuestException ex)
        {
            throw new ToolException(ex.Message);
        }
        catch (ConnectException ex)
        {
            throw new ConnectionLostException(ex.Message);
        }
    }

    protected override Task EndAsync() => CloseConsoleAsync();

    public override async ValueTask DisposeAsync()
    {
        await CloseConsoleAsync();
        await connection.DisposeAsync();
    }

    private sealed class ConsoleRelay(GuestConsole console)
    {
        public GuestConsole Console { get; } = console;

        public Task? Pump { get; set; }
    }
}
