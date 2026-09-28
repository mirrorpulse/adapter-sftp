using System.Buffers.Binary;
using System.IO.Pipes;

namespace MirrorPulse.Adapter.Sdk;

public static class AdapterPipeFrameLimits
{
    public const int LengthPrefixBytes = 4;
    public const uint MaxPayloadBytes = 4 * 1024 * 1024;
}

/// <summary>
/// Template SDK client for the current-user MirrorPulse Named Pipe protocol.
/// </summary>
public sealed class AdapterNamedPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;

    private AdapterNamedPipeClient(NamedPipeClientStream stream)
    {
        _stream = stream;
    }

    public static async Task<AdapterNamedPipeClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var timeoutMilliseconds = timeout == Timeout.InfiniteTimeSpan
            ? Timeout.Infinite
            : timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue
                ? throw new ArgumentOutOfRangeException(nameof(timeout))
                : (int)Math.Ceiling(timeout.TotalMilliseconds);
        var stream = new NamedPipeClientStream(".", pipeName.Trim(), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            return new AdapterNamedPipeClient(stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length > AdapterPipeFrameLimits.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var prefix = new byte[AdapterPipeFrameLimits.LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await _stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        var prefix = new byte[AdapterPipeFrameLimits.LengthPrefixBytes];
        await ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length > AdapterPipeFrameLimits.MaxPayloadBytes)
        {
            throw new InvalidDataException("The MirrorPulse frame exceeds the SDK payload limit.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The MirrorPulse pipe ended before a complete frame was received.");
            }

            offset += read;
        }
    }
}
