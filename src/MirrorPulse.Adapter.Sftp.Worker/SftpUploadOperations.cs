using System.Security.Cryptography;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet.Common;

namespace MirrorPulse.Adapter.Sftp.Worker;

internal sealed class SftpRecoveryRequiredException(string recoveryRelativePath) : IOException
{
    public string RecoveryRelativePath { get; } = recoveryRelativePath;
}

/// <summary>Recoverable optimistic publication; the SFTP server does not supply version CAS.</summary>
internal static class SftpUploadOperations
{
    internal sealed record Receipt(string Fingerprint, string Digest, string? OriginalDigest, string Phase);
    private static readonly string[] PrivatePrefixes = [".mp-stage-", ".mp-recovery-", ".mp-journal-"];

    public static bool IsPrivateName(string name) =>
        PrivatePrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.Ordinal) && name.Length == prefix.Length + 32 &&
            Guid.TryParseExact(name[prefix.Length..], "N", out _));

    public static void ValidateUserPath(string path)
    {
        if (path.Length == 0) throw new InvalidDataException("RootMutationForbidden");
        if (path.Split('/').Any(IsPrivateName)) throw new InvalidDataException("ReservedPath");
    }

    public static string Fingerprint(AdapterOperationRequest operation, long length) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new { type = "Upload", operation, length })));

    public static string Sibling(string path, string kind, Guid operation)
    {
        int separator = path.LastIndexOf('/');
        return (separator < 0 ? "" : path[..(separator + 1)]) + ".mp-" + kind + "-" + operation.ToString("N");
    }

    public static async Task PrepareAsync(SftpWorkerRoot root, AdapterOperationRequest operation, long length, CancellationToken token)
    {
        ValidateUserPath(operation.Path);
        if (!root.AllowsMutations) throw new InvalidDataException("ReadOnlyRoot");
        if (operation.IsDirectory) throw new InvalidDataException("InvalidRequest");
        Receipt? receipt = await ReadReceiptAsync(root, Sibling(operation.Path, "journal", operation.OperationId), token).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != Fingerprint(operation, length)) throw new InvalidDataException("OperationBindingMismatch");
            return; // The received bytes must still match the persisted operation digest.
        }
        string? current = await SftpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
        AdapterMutationPreconditions conditions = operation.Preconditions ?? new();
        if (current != conditions.ExpectedRevision || (conditions.DestinationMustBeAbsent && current is not null))
            throw new InvalidDataException("RemoteConflict");
    }

    public static async Task<string> UploadAsync(SftpWorkerRoot root, AdapterOperationRequest operation,
        Stream content, string digest, CancellationToken token)
    {
        string stage = Sibling(operation.Path, "stage", operation.OperationId);
        string backup = Sibling(operation.Path, "recovery", operation.OperationId);
        string journal = Sibling(operation.Path, "journal", operation.OperationId);
        string fingerprint = Fingerprint(operation, content.Length);
        Receipt? receipt = await ReadReceiptAsync(root, journal, token).ConfigureAwait(false);
        if (receipt is not null && (receipt.Fingerprint != fingerprint || receipt.Digest != digest))
            throw new InvalidDataException("OperationBindingMismatch");
        if (receipt?.Phase == "Committed")
        {
            if (await DigestAsync(root, operation.Path, token).ConfigureAwait(false) != digest)
                throw new InvalidDataException("RemoteConflict");
            return await RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
        }

        if (receipt is null)
        {
            await PrepareAsync(root, operation, content.Length, token).ConfigureAwait(false);
            string? original = await DigestAsync(root, operation.Path, token).ConfigureAwait(false);
            receipt = new(fingerprint, digest, original, "Prepared");
            await WriteReceiptAsync(root, journal, receipt, token).ConfigureAwait(false);
        }
        string recovery = receipt.OriginalDigest is null ? journal : backup;

        try
        {
            if (receipt.Phase == "Prepared")
            {
                string? preserved = await DigestAsync(root, backup, token).ConfigureAwait(false);
                if (preserved is not null)
                {
                    if (preserved != receipt.OriginalDigest) throw new SftpRecoveryRequiredException(backup);
                    receipt = receipt with { Phase = "Preserved" };
                }
                else
                {
                    content.Position = 0;
                    await root.Client.UploadFileAsync(content,
                        SftpPathPolicy.Resolve(root.Configuration.Endpoint, stage), token).ConfigureAwait(false);
                    if (await DigestAsync(root, stage, token).ConfigureAwait(false) != digest)
                        throw new IOException("StagingVerificationFailed");
                    AdapterMutationPreconditions conditions = operation.Preconditions ?? new();
                    string? current = await SftpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
                    if (current != conditions.ExpectedRevision ||
                        await DigestAsync(root, operation.Path, token).ConfigureAwait(false) != receipt.OriginalDigest)
                        throw new InvalidDataException("RemoteConflict");
                    if (receipt.OriginalDigest is not null)
                    {
                        await root.Client.RenameFileAsync(SftpPathPolicy.Resolve(root.Configuration.Endpoint, operation.Path),
                            SftpPathPolicy.Resolve(root.Configuration.Endpoint, backup), token).ConfigureAwait(false);
                        if (await DigestAsync(root, backup, token).ConfigureAwait(false) != receipt.OriginalDigest)
                            throw new SftpRecoveryRequiredException(backup);
                    }
                    receipt = receipt with { Phase = "Preserved" };
                }
                await WriteReceiptAsync(root, journal, receipt, token).ConfigureAwait(false);
            }

            if (receipt.OriginalDigest is not null &&
                await DigestAsync(root, backup, token).ConfigureAwait(false) != receipt.OriginalDigest)
                throw new SftpRecoveryRequiredException(backup);
            string? target = await DigestAsync(root, operation.Path, token).ConfigureAwait(false);
            if (target is not null)
            {
                if (target != digest) throw new SftpRecoveryRequiredException(recovery);
                // A lost acknowledgement is reconciled without publishing a second time.
            }
            else
            {
                if (await DigestAsync(root, stage, token).ConfigureAwait(false) != digest)
                    throw new SftpRecoveryRequiredException(recovery);
                await root.Client.RenameFileAsync(SftpPathPolicy.Resolve(root.Configuration.Endpoint, stage),
                    SftpPathPolicy.Resolve(root.Configuration.Endpoint, operation.Path), token).ConfigureAwait(false);
                if (await DigestAsync(root, operation.Path, token).ConfigureAwait(false) != digest)
                    throw new SftpRecoveryRequiredException(recovery);
            }
            await WriteReceiptAsync(root, journal, receipt with { Phase = "Committed" }, token).ConfigureAwait(false);
            return await RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SshException or IOException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            if (exception is not InvalidDataException and not SftpRecoveryRequiredException)
                root.RequireReconnect();
            // No automatic rollback or overwrite after a potentially destructive remote command.
            if (receipt.Phase == "Preserved" || await HasBackupAsync(root, backup).ConfigureAwait(false))
                throw new SftpRecoveryRequiredException(recovery);
            throw;
        }
    }

    private static async Task<bool> HasBackupAsync(SftpWorkerRoot root, string backup)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await SftpOperations.RevisionAsync(root, backup, timeout.Token).ConfigureAwait(false) is not null; }
        catch (Exception exception) when (exception is IOException or SshException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException) { return true; }
    }

    private static async Task<string> RevisionAsync(SftpWorkerRoot root, string relative, CancellationToken token) =>
        await SftpOperations.RevisionAsync(root, relative, token).ConfigureAwait(false) ?? throw new SftpRecoveryRequiredException(relative);

    public static async Task<string?> DigestAsync(SftpWorkerRoot root, string relative, CancellationToken token)
    {
        string? before = await SftpOperations.RevisionAsync(root, relative, token).ConfigureAwait(false);
        if (before is null) return null;
        string digest;
        await using (Stream input = await root.Client.OpenAsync(SftpPathPolicy.Resolve(root.Configuration.Endpoint, relative),
            FileMode.Open, FileAccess.Read, token).ConfigureAwait(false))
        {
            digest = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
        }
        if (before != await SftpOperations.RevisionAsync(root, relative, token).ConfigureAwait(false))
            throw new InvalidDataException("RemoteConflict");
        return digest;
    }

    public static async Task<Receipt?> ReadReceiptAsync(SftpWorkerRoot root, string relative, CancellationToken token)
    {
        if (await SftpOperations.RevisionAsync(root, relative, token).ConfigureAwait(false) is null) return null;
        try
        {
            byte[] bytes = new byte[64 * 1024 + 1];
            int count = 0;
            await using Stream input = await root.Client.OpenAsync(SftpPathPolicy.Resolve(root.Configuration.Endpoint, relative),
                FileMode.Open, FileAccess.Read, token).ConfigureAwait(false);
            while (count < bytes.Length)
            {
                int read = await input.ReadAsync(bytes.AsMemory(count), token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            if (count > 64 * 1024) throw new InvalidDataException("InvalidReceipt");
            Receipt receipt = AdapterProtocolJson.Decode<Receipt>(bytes.AsSpan(0, count));
            if (!IsDigest(receipt.Fingerprint) || !IsDigest(receipt.Digest) ||
                receipt.Phase is not ("Prepared" or "Preserved" or "Committed") ||
                (receipt.OriginalDigest is not null && !IsDigest(receipt.OriginalDigest)))
                throw new InvalidDataException("InvalidReceipt");
            return receipt;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
        { throw new SftpRecoveryRequiredException(relative); }
    }

    private static bool IsDigest(string? digest) => digest is { Length: 64 } && digest.All(Uri.IsHexDigit);

    public static async Task WriteReceiptAsync(SftpWorkerRoot root, string relative, Receipt receipt, CancellationToken token)
    {
        byte[] bytes = AdapterProtocolJson.Encode(receipt);
        using var input = new MemoryStream(bytes, writable: false);
        await root.Client.UploadFileAsync(input, SftpPathPolicy.Resolve(root.Configuration.Endpoint, relative), token).ConfigureAwait(false);
        if (await DigestAsync(root, relative, token).ConfigureAwait(false) != Convert.ToHexString(SHA256.HashData(bytes)))
            throw new SftpRecoveryRequiredException(relative);
    }
}
