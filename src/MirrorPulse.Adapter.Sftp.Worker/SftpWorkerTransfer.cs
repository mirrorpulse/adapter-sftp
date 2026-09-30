using System.Globalization;
using System.Text;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

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
            throw new SftpRevisionConflictException(expectedRevision, before);
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

    public async Task<string?> DeleteAsync(
        string relativePath,
        string? expectedRevision,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        if (isDirectory)
        {
            throw new NotSupportedException("The SFTP Worker does not delete directories through the mutation protocol.");
        }

        string? current = await GetRevisionAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        if (!string.Equals(current, expectedRevision, StringComparison.Ordinal))
        {
            throw new SftpRevisionConflictException(expectedRevision, current);
        }

        await client.DeleteFileAsync(ResolvePath(relativePath), cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<string> MoveAsync(
        string sourcePath,
        string destinationPath,
        string? expectedRevision,
        bool isDirectory,
        CancellationToken cancellationToken)
    {
        if (isDirectory)
        {
            throw new NotSupportedException("The SFTP Worker does not move directories through the mutation protocol.");
        }

        string? current = await GetRevisionAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, expectedRevision, StringComparison.Ordinal))
        {
            throw new SftpRevisionConflictException(expectedRevision, current);
        }

        try
        {
            client.RenameFile(ResolvePath(sourcePath), ResolvePath(destinationPath), isPosix: true);
        }
        catch (NotSupportedException)
        {
            client.RenameFile(ResolvePath(sourcePath), ResolvePath(destinationPath));
        }

        return await GetRevisionAsync(destinationPath, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The moved SFTP file is missing.");
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

    public async Task<SftpWorkerDirectoryPage> ReadDirectoryPageAsync(
        string relativePath,
        ReadOnlyMemory<byte> cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        int offset = ParseCursor(cursor);
        string directory = string.IsNullOrEmpty(relativePath)
            ? configuration.Endpoint.AbsolutePath.TrimEnd('/')
            : ResolvePath(relativePath);
        ISftpFile[] children = client.ListDirectory(directory)
            .Where(item => item.Name is not "." and not "..")
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        if (offset > children.Length)
        {
            throw new InvalidDataException("The SFTP directory cursor is past the directory.");
        }

        var entries = new List<SftpWorkerDirectoryEntry>(Math.Min(pageSize, children.Length - offset));
        string parent = relativePath.Trim('/');
        foreach (ISftpFile item in children.Skip(offset).Take(pageSize))
        {
            string childPath = parent.Length == 0 ? item.Name : parent + "/" + item.Name;
            DateTimeOffset modified = new(item.LastWriteTimeUtc, TimeSpan.Zero);
            string revision = $"{item.Length}:{modified.Ticks.ToString(CultureInfo.InvariantCulture)}";
            entries.Add(new(item.FullName, revision, item.IsDirectory ? "Directory" : "File", childPath,
                item.IsDirectory ? null : item.Length, new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
                modified, false));
        }

        int next = offset + entries.Count;
        bool complete = next >= children.Length;
        return new(entries, complete ? [] : Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture)), complete);
    }

    private static int ParseCursor(ReadOnlyMemory<byte> cursor)
    {
        if (cursor.IsEmpty) return 0;
        return int.TryParse(Encoding.UTF8.GetString(cursor.Span), NumberStyles.None,
            CultureInfo.InvariantCulture, out int offset) && offset >= 0
            ? offset : throw new InvalidDataException("The SFTP directory cursor is invalid.");
    }
}

public sealed record SftpWorkerDirectoryPage(
    IReadOnlyList<SftpWorkerDirectoryEntry> Entries,
    ReadOnlyMemory<byte> ContinuationCursor,
    bool IsComplete);

public sealed record SftpWorkerDirectoryEntry(
    string RemoteId,
    string RemoteRevision,
    string ItemKind,
    string RelativePath,
    long? Length,
    DateTimeOffset? CreationTime,
    DateTimeOffset? LastWriteTime,
    bool IsDeleted);

