using System.Text.Json;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Virtualization;

/// <summary>
/// A request from the browser: a text frame <c>{ "type": …, "id": …, … }</c>. The <c>id</c> is the browser's own,
/// echoed in the reply.
/// </summary>
internal sealed record ToolRequest(string Type, string Id, JsonElement Body)
{
    public string? Text(string name, int maxLength)
    {
        if (!Body.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ToolException($"“{name}” must be text.");
        return text.Length <= maxLength ? text : throw new ToolException($"“{name}” is longer than {maxLength} characters.");
    }

    public string RequiredText(string name, int maxLength) =>
        Text(name, maxLength) is { Length: > 0 } text ? text : throw new ToolException($"“{name}” is required.");

    public long? Number(string name)
    {
        if (!Body.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw new ToolException($"“{name}” must be a whole number.");
    }

    public bool Flag(string name) => Body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public DateTimeOffset? Time(string name)
    {
        var text = Text(name, 64);
        return text is null ? null
            : DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time
            : throw new ToolException($"“{name}” must be a date and time.");
    }

    public IReadOnlyList<JsonElement> Array(string name, int maxCount)
    {
        if (!Body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new ToolException($"“{name}” must be a list.");
        }

        return value.GetArrayLength() <= maxCount ? [.. value.EnumerateArray()] : throw new ToolException($"“{name}” has more than {maxCount} items.");
    }
}

/// <summary>A failure to show the user, answering one request. The session goes on.</summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary>
/// The protocol db-core shares with the linux, storage and windows plugins' tools (docs/architecture.md §7.25, §7.32): after core's <c>connected</c>, the browser
/// sends requests as JSON text frames, each answered by <c>reply</c> <c>{ id, … }</c> or <c>error</c> <c>{ id, message }</c>.
/// Requests are handled one at a time, in order; a tool runs long work (a folder walk, a follow) in the background.
/// </summary>
internal abstract class ToolSession : IStreamSession
{
    public const int MaxIdLength = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly List<Task> _background = [];
    private IStreamChannel? _channel;

    /// <summary>Why the tool can't be used on this host (it isn't Linux, say), sent as <c>fatal</c> before the session ends.</summary>
    protected string? Unsupported { get; init; }

    public async Task RunAsync(IStreamChannel channel, CancellationToken cancellationToken)
    {
        _channel = channel;
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (Unsupported is { } reason)
            {
                await SendAsync(new { type = "fatal", message = reason }, cancellationToken);
                return;
            }

            while (await channel.ReceiveAsync(cancellationToken) is { } frame)
            {
                if (!frame.IsText)
                {
                    await BinaryAsync(frame.Data, ending.Token);
                    continue;
                }

                if (Parse(frame.Data) is not { } request)
                {
                    continue;
                }

                try
                {
                    await HandleAsync(request, ending.Token);
                }
                catch (ToolException ex)
                {
                    await ErrorAsync(request.Id, ex.Message, cancellationToken);
                }
                catch (ConnectionLostException ex)
                {
                    await SendAsync(new { type = "fatal", message = ex.Message }, cancellationToken);
                    return;
                }
            }
        }
        finally
        {
            await ending.CancelAsync();
            await EndAsync();
            Task[] running;
            lock (_background)
            {
                running = [.. _background];
            }

            foreach (var task in running)
            {
                try
                {
                    await task;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The session is ending.
                }
            }
        }
    }

    public abstract ValueTask DisposeAsync();

    protected abstract Task HandleAsync(ToolRequest request, CancellationToken cancellationToken);

    /// <summary>A binary frame: only a tool that takes uploads expects one.</summary>
    protected virtual Task BinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The session is ending: stop anything still running.</summary>
    protected virtual Task EndAsync() => Task.CompletedTask;

    protected Task ReplyAsync(string id, object? data, CancellationToken cancellationToken) =>
        SendAsync(new { type = "reply", id, data }, cancellationToken);

    protected Task ErrorAsync(string id, string message, CancellationToken cancellationToken) =>
        SendAsync(new { type = "error", id, message }, cancellationToken);

    /// <summary>Sends a binary frame, in turn with the text ones.</summary>
    protected async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _sending.WaitAsync(cancellationToken);
        try
        {
            await _channel!.SendAsync(data, cancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    protected async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, Json);
        await _sending.WaitAsync(cancellationToken);
        try
        {
            await _channel!.SendTextAsync(json, cancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>Runs work alongside the requests; its failures answer its request.</summary>
    protected void Background(string id, Func<Task> work, CancellationToken cancellationToken)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex) when (ex is ToolException or IOException or ConnectionLostException && !cancellationToken.IsCancellationRequested)
            {
                await ErrorAsync(id, ex.Message, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // Stopped, or the session is ending.
            }
        }, cancellationToken);
        lock (_background)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    internal static ToolRequest? Parse(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 and <= MaxIdLength } requestId)
            {
                return null;
            }

            return new ToolRequest(type.GetString()!, requestId, root.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The connection the tool needs went, and couldn't be opened again: the tool can't go on.</summary>
internal sealed class ConnectionLostException(string message) : Exception(message);
