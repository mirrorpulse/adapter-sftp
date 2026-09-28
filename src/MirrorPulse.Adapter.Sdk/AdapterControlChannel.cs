using System.Text.Json;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>The SDK representation of the version-one MirrorPulse control envelope.</summary>
public sealed record AdapterControlFrame(
    int ProtocolVersion,
    string MessageType,
    Guid RequestId,
    Guid InstanceId,
    Guid WorkerSessionId,
    bool IsResponse,
    JsonElement Payload);

public sealed class AdapterControlChannel : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AdapterNamedPipeClient _pipe;
    private readonly Guid _instanceId;
    private readonly Guid _workerSessionId;

    public AdapterControlChannel(AdapterNamedPipeClient pipe, Guid instanceId, Guid workerSessionId)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        if (instanceId == Guid.Empty || workerSessionId == Guid.Empty)
        {
            throw new ArgumentException("A control channel requires non-empty instance and session IDs.");
        }

        _instanceId = instanceId;
        _workerSessionId = workerSessionId;
    }

    public async ValueTask SendAsync(
        string messageType,
        Guid requestId,
        bool isResponse,
        object payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payload);
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A control request ID cannot be empty.", nameof(requestId));
        }

        var frame = new AdapterControlFrame(
            1, messageType, requestId, _instanceId, _workerSessionId, isResponse,
            JsonSerializer.SerializeToElement(payload, JsonOptions));
        await _pipe.WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<AdapterControlFrame> ReadAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes = await _pipe.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        AdapterControlFrame frame = JsonSerializer.Deserialize<AdapterControlFrame>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The Host sent a null control frame.");
        if (frame.ProtocolVersion != 1 || frame.InstanceId != _instanceId ||
            frame.WorkerSessionId != _workerSessionId || frame.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(frame.MessageType) || frame.Payload.ValueKind is JsonValueKind.Undefined)
        {
            throw new InvalidDataException("The Host sent a control frame for another session or protocol.");
        }

        return frame;
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();

    public ValueTask SendChunkAsync(AdapterBinaryChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.InstanceId != _instanceId || chunk.WorkerSessionId != _workerSessionId)
        {
            throw new InvalidDataException("The binary chunk belongs to another Worker session.");
        }

        return _pipe.WriteFrameAsync(AdapterBinaryChunkCodec.Encode(chunk), cancellationToken);
    }

    public async ValueTask<AdapterBinaryChunk> ReadChunkAsync(CancellationToken cancellationToken = default)
    {
        AdapterBinaryChunk chunk = AdapterBinaryChunkCodec.Decode(
            await _pipe.ReadFrameAsync(cancellationToken).ConfigureAwait(false));
        if (chunk.InstanceId != _instanceId || chunk.WorkerSessionId != _workerSessionId)
        {
            throw new InvalidDataException("The binary chunk belongs to another Worker session.");
        }

        return chunk;
    }
}

public sealed record AdapterWorkerProcessArguments(Guid InstanceId, Guid WorkerSessionId, string PipeName)
{
    public static AdapterWorkerProcessArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 6 || args[0] != "--instance-id" || args[2] != "--worker-session-id" ||
            args[4] != "--pipe-name" || !Guid.TryParse(args[1], out Guid instanceId) ||
            !Guid.TryParse(args[3], out Guid sessionId) || instanceId == Guid.Empty ||
            sessionId == Guid.Empty || string.IsNullOrWhiteSpace(args[5]))
        {
            throw new ArgumentException("Expected --instance-id, --worker-session-id, and --pipe-name.", nameof(args));
        }

        return new(instanceId, sessionId, args[5]);
    }
}