public sealed class SftpRevisionConflictException : IOException
{
    public SftpRevisionConflictException(string? expectedRevision = null, string? actualRevision = null)
        : base("The remote SFTP file changed before the conditional upload could complete.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string? ExpectedRevision { get; }

    public string? ActualRevision { get; }
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
                case "Delete":
                    await HandleDeleteAsync(command, cancellationToken).ConfigureAwait(false);
                    break;
                case "Move":
                    await HandleMoveAsync(command, cancellationToken).ConfigureAwait(false);
                    break;
                case "List":
                    await HandleListAsync(command, cancellationToken).ConfigureAwait(false);
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
            if (exception is SftpRevisionConflictException conflict)
            {
                await channel.SendAsync("OperationError", command.RequestId, true,
                    new { code, expectedRevision = conflict.ExpectedRevision, actualRevision = conflict.ActualRevision },
                    CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await channel.SendAsync("OperationError", command.RequestId, true, new { code },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleDeleteAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString()
            ?? throw new InvalidDataException("The SFTP delete path is missing.");
        string? expectedRevision = command.Payload.TryGetProperty("expectedRevision", out var expected) &&
            expected.ValueKind is not System.Text.Json.JsonValueKind.Null ? expected.GetString() : null;
        bool isDirectory = command.Payload.GetProperty("isDirectory").GetBoolean();
        string? revision = await _transfer.DeleteAsync(path, expectedRevision, isDirectory,
            cancellationToken).ConfigureAwait(false);
        await channel.SendAsync("MutationComplete", command.RequestId, true, new { revision },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleMoveAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string sourcePath = command.Payload.GetProperty("sourcePath").GetString()
            ?? throw new InvalidDataException("The SFTP move source path is missing.");
        string destinationPath = command.Payload.GetProperty("destinationPath").GetString()
            ?? throw new InvalidDataException("The SFTP move destination path is missing.");
        string? expectedRevision = command.Payload.TryGetProperty("expectedRevision", out var expected) &&
            expected.ValueKind is not System.Text.Json.JsonValueKind.Null ? expected.GetString() : null;
        bool isDirectory = command.Payload.GetProperty("isDirectory").GetBoolean();
        string revision = await _transfer.MoveAsync(sourcePath, destinationPath, expectedRevision,
            isDirectory, cancellationToken).ConfigureAwait(false);
        await channel.SendAsync("MutationComplete", command.RequestId, true, new { revision },
            cancellationToken).ConfigureAwait(false);
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
                    await channel.SendAsync("TransferProgress", command.RequestId, false,
                        new
                        {
                            operation = "upload",
                            bytesTransferred = received,
                            totalBytes = length,
                            phase = "Transferring",
                        }, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleListAsync(AdapterControlFrame command, CancellationToken cancellationToken)
    {
        string path = command.Payload.GetProperty("path").GetString() ?? string.Empty;
        int pageSize = command.Payload.GetProperty("pageSize").GetInt32();
        byte[] cursor = command.Payload.TryGetProperty("cursor", out var cursorElement) &&
            cursorElement.ValueKind is not System.Text.Json.JsonValueKind.Null &&
            !string.IsNullOrEmpty(cursorElement.GetString())
            ? Convert.FromBase64String(cursorElement.GetString()!) : [];
        SftpWorkerDirectoryPage page = await _transfer.ReadDirectoryPageAsync(path, cursor,
            pageSize, cancellationToken).ConfigureAwait(false);
        await channel.SendAsync("DirectoryPage", command.RequestId, true, new
        {
            entries = page.Entries,
            cursor = page.IsComplete ? null : Convert.ToBase64String(page.ContinuationCursor.Span),
            isComplete = page.IsComplete,
        }, cancellationToken).ConfigureAwait(false);
    }
}
