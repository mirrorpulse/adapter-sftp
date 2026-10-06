using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Adapter.Sftp.Worker.Tests;

[TestClass]
public sealed class SftpWorkerProtocolTests
{
    private static readonly string[] EnabledRoots = ["left", "right"];
    private static readonly string[] FirstRoot = ["left"];
    [TestMethod]
    public async Task AHostileServerEntryCannotBeNormalizedIntoAnAuthorizedAlias()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        foreach (string name in new[] { "../escape.txt", "nested/escape.txt", "/escape.txt", "../..", "nested/." })
        {
            await File.WriteAllTextAsync(Path.Combine(session.Left.Storage, ".fixture-listing.json"),
                System.Text.Json.JsonSerializer.Serialize(new { name }));
            AdapterControlFrame refused = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 512 });
            Assert.AreEqual("OperationError", refused.MessageType);
            Assert.AreEqual("DirectoryEnumerationIncomplete", refused.Payload.GetProperty("code").GetString());
        }
        Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
    }

    [TestMethod]
    public async Task TwoSourcesKeepCredentialsNamesVersionsAndRangesBoundToTheirRoots()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(session.Left.Storage, ".fixture-canonical-parent"), "enabled");
        CollectionAssert.AreEqual(EnabledRoots, session.CredentialRoots);
        Assert.IsEmpty(session.ChallengeRoots);
        foreach (string root in new[] { "left", "right" })
        {
            AdapterControlFrame page = await session.RequestAsync("List", new { rootKey = root, path = "", pageSize = 1 });
            Assert.AreEqual("DirectoryPage", page.MessageType);
            var item = page.Payload.GetProperty("entries")[0];
            Assert.AreEqual("same.txt", item.GetProperty("remoteId").GetString());
            Assert.AreEqual(root.Length, item.GetProperty("length").GetInt32());
            AdapterControlFrame stat = await session.RequestAsync("Stat", new { rootKey = root, path = "same.txt" });
            Assert.AreEqual(stat.Payload.GetProperty("revision").GetString(), item.GetProperty("remoteRevision").GetString());
            Assert.AreEqual(root, Encoding.UTF8.GetString(await session.ReadRangeAsync(root, "same.txt", root.Length)));
        }
    }

    [TestMethod]
    public async Task UnknownHostKeysRequireAnIndependentRootBoundApproval()
    {
        await using var session = await SftpWorkerSession.StartAsync(unknownKeys: true);
        CollectionAssert.AreEqual(EnabledRoots, session.ChallengeRoots);
        Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
    }

    [TestMethod]
    public async Task ADeclinedUnknownHostKeyStopsTheSessionWithoutExposingCredentials()
    {
        await using var session = await SftpWorkerSession.StartAsync(unknownKeys: true, rejectHostKey: true);
        Assert.AreEqual("Error", session.StartupFrame!.MessageType);
        Assert.AreEqual("HostKeyRejected", session.StartupFrame.Payload.GetProperty("code").GetString());
        Assert.DoesNotContain("secret", session.StartupFrame.Payload.GetRawText());
        CollectionAssert.AreEqual(FirstRoot, session.ChallengeRoots);
    }

    [TestMethod]
    public async Task AHostKeyDecisionCannotAuthorizeAnotherRoot()
    {
        await using var session = await SftpWorkerSession.StartAsync(unknownKeys: true, wrongDecisionRoot: true);
        Assert.AreEqual("HostKeyRejected", session.StartupFrame!.Payload.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task AChangedPinnedHostKeyIsRejectedWithoutAnotherApprovalRequest()
    {
        await using var session = await SftpWorkerSession.StartAsync(rejectHostKey: true);
        Assert.AreEqual("HostKeyRejected", session.StartupFrame!.Payload.GetProperty("code").GetString());
        Assert.IsEmpty(session.ChallengeRoots);
    }

    [TestMethod]
    public async Task WrongCredentialsFailWithASanitizedAuthenticationCode()
    {
        await using var session = await SftpWorkerSession.StartAsync(wrongCredential: true);
        Assert.AreEqual("AuthenticationFailed", session.StartupFrame!.Payload.GetProperty("code").GetString());
        Assert.DoesNotContain("secret", session.StartupFrame.Payload.GetRawText());
    }

    [TestMethod]
    public async Task TraversalOfflineRootsAndCursorReplayAreRefusedWhileOtherRootsRemainUsable()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        AdapterControlFrame page = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 1 });
        string cursor = page.Payload.GetProperty("cursor").GetString()!;
        AdapterControlFrame replay = await session.RequestAsync("List", new { rootKey = "right", path = "", pageSize = 1, cursor });
        Assert.AreEqual("InvalidCursor", replay.Payload.GetProperty("code").GetString());
        foreach ((string root, string path, string code) in new[] { ("offline", "same.txt", "RootOffline"),
            ("left", "../same.txt", "InvalidPath"), ("missing", "same.txt", "UnknownRoot") })
        {
            AdapterControlFrame error = await session.RequestAsync("Stat", new { rootKey = root, path });
            Assert.AreEqual(code, error.Payload.GetProperty("code").GetString());
        }
        Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
    }

    [TestMethod]
    public async Task AStaleRevisionOrOversizedRangeEmitsNoContentAndDoesNotKillTheWorker()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        AdapterControlFrame stat = await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" });
        string expectedRevision = stat.Payload.GetProperty("revision").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(session.Left.Storage, "same.txt"), "different-size");
        AdapterControlFrame stale = await session.RequestAsync("ReadRange", new { rootKey = "left", path = "same.txt", offset = 0, length = 4, expectedRevision });
        Assert.AreEqual("RemoteConflict", stale.Payload.GetProperty("code").GetString());
        AdapterControlFrame oversized = await session.RequestAsync("ReadRange", new { rootKey = "left", path = "same.txt", offset = 0, length = 1024 * 1024 + 1 });
        Assert.AreEqual("InvalidRange", oversized.Payload.GetProperty("code").GetString());
        Assert.AreEqual("right", Encoding.UTF8.GetString(await session.ReadRangeAsync("right", "same.txt", 5)));
    }
    [TestMethod]
    public async Task OptimisticUploadsRetainOriginalsAndKeepRootsAndPrivateEvidenceIsolated()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
        Guid operation = Guid.NewGuid();
        byte[] content = Encoding.UTF8.GetBytes("uploaded replacement");
        AdapterControlFrame complete = await session.UploadAsync("left", "same.txt", content, operation, new(revision, false));
        Assert.AreEqual("UploadComplete", complete.MessageType, complete.Payload.GetRawText());
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(content)), complete.Payload.GetProperty("contentSha256").GetString());
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(Path.Combine(session.Left.Storage, "same.txt")));
        Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Left.Storage, ".mp-recovery-" + operation.ToString("N"))));
        Assert.AreEqual("right", await File.ReadAllTextAsync(Path.Combine(session.Right.Storage, "same.txt")));
        AdapterControlFrame page = await session.RequestAsync("List", new { rootKey = "left", path = "", pageSize = 512 });
        Assert.AreEqual(2, page.Payload.GetProperty("entries").GetArrayLength());
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task MultiFrameAndEmptyUploadsPreserveExactContentAndReleaseTheTransferLease()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        byte[] content = new byte[AdapterBinaryChunkV2Codec.MaximumChunkBytes + 173];
        RandomNumberGenerator.Fill(content);
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "large.bin", content)).MessageType);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(Path.Combine(session.Left.Storage, "large.bin")));
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("right", "empty.bin", [])).MessageType);
        Assert.AreEqual(0L, new FileInfo(Path.Combine(session.Right.Storage, "empty.bin")).Length);
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task ReadOnlyInvalidPoliciesAndStaleOrReservedAddressesRejectUploadsBeforeBytes()
    {
        await using (var session = await SftpWorkerSession.StartAsync(mutationPolicy: "ReadOnly"))
        {
            AssertCode("ReadOnlyRoot", await session.UploadAsync("left", "new.txt", [1]));
            Assert.IsFalse(File.Exists(Path.Combine(session.Left.Storage, "new.txt")));
            Assert.IsFalse(Directory.Exists(session.Cache));
        }
        await using (var session = await SftpWorkerSession.StartAsync(mutationPolicy: "invalid"))
        {
            Assert.AreEqual("Error", session.StartupFrame!.MessageType);
            Assert.IsEmpty(session.CredentialRoots);
        }
        await using (var session = await SftpWorkerSession.StartAsync())
        {
            AssertCode("RemoteConflict", await session.UploadAsync("left", "same.txt", [1], preconditions: new("stale", false)));
            AssertCode("ReservedPath", await session.UploadAsync("left", ".mp-stage-" + Guid.NewGuid().ToString("N"), [1]));
            AssertCode("RootMutationForbidden", await session.UploadAsync("left", "", [1]));
            Assert.AreEqual("left", await File.ReadAllTextAsync(Path.Combine(session.Left.Storage, "same.txt")));
            Assert.IsFalse(Directory.Exists(session.Cache));
        }
    }

    [TestMethod]
    public async Task StableUploadReplayValidatesBytesBindingAndChangesWithUnchangedMetadata()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        Guid operation = Guid.NewGuid();
        byte[] content = Encoding.UTF8.GetBytes("created");
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "new.txt", content, operation)).MessageType);
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "new.txt", content, operation)).MessageType);
        AssertCode("OperationBindingMismatch", await session.UploadAsync("left", "new.txt", Encoding.UTF8.GetBytes("changed"), operation));
        AssertCode("OperationBindingMismatch", await session.UploadAsync("right", "new.txt", content, operation));
        AssertCode("OperationBindingMismatch", await session.UploadAsync("left", "different.txt", content, operation));
        string target = Path.Combine(session.Left.Storage, "new.txt");
        DateTime timestamp = File.GetLastWriteTimeUtc(target);
        await File.WriteAllTextAsync(target, "altered");
        File.SetLastWriteTimeUtc(target, timestamp);
        AssertCode("RemoteConflict", await session.UploadAsync("left", "new.txt", content, operation));
        Assert.AreEqual("altered", await File.ReadAllTextAsync(target));
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task ContentChecksBeforePublicationRejectSameSizeAndTimestampExternalEdits()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(session.Left.Storage, ".fixture-edit-after-stage.json"),
            System.Text.Json.JsonSerializer.Serialize(new { path = "same.txt", content = "edit" }));
        AssertCode("RemoteConflict", await session.UploadAsync("left", "same.txt", Encoding.UTF8.GetBytes("replacement"), preconditions: new(revision, false)));
        Assert.AreEqual("edit", await File.ReadAllTextAsync(Path.Combine(session.Left.Storage, "same.txt")));
        Assert.IsEmpty(Directory.GetFiles(session.Left.Storage, ".mp-recovery-*"));
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task CanceledOrInvalidFramesReleaseTheUploadLeaseWithoutMutatingSources()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        foreach (bool cancel in new[] { true, false })
        {
            Guid request = Guid.NewGuid(), stream = Guid.NewGuid(), operation = Guid.NewGuid();
            await session.SendAsync("Upload", request, new { rootKey = "left", path = "new.txt", operationId = operation, streamId = stream, length = 2 });
            Assert.AreEqual("UploadReady", (await session.ReadAsync()).MessageType);
            await session.SendChunkAsync(request, stream, "left", 0, [1], false);
            if (cancel)
            {
                AssertCode("CancelRootMismatch", await session.RequestAsync("Cancel", new { rootKey = "right", targetRequestId = request, operationId = operation }));
                Guid cancelRequest = Guid.NewGuid();
                await session.SendAsync("Cancel", cancelRequest, new { rootKey = "left", targetRequestId = request, operationId = operation });
                AdapterControlFrame error = await session.ReadAsync();
                Assert.AreEqual(request, error.RequestId);
                AssertCode("Canceled", error);
                Assert.AreEqual(cancelRequest, (await session.ReadAsync()).RequestId);
            }
            else
            {
                await session.SendChunkAsync(request, stream, "right", 1, [2], true);
                Assert.AreEqual("OperationError", (await session.ReadAsync()).MessageType);
            }
            Assert.IsFalse(File.Exists(Path.Combine(session.Left.Storage, "new.txt")));
            await AssertCacheClearedAsync(session);
        }
    }

    [TestMethod]
    public async Task FileMovesAndDeletesRetainOriginalsAndRejectRecreatedSourcesOnReplay()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
        var move = new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "left", "moved.txt", new(revision));
        AdapterControlFrame moved = await session.RequestAsync("Move", move);
        Assert.AreEqual("MutationComplete", moved.MessageType, moved.Payload.GetRawText());
        Assert.IsNull(session.Left.ReadStoredFile("/same.txt"));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/moved.txt"));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + move.OperationId.ToString("N")));
        int commands = session.Left.MutationCommands.Count;
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Move", move)).MessageType);
        Assert.HasCount(commands, session.Left.MutationCommands);
        var delete = new AdapterOperationRequest(Guid.NewGuid(), "left", "moved.txt", Preconditions: new(moved.Payload.GetProperty("revision").GetString()));
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", delete)).MessageType);
        Assert.IsNull(session.Left.ReadStoredFile("/moved.txt"));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/.mp-recovery-" + delete.OperationId.ToString("N")));
        commands = session.Left.MutationCommands.Count;
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", delete)).MessageType);
        Assert.HasCount(commands, session.Left.MutationCommands);
        session.Left.ReplaceStoredFile("/moved.txt", Encoding.UTF8.GetBytes("external"));
        AssertCode("RemoteConflict", await session.RequestAsync("Delete", delete));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("external"), session.Left.ReadStoredFile("/moved.txt"));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("right"), session.Right.ReadStoredFile("/same.txt"));
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task DirectoryCreationAndEmptyDeletionRefuseChildrenAndReservedRecoveryEvidence()
    {
        await using var session = await SftpWorkerSession.StartAsync();
        var create = new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "folder");
        AdapterControlFrame created = await session.RequestAsync("CreateDirectory", create);
        Assert.AreEqual("MutationComplete", created.MessageType, created.Payload.GetRawText());
        Assert.IsTrue(session.Left.HasDirectory("/folder"));
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory", create)).MessageType);
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "folder/nested"))).MessageType);
        string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "folder" })).Payload.GetProperty("revision").GetString()!;
        AssertCode("DirectoryNotEmpty", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "folder", Preconditions: new(revision), IsDirectory: true)));
        Assert.IsTrue(session.Left.HasDirectory("/folder/nested"));
        Assert.AreEqual("UploadComplete", (await session.UploadAsync("left", "folder/nested/file.bin", [1, 2])).MessageType);
        string nestedRevision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "folder/nested" })).Payload.GetProperty("revision").GetString()!;
        AssertCode("DirectoryNotEmpty", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "folder/nested", Preconditions: new(nestedRevision), IsDirectory: true)));
        AssertCode("DirectoryMoveUnavailable", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "folder", "left", "moved", new(revision), true)));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, session.Left.ReadStoredFile("/folder/nested/file.bin"));
        AdapterControlFrame empty = await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "empty"));
        var remove = new AdapterOperationRequest(Guid.NewGuid(), "left", "empty", Preconditions: new(empty.Payload.GetProperty("revision").GetString()), IsDirectory: true);
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", remove)).MessageType);
        Assert.IsFalse(session.Left.HasDirectory("/empty"));
        Assert.AreEqual("MutationComplete", (await session.RequestAsync("Delete", remove)).MessageType);
        await AssertCacheClearedAsync(session);
    }

    [TestMethod]
    public async Task NamespaceMutationsEnforceReadOnlyDestinationAndOperationBindingBoundaries()
    {
        await using (var session = await SftpWorkerSession.StartAsync(mutationPolicy: "ReadOnly"))
        {
            AssertCode("ReadOnlyRoot", await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(Guid.NewGuid(), "left", "folder")));
            AssertCode("ReadOnlyRoot", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt")));
            AssertCode("ReadOnlyRoot", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "left", "new.txt")));
            Assert.IsEmpty(session.Left.MutationCommands);
        }
        await using (var session = await SftpWorkerSession.StartAsync())
        {
            string revision = (await session.RequestAsync("Stat", new { rootKey = "left", path = "same.txt" })).Payload.GetProperty("revision").GetString()!;
            AssertCode("CrossRootMoveUnavailable", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "right", "new.txt", new(revision))));
            Assert.IsEmpty(session.Left.MutationCommands);
            AssertCode("RemoteConflict", await session.RequestAsync("Move", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", "left", "second.txt", new(revision, false))));
            AssertCode("RemoteConflict", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "same.txt", Preconditions: new("stale"))));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), session.Left.ReadStoredFile("/same.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("second-left"), session.Left.ReadStoredFile("/second.txt"));
            Guid operation = Guid.NewGuid();
            Assert.AreEqual("MutationComplete", (await session.RequestAsync("CreateDirectory", new AdapterCreateDirectoryRequest(operation, "left", "created"))).MessageType);
            AssertCode("OperationBindingMismatch", await session.UploadAsync("left", "new.txt", [1], operation));
            AssertCode("RootMutationForbidden", await session.RequestAsync("Delete", new AdapterOperationRequest(Guid.NewGuid(), "left", "", IsDirectory: true)));
        }
    }

    private static void AssertCode(string code, AdapterControlFrame response)
    {
        Assert.AreEqual("OperationError", response.MessageType, response.Payload.GetRawText());
        Assert.AreEqual(code, response.Payload.GetProperty("code").GetString());
    }

    private static async Task AssertCacheClearedAsync(SftpWorkerSession session)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(session.Cache) && Directory.GetFiles(session.Cache).Length != 0; attempt++)
            await Task.Delay(50);
        if (Directory.Exists(session.Cache)) Assert.IsEmpty(Directory.GetFiles(session.Cache));
    }
}
