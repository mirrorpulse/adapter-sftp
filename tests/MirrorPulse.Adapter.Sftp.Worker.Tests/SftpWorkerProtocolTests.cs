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
}
