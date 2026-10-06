using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace MirrorPulse.Adapter.Sftp.Worker;

internal static class SftpOperations
{
    private static string Revision(ISftpFile item) => "sftp-metadata:" + (item.IsDirectory ? "directory" : "file") + ":" +
        item.Length.ToString(CultureInfo.InvariantCulture) + ":" + item.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    private static async Task<ISftpFile[]> ReadListingAsync(SftpWorkerRoot root, string path, CancellationToken token)
    {
        await root.EnsureConnectedAsync(token).ConfigureAwait(false);
        ISftpFile directory = await root.Client.GetAsync(path, token).ConfigureAwait(false);
        if (!directory.IsDirectory || directory.IsSymbolicLink || directory.FullName.Length is < 1 or > 8192)
            throw new InvalidDataException("DirectoryEnumerationIncomplete");
        // ListDirectoryAsync uses the server's canonical parent, which can differ
        // from the requested path. Preserve that exact prefix when checking raw names.
        string prefix = directory.FullName.EndsWith('/') ? directory.FullName : directory.FullName + '/';
        var items = new List<ISftpFile>();
        await foreach (ISftpFile item in root.Client.ListDirectoryAsync(path, token).ConfigureAwait(false))
        {
            // SSH.NET exposes Name as the last path segment. Check FullName too
            // so a hostile raw directory entry cannot be normalized into an alias.
            if (item.FullName != prefix + item.Name)
                throw new InvalidDataException("DirectoryEnumerationIncomplete");
            if (item.Name is "." or "..") continue;
            if (items.Count >= 8192 || item.Name.Length is < 1 or > 4096 || item.Name.Contains('/') ||
                item.Name.Contains('\\') || item.Name.Contains(':') || item.Name.Any(char.IsControl) ||
                item.IsSymbolicLink || (!item.IsDirectory && !item.IsRegularFile))
                throw new InvalidDataException("DirectoryEnumerationIncomplete");
            items.Add(item);
        }
        if (items.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new InvalidDataException("DirectoryEnumerationIncomplete");
        return items.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private static async Task<string> DirectoryAsync(SftpWorkerRoot root, string relative, CancellationToken token)
    {
        string current = SftpPathPolicy.Resolve(root.Configuration.Endpoint, "");
        _ = SftpPathPolicy.Resolve(root.Configuration.Endpoint, relative);
        if (relative.Length == 0) return current;
        foreach (string part in relative.Split('/'))
        {
            ISftpFile[] items = await ReadListingAsync(root, current, token).ConfigureAwait(false);
            ISftpFile item = items.SingleOrDefault(item => item.Name == part) ?? throw new InvalidDataException("SourceUnavailable");
            if (!item.IsDirectory) throw new InvalidDataException("SourceUnavailable");
            current = current.TrimEnd('/') + "/" + part;
        }
        return current;
    }

    public static async Task<ISftpFile[]> ReadDirectoryAsync(SftpWorkerRoot root, string relative, CancellationToken token) =>
        await ReadListingAsync(root, await DirectoryAsync(root, relative, token).ConfigureAwait(false), token).ConfigureAwait(false);

    public static async Task<string?> RevisionAsync(SftpWorkerRoot root, string relative, CancellationToken token)
    {
        _ = SftpPathPolicy.Resolve(root.Configuration.Endpoint, relative);
        if (relative.Length == 0)
        {
            ISftpFile[] children = await ReadListingAsync(root, await DirectoryAsync(root, "", token).ConfigureAwait(false), token).ConfigureAwait(false);
            return "sftp-directory:" + Convert.ToHexString(SHA256.HashData(AdapterProtocolJson.Encode(
                children.Where(item => !SftpUploadOperations.IsPrivateName(item.Name)).Select(item => new { item.Name, revision = Revision(item) }))));
        }
        int separator = relative.LastIndexOf('/');
        string parent = separator < 0 ? "" : relative[..separator];
        string name = relative[(separator + 1)..];
        try
        {
            string directory = await DirectoryAsync(root, parent, token).ConfigureAwait(false);
            ISftpFile[] children = await ReadListingAsync(root, directory, token).ConfigureAwait(false);
            ISftpFile? item = children.SingleOrDefault(item => item.Name == name);
            return item is null ? null : Revision(item);
        }
        catch (SftpPathNotFoundException) { return null; }
    }

    public static async Task<object> ListAsync(SftpWorkerRoot root, AdapterFileAddress address, int size, string? cursor, CancellationToken token)
    {
        if (size is < 1 or > 512) throw new InvalidDataException("InvalidPageSize");
        int offset = 0;
        if (cursor is not null)
        {
            if (cursor.Length > 8192) throw new InvalidDataException("InvalidCursor");
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\0');
            if (parts.Length != 3 || parts[0] != address.RootKey || parts[1] != address.Path ||
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)
                throw new InvalidDataException("InvalidCursor");
        }
        string directory = await DirectoryAsync(root, address.Path, token).ConfigureAwait(false);
        ISftpFile[] children = await ReadListingAsync(root, directory, token).ConfigureAwait(false);
        children = children.Where(item => !SftpUploadOperations.IsPrivateName(item.Name)).ToArray();
        if (offset > children.Length) throw new InvalidDataException("InvalidCursor");
        var entries = new List<object>();
        foreach (ISftpFile item in children.Skip(offset).Take(size))
        {
            string relative = address.Path.Length == 0 ? item.Name : address.Path + "/" + item.Name;
            entries.Add(new
            {
                remoteId = relative,
                relativePath = relative,
                remoteRevision = Revision(item),
                itemKind = item.IsDirectory ? "Directory" : "File",
                length = item.IsDirectory ? (long?)null : item.Length,
                creationTime = new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
                lastWriteTime = new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
                isDeleted = false
            });
        }
        int next = offset + entries.Count;
        bool complete = next >= children.Length;
        string? nextCursor = complete ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(address.RootKey + "\0" + address.Path + "\0" + next.ToString(CultureInfo.InvariantCulture)));
        return new { rootKey = address.RootKey, entries, cursor = nextCursor, isComplete = complete };
    }

    public static async Task<byte[]> ReadAsync(SftpWorkerRoot root, AdapterFileAddress address, long offset, int length, string? expected, CancellationToken token)
    {
        string? before = await RevisionAsync(root, address.Path, token).ConfigureAwait(false);
        if (before is null) throw new InvalidDataException("SourceUnavailable");
        if (expected is not null && expected != before) throw new InvalidDataException("RemoteConflict");
        byte[] bytes = new byte[length];
        await using (Stream stream = root.Client.OpenRead(SftpPathPolicy.Resolve(root.Configuration.Endpoint, address.Path)))
        {
            stream.Position = offset;
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        }
        if (before != await RevisionAsync(root, address.Path, token).ConfigureAwait(false)) throw new InvalidDataException("RemoteConflict");
        return bytes;
    }
}
