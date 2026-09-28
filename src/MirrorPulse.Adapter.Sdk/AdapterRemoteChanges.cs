namespace MirrorPulse.Adapter.Sdk;

public enum AdapterRemoteChangeKind
{
    FileUpsert,
    DirectoryUpsert,
    MetadataUpdate,
    Move,
    Delete,
}

public enum AdapterRemoteItemKind
{
    File,
    Directory,
}

public sealed record AdapterRemoteMetadata(
    FileAttributes Attributes,
    DateTimeOffset? CreationTime = null,
    DateTimeOffset? LastAccessTime = null,
    DateTimeOffset? LastWriteTime = null,
    DateTimeOffset? ChangeTime = null);

/// <summary>One Adapter-owned change, with paths relative to its declared first-level root.</summary>
public sealed record AdapterRemoteChange(
    string ChangeId,
    AdapterRemoteChangeKind Kind,
    string RootKey,
    string RemoteId,
    string RemoteRevision,
    AdapterRemoteItemKind ItemKind,
    string RelativePath,
    string? PreviousRemoteRevision = null,
    string? PreviousRootKey = null,
    string? PreviousRelativePath = null,
    long? Length = null,
    AdapterRemoteMetadata? Metadata = null,
    ReadOnlyMemory<byte> CursorAfter = default);

public sealed record AdapterRemoteChangeBatch(
    string BatchId,
    ReadOnlyMemory<byte> InitialCursor,
    IReadOnlyList<AdapterRemoteChange> Changes,
    ReadOnlyMemory<byte> FinalCursor);
