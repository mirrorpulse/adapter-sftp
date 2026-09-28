using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MirrorPulse.Adapter.Sdk;

public sealed record AdapterBinaryChunk(
    Guid RequestId,
    Guid InstanceId,
    Guid WorkerSessionId,
    Guid StreamId,
    long Offset,
    ReadOnlyMemory<byte> Data,
    bool EndOfStream);

/// <summary>Encodes the SDK side of the version-one binary chunk frame.</summary>
public static class AdapterBinaryChunkCodec
{
    private const int GuidBytes = 16;
    private const int HashBytes = 32;
    private const int HeaderBytes = GuidBytes * 4 + sizeof(long) + sizeof(uint) + sizeof(byte) + HashBytes;
    public const int MaxDataBytes = (int)AdapterPipeFrameLimits.MaxPayloadBytes - HeaderBytes;

    public static byte[] Encode(AdapterBinaryChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.RequestId == Guid.Empty || chunk.InstanceId == Guid.Empty ||
            chunk.WorkerSessionId == Guid.Empty || chunk.StreamId == Guid.Empty ||
            chunk.Offset < 0 || chunk.Data.Length > MaxDataBytes)
        {
            throw new ArgumentException("The binary chunk has invalid identity, offset, or size.", nameof(chunk));
        }

        byte[] output = new byte[HeaderBytes + chunk.Data.Length];
        int offset = 0;
        foreach (Guid id in new[] { chunk.RequestId, chunk.InstanceId, chunk.WorkerSessionId, chunk.StreamId })
        {
            id.TryWriteBytes(output.AsSpan(offset, GuidBytes));
            offset += GuidBytes;
        }

        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(offset, sizeof(long)), chunk.Offset);
        offset += sizeof(long);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, sizeof(uint)), (uint)chunk.Data.Length);
        offset += sizeof(uint);
        output[offset++] = (byte)((chunk.EndOfStream ? 1 : 0) | 2);
        SHA256.HashData(chunk.Data.Span).CopyTo(output.AsSpan(offset, HashBytes));
        offset += HashBytes;
        chunk.Data.Span.CopyTo(output.AsSpan(offset));
        return output;
    }

    public static AdapterBinaryChunk Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes)
        {
            throw new InvalidDataException("The binary chunk header is incomplete.");
        }

        int offset = 0;
        Guid requestId = new(payload.Slice(offset, GuidBytes));
        offset += GuidBytes;
        Guid instanceId = new(payload.Slice(offset, GuidBytes));
        offset += GuidBytes;
        Guid sessionId = new(payload.Slice(offset, GuidBytes));
        offset += GuidBytes;
        Guid streamId = new(payload.Slice(offset, GuidBytes));
        offset += GuidBytes;
        long dataOffset = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        byte flags = payload[offset++];
        ReadOnlySpan<byte> hash = payload.Slice(offset, HashBytes);
        offset += HashBytes;
        if (requestId == Guid.Empty || instanceId == Guid.Empty || sessionId == Guid.Empty ||
            streamId == Guid.Empty || dataOffset < 0 || count > MaxDataBytes ||
            payload.Length - offset != count || (flags & ~3) != 0 || (flags & 2) == 0)
        {
            throw new InvalidDataException("The binary chunk is malformed.");
        }

        ReadOnlySpan<byte> content = payload[offset..];
        if (!SHA256.HashData(content).AsSpan().SequenceEqual(hash))
        {
            throw new InvalidDataException("The binary chunk checksum is invalid.");
        }

        return new(requestId, instanceId, sessionId, streamId, dataOffset, content.ToArray(), (flags & 1) != 0);
    }
}
