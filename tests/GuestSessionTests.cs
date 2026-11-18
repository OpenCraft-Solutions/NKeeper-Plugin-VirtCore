using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Virtualization.Tests;

/// <summary>The guests session (docs/architecture.md §7.36), on an in-memory hypervisor.</summary>
public sealed class GuestSessionTests : IAsyncLifetime
{
    private static readonly Guid HostId = Guid.NewGuid();

    private readonly FakeHypervisor _hypervisor = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeChannel _channel = new();
    private IStreamSession? _session;
    private Task? _running;

    public async ValueTask InitializeAsync()
    {
        var handler = new GuestSessionHandler<FakeHypervisor>(_hypervisor, _audit);
        _session = await handler.OpenAsync(new StreamSessionRequest(new HostContext(HostId, "kvm01", null, "10.0.0.5", [], HostPermission.Use), Guid.NewGuid(), null, default), default);
        _running = _session.RunAsync(_channel, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Close();
        await _running!;
        await _session!.DisposeAsync();
    }

    [Fact]
    public async Task Lists_the_guests_with_their_consoles_and_actions()
    {
        var guests = (await _channel.ReplyAsync("list")).GetProperty("guests");

        Assert.Equal(["db01", "web01"], guests.EnumerateArray().Select(g => g.GetProperty("name").GetString()));
        Assert.Equal("vnc", guests[0].GetProperty("console").GetString());
        Assert.Equal(["shutdown", "stop"], guests[0].GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task A_power_action_runs_and_is_audited_with_the_guests_name_and_others_are_refused()
    {
        await _channel.ReplyAsync("power", new { guest = "1", action = "shutdown" });
        Assert.Equal(("1", "shutdown"), Assert.Single(_hypervisor.Powered));
        Assert.Equal(("kvm.power", (Guid?)HostId, "shutdown db01"), Assert.Single(_audit.Events));

        Assert.Contains("can't start now", await _channel.ErrorAsync("power", new { guest = "1", action = "start" }), StringComparison.Ordinal);
        Assert.Contains("isn't a power action", await _channel.ErrorAsync("power", new { guest = "1", action = "explode" }), StringComparison.Ordinal);
        Assert.Contains("isn't on this hypervisor", await _channel.ErrorAsync("power", new { guest = "9", action = "start" }), StringComparison.Ordinal);
        Assert.Contains("libvirt refused", await _channel.ErrorAsync("power", new { guest = "2", action = "start" }), StringComparison.Ordinal);
        Assert.Single(_audit.Events);
    }

    [Fact]
    public async Task A_console_carries_bytes_both_ways_and_says_when_the_guest_closes_it()
    {
        var opened = await _channel.ReplyAsync("console", new { guest = "1" });
        Assert.Equal(("vnc", "display-secret"), (opened.GetProperty("protocol").GetString(), opened.GetProperty("password").GetString()));

        // The guest speaks first, as a VNC server does; the browser answers.
        await _hypervisor.FromGuest.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), TestContext.Current.CancellationToken);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(await _channel.BinaryAsync()));
        _channel.SendBinary(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        Assert.Equal("RFB 003.008\n", await _hypervisor.ReadToGuestAsync(12));

        Assert.Contains("already open", await _channel.ErrorAsync("console", new { guest = "1" }), StringComparison.Ordinal);

        await _hypervisor.FromGuest.CompleteAsync();
        Assert.Equal("consoleClosed", (await _channel.MessageAsync("consoleClosed")).GetProperty("type").GetString());

        // Another can be opened now.
        Assert.Equal("vnc", (await _channel.ReplyAsync("console", new { guest = "1" })).GetProperty("protocol").GetString());
        await _channel.ReplyAsync("console.close");
    }

    [Fact]
    public async Task A_guest_without_a_console_is_refused()
    {
        Assert.Contains("no console", await _channel.ErrorAsync("console", new { guest = "2" }), StringComparison.Ordinal);
    }
}

/// <summary>A hypervisor in memory: two guests, one running with a VNC console whose bytes are a pipe each way.</summary>
internal sealed class FakeHypervisor : IGuestProvider, IGuestConnection
{
    private Pipe _toGuest = new();
    private Pipe _fromGuest = new();

    public string PluginId => "kvm";

    public string Product => "KVM";

    public List<(string Guest, string Action)> Powered { get; } = [];

    public PipeWriter FromGuest => _fromGuest.Writer;

    public Task<IGuestConnection> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken) => Task.FromResult<IGuestConnection>(this);

