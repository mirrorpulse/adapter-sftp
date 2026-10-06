using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet.Common;

namespace MirrorPulse.Adapter.Sftp.Worker;

internal sealed class SftpTransferProtocol(AdapterControlChannel channel, AdapterWorkerProcessArguments arguments,
    SftpWorkerRoots roots, string cache) : IAsyncDisposable
{
    private readonly Dictionary<Guid, PendingUpload> _uploads = [];
    private readonly Dictionary<Guid, string> _bindings = [];
    private readonly Queue<Guid> _bindingOrder = [];

    public async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            AdapterWorkerFrame frame = await channel.ReadNextAsync(token).ConfigureAwait(false);
            if (frame.Chunk is { } chunk)
            {
                await ReceiveAsync(chunk, token).ConfigureAwait(false);
                continue;
            }
            AdapterControlFrame command = frame.Control!;
            if (command.IsResponse) throw new InvalidDataException("UnexpectedResponse");
            if (command.MessageType == "Stop") { await ReplyAsync(command, "Stopped", new { }, token).ConfigureAwait(false); return; }
            try
            {
                if (command.MessageType == "Cancel")
                {
                    await CancelAsync(command, token).ConfigureAwait(false);
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
                    case "Upload": await BeginUploadAsync(command, address, root, token).ConfigureAwait(false); break;
                    default: throw new InvalidDataException("ConditionalMutationUnavailable");
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException or FormatException or SshException or KeyNotFoundException or System.Security.Authentication.AuthenticationException or TimeoutException or System.Net.Sockets.SocketException)
            {
                await ErrorAsync(command, exception, token).ConfigureAwait(false);
            }
        }
    }

    private async Task BeginUploadAsync(AdapterControlFrame command, AdapterFileAddress address, SftpWorkerRoot root, CancellationToken token)
    {
        AdapterOperationRequest operation = AdapterProtocolJson.Decode<AdapterOperationRequest>(Encoding.UTF8.GetBytes(command.Payload.GetRawText()));
        AdapterProtocolJson.ValidateMutation(operation, requiresDestination: false);
        long length = command.Payload.GetProperty("length").GetInt64();
        Guid stream = command.Payload.GetProperty("streamId").GetGuid();
        if (length < 0 || stream == Guid.Empty || _uploads.Count >= 4) throw new InvalidDataException("UploadLimit");
        if (_uploads.Values.Any(upload => upload.Operation.OperationId == operation.OperationId)) throw new InvalidDataException("OperationInProgress");
        string fingerprint = SftpUploadOperations.Fingerprint(operation, length);
        CheckOperationBinding(operation.OperationId, fingerprint);
        await SftpUploadOperations.PrepareAsync(root, operation, length, token).ConfigureAwait(false);
        BindOperation(operation.OperationId, fingerprint);
        var lease = new AdapterTransferLease(cache);
        try
        {
            _uploads.Add(command.RequestId, new(command, operation, root,
                new(command.RequestId, arguments.InstanceId, arguments.WorkerSessionId, stream, address.RootKey, 0, length), lease));
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
        await ReplyAsync(command, "UploadReady", new { rootKey = address.RootKey, operationId = operation.OperationId, streamId = stream }, token).ConfigureAwait(false);
    }

    private void BindOperation(Guid operation, string fingerprint)
    {
        CheckOperationBinding(operation, fingerprint);
        if (_bindings.TryAdd(operation, fingerprint))
        {
            _bindingOrder.Enqueue(operation);
            if (_bindingOrder.Count > 256) _bindings.Remove(_bindingOrder.Dequeue());
        }
    }

    private void CheckOperationBinding(Guid operation, string fingerprint)
    {
        if (_bindings.TryGetValue(operation, out string? previous) && previous != fingerprint)
            throw new InvalidDataException("OperationBindingMismatch");
    }

    private async Task ReceiveAsync(AdapterBinaryChunk chunk, CancellationToken token)
    {
        if (!_uploads.TryGetValue(chunk.RequestId, out PendingUpload? upload)) throw new InvalidDataException("UnexpectedChunk");
        bool terminal = false;
        try
        {
            upload.Binding.Accept(chunk);
            await upload.Lease.Stream.WriteAsync(chunk.Data, token).ConfigureAwait(false);
            if (!upload.Binding.Completed) return;
            terminal = true;
            upload.Lease.Stream.Position = 0;
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(upload.Lease.Stream, token).ConfigureAwait(false));
            string revision = await SftpUploadOperations.UploadAsync(upload.Root, upload.Operation, upload.Lease.Stream, digest, token).ConfigureAwait(false);
            await ReplyAsync(upload.Command, "UploadComplete", new
            {
                rootKey = upload.Operation.RootKey,
                operationId = upload.Operation.OperationId,
                revision,
                contentSha256 = digest
            }, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException or FormatException or SshException or KeyNotFoundException or System.Security.Authentication.AuthenticationException or TimeoutException or System.Net.Sockets.SocketException)
        { terminal = true; await ErrorAsync(upload.Command, exception, token).ConfigureAwait(false); }
        finally
        {
            if (terminal)
            {
                _uploads.Remove(chunk.RequestId);
                await upload.Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task CancelAsync(AdapterControlFrame command, CancellationToken token)
    {
        string rootKey = command.Payload.GetProperty("rootKey").GetString() ?? throw new InvalidDataException("RootRequired");
        roots.Get(rootKey);
        Guid target = command.Payload.GetProperty("targetRequestId").GetGuid();
        if (target == Guid.Empty) throw new InvalidDataException("InvalidRequest");
        string status = "alreadyCompleted";
        if (_uploads.TryGetValue(target, out PendingUpload? upload))
        {
            if (upload.Operation.RootKey != rootKey) throw new InvalidDataException("CancelRootMismatch");
            if (command.Payload.TryGetProperty("operationId", out JsonElement operation) && operation.ValueKind != JsonValueKind.Null &&
                operation.GetGuid() != upload.Operation.OperationId) throw new InvalidDataException("CancelOperationMismatch");
            _uploads.Remove(target);
            await upload.Lease.CancelAsync().ConfigureAwait(false);
            await ErrorAsync(upload.Command, new InvalidDataException("Canceled"), token).ConfigureAwait(false);
            status = "canceled";
        }
        await ReplyAsync(command, "CancelAck", new { rootKey, targetRequestId = target, status }, token).ConfigureAwait(false);
    }

    private ValueTask ErrorAsync(AdapterControlFrame command, Exception exception, CancellationToken token)
    {
        string[] codes = ["UnknownRoot", "RootOffline", "InvalidPath", "InvalidCursor", "InvalidPageSize", "InvalidRange", "RemoteConflict",
            "SourceUnavailable", "DirectoryEnumerationIncomplete", "ConditionalMutationUnavailable", "OperationBindingMismatch", "OperationInProgress",
            "ReadOnlyRoot", "RootMutationForbidden", "ReservedPath", "UploadLimit", "Canceled", "CancelRootMismatch", "CancelOperationMismatch",
            "CrossRootMoveUnavailable", "DirectoryMoveUnavailable", "DirectoryNotEmpty"];
        string code = exception is SftpRecoveryRequiredException ? "MutationOutcomeAmbiguous" :
            exception is InvalidDataException { Message: "HostKeyRejected" } ? "HostKeyRejected" :
            exception is InvalidDataException && codes.Contains(exception.Message, StringComparer.Ordinal) ? exception.Message :
            exception is SshException or IOException or TimeoutException or System.Net.Sockets.SocketException ? "RetryableTransferFailure" : "InvalidRequest";
        string? root = command.Payload.TryGetProperty("rootKey", out JsonElement rootValue) && rootValue.ValueKind == JsonValueKind.String ? rootValue.GetString() : null;
        if (root is not null && exception is not InvalidDataException and not SftpRecoveryRequiredException &&
            exception is SshException or IOException or TimeoutException or System.Net.Sockets.SocketException)
            roots.RequireReconnect(root);
        Guid? operation = command.Payload.TryGetProperty("operationId", out JsonElement operationValue) && operationValue.ValueKind == JsonValueKind.String && operationValue.TryGetGuid(out Guid id) ? id : null;
        return ReplyAsync(command, "OperationError", new
        {
            rootKey = root,
            operationId = operation,
            code,
            recoveryRelativePath = (exception as SftpRecoveryRequiredException)?.RecoveryRelativePath
        }, token);
    }
    private ValueTask ReplyAsync(AdapterControlFrame command, string type, object payload, CancellationToken token) =>
        channel.SendAsync(type, command.RequestId, true, payload, token);
    public async ValueTask DisposeAsync()
    {
        foreach (PendingUpload upload in _uploads.Values) await upload.Lease.DisposeAsync().ConfigureAwait(false);
        _uploads.Clear();
    }

    private sealed record PendingUpload(AdapterControlFrame Command, AdapterOperationRequest Operation, SftpWorkerRoot Root,
        AdapterStreamBinding Binding, AdapterTransferLease Lease);
}
