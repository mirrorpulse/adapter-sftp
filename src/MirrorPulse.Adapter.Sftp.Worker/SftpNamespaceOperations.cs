using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet.Common;

namespace MirrorPulse.Adapter.Sftp.Worker;

internal static class SftpNamespaceOperations
{
    public static string Fingerprint(string type, AdapterOperationRequest operation) =>
        Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(new { type, operation })));

    public static async Task<string?> MutateAsync(string type, SftpWorkerRoot root,
        AdapterOperationRequest operation, string cache, Action bindOperation, CancellationToken token)
    {
        SftpUploadOperations.ValidateUserPath(operation.Path);
        if (!root.AllowsMutations) throw new InvalidDataException("ReadOnlyRoot");
        if (type == "Move")
        {
            if (operation.DestinationRootKey != operation.RootKey) throw new InvalidDataException("CrossRootMoveUnavailable");
            SftpUploadOperations.ValidateUserPath(operation.DestinationPath!);
            _ = SftpPathPolicy.Resolve(root.Configuration.Endpoint, operation.DestinationPath!);
            if (operation.Path == operation.DestinationPath) throw new InvalidDataException("InvalidRequest");
            if (operation.IsDirectory) throw new InvalidDataException("DirectoryMoveUnavailable");
        }
        string journal = SftpUploadOperations.Sibling(operation.Path, "journal", operation.OperationId);
        string backup = SftpUploadOperations.Sibling(operation.Path, "recovery", operation.OperationId);
        string fingerprint = Fingerprint(type, operation);
        SftpUploadOperations.Receipt? receipt = await SftpUploadOperations.ReadReceiptAsync(root, journal, token).ConfigureAwait(false);
        if (receipt is not null && receipt.Fingerprint != fingerprint) throw new InvalidDataException("OperationBindingMismatch");
        string? current = await SftpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
        if (receipt?.Phase == "Committed")
        {
            bindOperation();
            return await ReconcileAsync(type, root, operation, receipt.Digest, token).ConfigureAwait(false);
        }
        if (receipt is null)
        {
            AdapterMutationPreconditions conditions = operation.Preconditions ?? new();
            if (type == "CreateDirectory")
            {
                if (current is not null && (conditions.DestinationMustBeAbsent || !IsDirectory(current)))
                    throw new InvalidDataException("RemoteConflict");
            }
            else if (current is null || current != conditions.ExpectedRevision || IsDirectory(current) != operation.IsDirectory)
                throw new InvalidDataException("RemoteConflict");
            if (type == "Move" && await SftpOperations.RevisionAsync(root, operation.DestinationPath!, token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("RemoteConflict");
            if (type == "Delete" && operation.IsDirectory) await RequireEmptyAsync(root, operation.Path, token).ConfigureAwait(false);
            string digest = operation.IsDirectory ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current ?? ""))) :
                await SftpUploadOperations.DigestAsync(root, operation.Path, token).ConfigureAwait(false) ?? throw new InvalidDataException("RemoteConflict");
            receipt = new(fingerprint, digest, null, "Prepared");
            await SftpUploadOperations.WriteReceiptAsync(root, journal, receipt, token).ConfigureAwait(false);
        }
        bindOperation();
        try
        {
            if (operation.IsDirectory)
            {
                if (type == "CreateDirectory")
                {
                    if (current is null) await root.Client.CreateDirectoryAsync(Resolve(root, operation.Path), token).ConfigureAwait(false);
                }
                else if (current is not null)
                {
                    if (current != operation.Preconditions?.ExpectedRevision || !IsDirectory(current))
                        throw new InvalidDataException("RemoteConflict");
                    await RequireEmptyAsync(root, operation.Path, token).ConfigureAwait(false);
                    await root.Client.DeleteDirectoryAsync(Resolve(root, operation.Path), token).ConfigureAwait(false);
                }
            }
            else
            {
                string? preserved = await SftpUploadOperations.DigestAsync(root, backup, token).ConfigureAwait(false);
                if (type == "Delete")
                {
                    if (preserved is not null)
                    {
                        if (preserved != receipt.Digest || current is not null) throw new SftpRecoveryRequiredException(backup);
                    }
                    else
                    {
                        await RequireSourceAsync(root, operation, receipt.Digest, token).ConfigureAwait(false);
                        await root.Client.RenameFileAsync(Resolve(root, operation.Path), Resolve(root, backup), token).ConfigureAwait(false);
                    }
                    if (await SftpUploadOperations.DigestAsync(root, backup, token).ConfigureAwait(false) != receipt.Digest)
                        throw new SftpRecoveryRequiredException(backup);
                }
                else
                {
                    if (preserved is null)
                    {
                        await RequireSourceAsync(root, operation, receipt.Digest, token).ConfigureAwait(false);
                        await using var lease = new AdapterTransferLease(cache);
                        await root.Client.DownloadFileAsync(Resolve(root, operation.Path), lease.Stream, token).ConfigureAwait(false);
                        lease.Stream.Position = 0;
                        if (Convert.ToHexString(await SHA256.HashDataAsync(lease.Stream, token).ConfigureAwait(false)) != receipt.Digest)
                            throw new InvalidDataException("RemoteConflict");
                        lease.Stream.Position = 0;
                        await root.Client.UploadFileAsync(lease.Stream, Resolve(root, backup), canOverride: false,
                            uploadProgress: null, cancellationToken: token).ConfigureAwait(false);
                    }
                    if (await SftpUploadOperations.DigestAsync(root, backup, token).ConfigureAwait(false) != receipt.Digest)
                        throw new SftpRecoveryRequiredException(backup);
                    if (current is not null)
                    {
                        await RequireSourceAsync(root, operation, receipt.Digest, token).ConfigureAwait(false);
                        if (await SftpOperations.RevisionAsync(root, operation.DestinationPath!, token).ConfigureAwait(false) is not null)
                            throw new InvalidDataException("RemoteConflict");
                        await root.Client.RenameFileAsync(Resolve(root, operation.Path), Resolve(root, operation.DestinationPath!), token).ConfigureAwait(false);
                    }
                }
            }
            string? revision = await ReconcileAsync(type, root, operation, receipt.Digest, token).ConfigureAwait(false);
            await SftpUploadOperations.WriteReceiptAsync(root, journal, receipt with { Phase = "Committed" }, token).ConfigureAwait(false);
            return revision;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is SshException or IOException or OperationCanceledException or TimeoutException or System.Net.Sockets.SocketException)
        {
            if (exception is not SftpRecoveryRequiredException)
                root.RequireReconnect();
            throw new SftpRecoveryRequiredException(operation.IsDirectory ? journal : backup);
        }
    }

    private static async Task RequireSourceAsync(SftpWorkerRoot root, AdapterOperationRequest operation, string digest, CancellationToken token)
    {
        if (await SftpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false) != operation.Preconditions?.ExpectedRevision ||
            await SftpUploadOperations.DigestAsync(root, operation.Path, token).ConfigureAwait(false) != digest)
            throw new InvalidDataException("RemoteConflict");
    }

    private static async Task<string?> ReconcileAsync(string type, SftpWorkerRoot root, AdapterOperationRequest operation, string digest, CancellationToken token)
    {
        string? source = await SftpOperations.RevisionAsync(root, operation.Path, token).ConfigureAwait(false);
        if (type == "CreateDirectory")
        {
            if (source is null || !IsDirectory(source)) throw new InvalidDataException("RemoteConflict");
            return source;
        }
        if (source is not null) throw new InvalidDataException("RemoteConflict");
        if (type == "Delete") return null;
        if (await SftpUploadOperations.DigestAsync(root, operation.DestinationPath!, token).ConfigureAwait(false) != digest)
            throw new InvalidDataException("RemoteConflict");
        return await SftpOperations.RevisionAsync(root, operation.DestinationPath!, token).ConfigureAwait(false);
    }

    private static async Task RequireEmptyAsync(SftpWorkerRoot root, string relative, CancellationToken token)
    {
        if ((await SftpOperations.ReadDirectoryAsync(root, relative, token).ConfigureAwait(false)).Length != 0)
            throw new InvalidDataException("DirectoryNotEmpty");
    }
    private static bool IsDirectory(string revision) => revision.StartsWith("sftp-metadata:directory:", StringComparison.Ordinal);
    private static string Resolve(SftpWorkerRoot root, string relative) => SftpPathPolicy.Resolve(root.Configuration.Endpoint, relative);
}