    public Task<IReadOnlyList<Guest>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guest>>(
        [
            new Guest("1", "db01", "running", ConsoleProtocols.Vnc, [GuestActions.Shutdown, GuestActions.Stop]),
            new Guest("2", "web01", "shut off", null, [GuestActions.Start]),
        ]);

    public Task PowerAsync(string guestId, string action, CancellationToken cancellationToken)
    {
        if (guestId == "2")
        {
            throw new GuestException("libvirt refused the request: not enough memory.");
        }

        Powered.Add((guestId, action));
        return Task.CompletedTask;
    }

    public Task<GuestConsole> OpenConsoleAsync(string guestId, CancellationToken cancellationToken)
    {
        if (guestId != "1")
        {
            throw new GuestException("This guest has no console the browser can open.");
        }

        _toGuest = new Pipe();
        _fromGuest = new Pipe();
        var stream = new DuplexStream(_fromGuest.Reader.AsStream(), _toGuest.Writer.AsStream());
        return Task.FromResult(new GuestConsole(ConsoleProtocols.Vnc, "display-secret", stream, () => stream.DisposeAsync()));
    }

    public async Task<string> ReadToGuestAsync(int count)
    {
        var buffer = new byte[count];
        await _toGuest.Reader.AsStream().ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
        return Encoding.ASCII.GetString(buffer);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);

        public override void Flush() => output.Flush();

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class FakeAudit : IPluginAudit
{
    public List<(string Action, Guid? HostId, string Detail)> Events { get; } = [];

    public Task RecordAsync(string action, Guid? hostId, string detail, CancellationToken cancellationToken = default)
    {
        lock (Events)
        {
            Events.Add((action, hostId, detail));
        }

        return Task.CompletedTask;
    }
}

/// <summary>The browser's end of a session, without a server: requests and console bytes in; replies, messages and console bytes out.</summary>
internal sealed class FakeChannel : IStreamChannel
{
    private readonly Channel<StreamFrame?> _in = Channel.CreateUnbounded<StreamFrame?>();
    private readonly Channel<JsonElement> _text = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<byte[]> _binary = Channel.CreateUnbounded<byte[]>();
    private int _next;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken) => await _in.Reader.ReadAsync(cancellationToken);

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _binary.Writer.TryWrite(data.ToArray());
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        _text.Writer.TryWrite(JsonDocument.Parse(json).RootElement.Clone());
        return Task.CompletedTask;
    }

    public string Send(string type, object? body = null)
    {
        var id = $"r{Interlocked.Increment(ref _next)}";
        var message = JsonSerializer.SerializeToNode(body ?? new { }, JsonSerializerOptions.Web)!.AsObject();
        message["type"] = type;
        message["id"] = id;
        _in.Writer.TryWrite(new StreamFrame(true, Encoding.UTF8.GetBytes(message.ToJsonString())));
        return id;
    }

    public void SendBinary(byte[] data) => _in.Writer.TryWrite(new StreamFrame(false, data));

    public void Close() => _in.Writer.TryWrite(null);

    public async Task<byte[]> BinaryAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _binary.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>The next text message matching, skipping others (they come back in any order).</summary>
    public async Task<JsonElement> MessageAsync(Func<JsonElement, bool> matches)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var skipped = new List<JsonElement>();
        try
        {
            while (true)
            {
                var message = await _text.Reader.ReadAsync(timeout.Token);
                if (matches(message))
                {
                    return message;
                }

                skipped.Add(message);
            }
        }
        finally
        {
            foreach (var message in skipped)
            {
                _text.Writer.TryWrite(message);
            }
        }
    }

    public Task<JsonElement> MessageAsync(string type) => MessageAsync(m => m.GetProperty("type").GetString() == type);

    public async Task<JsonElement> ReplyAsync(string type, object? body = null)
    {
        var id = Send(type, body);
        var answer = await MessageAsync(m => m.TryGetProperty("id", out var i) && i.GetString() == id);
        Assert.True(answer.GetProperty("type").GetString() == "reply", answer.ToString());
        return answer.GetProperty("data");
    }

    public async Task<string> ErrorAsync(string type, object? body = null)
    {
        var id = Send(type, body);
        var answer = await MessageAsync(m => m.TryGetProperty("id", out var i) && i.GetString() == id);
        Assert.True(answer.GetProperty("type").GetString() == "error", answer.ToString());
        return answer.GetProperty("message").GetString()!;
    }
}
