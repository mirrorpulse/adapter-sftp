using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet.Common;

namespace MirrorPulse.Adapter.Sftp.Worker;

internal sealed class SftpTransferProtocol(AdapterControlChannel channel, AdapterWorkerProcessArguments arguments,
    SftpWorkerRoots roots, string cache) : IAsyncDisposable
{
    public async Task RunAsync(CancellationToken token)
    {
        _ = cache; // The Host owns cache paths; read-only operations create no transfer lease.
        while (true)
        {
            AdapterWorkerFrame frame = await channel.ReadNextAsync(token).ConfigureAwait(false);
            if (frame.Chunk is not null) throw new InvalidDataException("UnexpectedChunk");
            AdapterControlFrame command = frame.Control!;
            if (command.IsResponse) throw new InvalidDataException("UnexpectedResponse");
            if (command.MessageType == "Stop") { await ReplyAsync(command, "Stopped", new { }, token).ConfigureAwait(false); return; }
            try
            {
                if (command.MessageType == "Cancel")
                {
                    string rootKey = command.Payload.GetProperty("rootKey").GetString() ?? throw new InvalidDataException("RootRequired");
                    roots.Get(rootKey);
                    Guid targetRequestId = command.Payload.GetProperty("targetRequestId").GetGuid();
                    if (targetRequestId == Guid.Empty) throw new InvalidDataException("InvalidRequest");
                    await ReplyAsync(command, "CancelAck", new { rootKey, targetRequestId, status = "alreadyCompleted" }, token).ConfigureAwait(false);
                    continue;
                }
                AdapterFileAddress address = AdapterProtocolJson.ReadAddress(command.Payload, 2);
                SftpWorkerRoot root = roots.Get(address.RootKey);
                _ = SftpPathPolicy.Resolve(root.Configuration.Endpoint, address.Path);
                switch (command.MessageType)
                {
                    case "Stat":
                        await ReplyAsync(command, "StatResult", new
                        {
                            rootKey = address.RootKey,
                            revision = await SftpOperations.RevisionAsync(root, address.Path, token).ConfigureAwait(false)
                        }, token).ConfigureAwait(false);
                        break;
                    case "List":
                        string? cursor = command.Payload.TryGetProperty("cursor", out JsonElement cursorValue) && cursorValue.ValueKind == JsonValueKind.String ? cursorValue.GetString() : null;
                        object page = await SftpOperations.ListAsync(root, address, command.Payload.GetProperty("pageSize").GetInt32(), cursor, token).ConfigureAwait(false);
                        await ReplyAsync(command, "DirectoryPage", page, token).ConfigureAwait(false);
                        break;
                    case "ReadRange":
                        long offset = command.Payload.GetProperty("offset").GetInt64();
                        long length = command.Payload.GetProperty("length").GetInt64();
                        if (offset < 0 || length is < 1 or > AdapterBinaryChunkV2Codec.MaximumChunkBytes || offset > long.MaxValue - length)
                            throw new InvalidDataException("InvalidRange");
                        string? expected = command.Payload.TryGetProperty("expectedRevision", out JsonElement revision) && revision.ValueKind == JsonValueKind.String ? revision.GetString() : null;
                        byte[] bytes = await SftpOperations.ReadAsync(root, address, offset, checked((int)length), expected, token).ConfigureAwait(false);
                        Guid stream = Guid.NewGuid();
                        await ReplyAsync(command, "ReadRangeReady", new { rootKey = address.RootKey, streamId = stream, length }, token).ConfigureAwait(false);
                        await channel.SendChunkAsync(new(command.RequestId, arguments.InstanceId, arguments.WorkerSessionId, stream, offset, bytes, true)
                        { RootKey = address.RootKey }, token).ConfigureAwait(false);
                        break;
                    default: throw new InvalidDataException("ConditionalMutationUnavailable");
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException or FormatException or SshException or KeyNotFoundException)
            {
                string[] codes = ["UnknownRoot", "RootOffline", "InvalidPath", "InvalidCursor", "InvalidPageSize", "InvalidRange",
                    "RemoteConflict", "SourceUnavailable", "DirectoryEnumerationIncomplete", "ConditionalMutationUnavailable"];
                string code = exception is InvalidDataException && codes.Contains(exception.Message, StringComparer.Ordinal)
                    ? exception.Message : exception is SshException or IOException ? "RetryableTransferFailure" : "InvalidRequest";
                string? root = command.Payload.TryGetProperty("rootKey", out JsonElement rootValue) && rootValue.ValueKind == JsonValueKind.String ? rootValue.GetString() : null;
                Guid? operation = command.Payload.TryGetProperty("operationId", out JsonElement operationValue) && operationValue.TryGetGuid(out Guid id) ? id : null;
                await ReplyAsync(command, "OperationError", new { rootKey = root, operationId = operation, code }, token).ConfigureAwait(false);
            }
        }
    }
    private ValueTask ReplyAsync(AdapterControlFrame command, string type, object payload, CancellationToken token) =>
        channel.SendAsync(type, command.RequestId, true, payload, token);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
