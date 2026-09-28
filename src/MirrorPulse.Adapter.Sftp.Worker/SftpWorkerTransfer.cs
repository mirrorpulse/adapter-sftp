using MirrorPulse.Adapter.Sdk;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MirrorPulse.Adapter.Sftp.Worker;

public sealed class SftpWorkerTransfer(SftpClient client, SftpWorkerConfiguration configuration)
{
    public const int MaximumRangeBytes = 1024 * 1024;

    public async Task<byte[]> ReadRangeAsync(
        string relativePath, long offset, int length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0 or > MaximumRangeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        string path = ResolvePath(relativePath);
        if (length == 0)
        {
            return [];
        }

        await using Stream stream = client.OpenRead(path);
        stream.Position = offset;
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public async Task<string?> GetRevisionAsync(string relativePath, CancellationToken cancellationToken)
    {
        string path = ResolvePath(relativePath);
        try
        {
            var attributes = await client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return $"{attributes.Size}:{attributes.LastWriteTimeUtc.Ticks}";
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task<string> UploadAsync(
        string relativePath, string? expectedRevision, Stream content, Guid requestId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !content.CanSeek || requestId == Guid.Empty)
        {
            throw new ArgumentException("A seekable upload stream and request ID are required.");
        }

        string path = ResolvePath(relativePath);
        string? before = await GetRevisionAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(before, expectedRevision, StringComparison.Ordinal))
        {
            throw new SftpRevisionConflictException();
        }

        string stagedPath = $"{path}.mirrorpulse-upload-{requestId:N}";
        try
        {
            await client.UploadFileAsync(content, stagedPath, cancellationToken).ConfigureAwait(false);
            string? after = await GetRevisionAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(after, expectedRevision, StringComparison.Ordinal))
            {
                throw new SftpRevisionConflictException();
            }

            try
            {
                client.RenameFile(stagedPath, path, isPosix: true);
            }
            catch (NotSupportedException)
            {
                client.RenameFile(stagedPath, path);
            }
            return await GetRevisionAsync(relativePath, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("The renamed SFTP upload is missing.");
        }
        catch
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DeleteFileAsync(stagedPath, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // A disconnected server may leave a staging file for the next retry to overwrite.
            }

            throw;
        }
    }

    private string ResolvePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (relativePath.Length > 4096 || relativePath.Contains('\0') ||
            relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new InvalidDataException("The SFTP path must be a bounded relative path.");
        }

        string[] parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
        {
            throw new InvalidDataException("The SFTP path contains an unsafe segment.");
        }

        string root = configuration.Endpoint.AbsolutePath.TrimEnd('/');
        return root + "/" + string.Join('/', parts);
    }
}

public sealed class SftpRevisionConflictException : IOException
{
    public SftpRevisionConflictException()
        : base("The remote SFTP file changed before the conditional upload could complete.")
    {
    }
}

public sealed class SftpWorkerTransferProtocol(
    AdapterControlChannel channel, SftpClient client, SftpWorkerConfiguration configuration,
    Guid instanceId, Guid workerSessionId)
{
    private readonly SftpWorkerTransfer _transfer = new(client, configuration);

    public async Task HandleAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        try
        {
            switch (command.MessageType)
            {
                case "Stat":
                    {
                        string path = command.Payload.GetProperty("path").GetString()
                            ?? throw new InvalidDataException("The SFTP stat path is missing.");
                        string? revision = await _transfer.GetRevisionAsync(path, cancellationToken).ConfigureAwait(false);
                        await channel.SendAsync("StatResult", command.RequestId, true, new { revision }, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                case "ReadRange":
                    {
                        string path = command.Payload.GetProperty("path").GetString()
                            ?? throw new InvalidDataException("The SFTP read path is missing.");
                        long offset = command.Payload.GetProperty("offset").GetInt64();
                        int length = command.Payload.GetProperty("length").GetInt32();
                        byte[] bytes = await _transfer.ReadRangeAsync(path, offset, length, cancellationToken)
                            .ConfigureAwait(false);
                        Guid streamId = Guid.NewGuid();
                        await channel.SendAsync("ReadRangeReady", command.RequestId, true,
                            new { streamId, length = bytes.Length }, cancellationToken).ConfigureAwait(false);
                        await channel.SendChunkAsync(new AdapterBinaryChunk(
                            command.RequestId, instanceId, workerSessionId, streamId, offset, bytes, true),
                            cancellationToken).ConfigureAwait(false);
                        break;
                    }
                case "Upload":
                    await HandleUploadAsync(command, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException("The SFTP Worker received an unsupported command.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string code = exception switch
            {
                SftpRevisionConflictException => "RemoteConflict",
                InvalidDataException or ArgumentException or System.Text.Json.JsonException => "InvalidRequest",
                NotSupportedException => "CapabilityUnavailable",
                _ => "RetryableTransferFailure",
            };
            await channel.SendAsync("OperationError", command.RequestId, true, new { code }, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleUploadAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString()
            ?? throw new InvalidDataException("The SFTP upload path is missing.");
        string? expectedRevision = command.Payload.GetProperty("expectedRevision").GetString();
        long length = command.Payload.GetProperty("length").GetInt64();
        Guid streamId = command.Payload.GetProperty("streamId").GetGuid();
        if (length < 0 || streamId == Guid.Empty)
        {
            throw new InvalidDataException("The SFTP upload length or stream ID is invalid.");
        }

        string cache = Environment.GetEnvironmentVariable("MP_TRANSFER_CACHE_DIR")
            ?? throw new InvalidDataException("The Host did not provide a transfer cache directory.");
        Directory.CreateDirectory(cache);
        string stagedFile = Path.Combine(cache, $"sftp-{command.RequestId:N}.tmp");
        await channel.SendAsync("UploadReady", command.RequestId, true, new { streamId }, cancellationToken)
            .ConfigureAwait(false);
        string revision;
        try
        {
            await using (var output = new FileStream(stagedFile, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                long received = 0;
                while (true)
                {
                    AdapterBinaryChunk chunk = await channel.ReadChunkAsync(cancellationToken).ConfigureAwait(false);
                    if (chunk.RequestId != command.RequestId || chunk.StreamId != streamId ||
                        chunk.Offset != received || chunk.Data.Length > length - received)
                    {
                        throw new InvalidDataException("The SFTP upload chunk is out of order or exceeds the declared size.");
                    }

                    await output.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
                    received += chunk.Data.Length;
                    if (chunk.EndOfStream)
                    {
                        if (received != length)
                        {
                            throw new InvalidDataException("The SFTP upload ended before its declared size.");
                        }

                        break;
                    }
                }
            }

            await using var input = new FileStream(stagedFile, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            revision = await _transfer.UploadAsync(path, expectedRevision, input,
                command.RequestId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(stagedFile);
        }

        await channel.SendAsync("UploadComplete", command.RequestId, true,
            new { revision }, cancellationToken).ConfigureAwait(false);
    }
}
