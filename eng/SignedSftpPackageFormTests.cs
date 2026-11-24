using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Adapter.Sftp.Worker.Tests;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Packaging;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class SignedSftpPackageFormTests
{
    [TestMethod]
    public async Task SignedSingleRootPackageUsesSavedCredentialsAndRecoversAnAcceptedUpload()
    {
        string package = Environment.GetEnvironmentVariable("MP_SFTP_V2_PACKAGE")
            ?? throw new InvalidOperationException("A signed SFTP candidate is required.");
        bool official = Environment.GetEnvironmentVariable("MP_SFTP_V2_OFFICIAL_SIGNED") == "true";
        string directory = Directory.CreateTempSubdirectory("mp-sftp-host-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        try
        {
            await using var source = await SftpServerFixture.StartAsync("left");
            var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
            string credentialRoot = Path.Combine(directory, "credentials");
            var credentials = new DpapiCredentialStore(credentialRoot);
            string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            using RSA publisher = official ? MirrorPulseOfficialAdapterTrust.CreatePublicKey() : RSA.Create();
            if (!official)
                publisher.ImportFromPem(await File.ReadAllTextAsync(package + ".public.pem", token).ConfigureAwait(false));

            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, cancellationToken: token).ConfigureAwait(false);
            var installed = await catalog.InstallSignedAdapterAsync(package, package + ".signature.json",
                Path.Combine(directory, "installed"), runtime, publisher,
                official ? MirrorPulseOfficialAdapterTrust.Signer : "MirrorPulse Dry Run", token).ConfigureAwait(false);
            Assert.AreEqual("com.mirrorpulse.adapter.sftp", installed.AdapterId.Value);
            Assert.IsTrue(installed.IsSigned);
            Assert.AreEqual(2, installed.Manifest.SchemaVersion);
            Assert.AreEqual(1, installed.Manifest.InstancePolicy.MaximumRootDefinitions);
            var presentation = installed.Manifest.Presentation ?? throw new InvalidDataException("The signed form is missing.");
            Assert.AreEqual("assets/logo.png", presentation.LogoPath);
            var password = presentation.Resolve("en-US").Controls.Single(field => field.Key == "password");
            Assert.IsTrue(password.Required);

            var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["endpoint"] = $"sftp://127.0.0.1:{source.Port}/",
                ["username"] = "user-left",
                ["trustedHostKeySha256"] = "SHA256:" + source.Fingerprint,
                ["mutationPolicy"] = "Optimistic",
            };
            var provisioner = new MirrorPulseAdapterInstanceProvisioner(catalog, credentials, paths.DataRootPath);
            var draft = new MirrorPulseCreateInstanceRequest(installed.InstallId.ToString(), "SFTP fixture",
                configuration, new Dictionary<string, string>(), null, Enabled: true);
            await Assert.ThrowsExactlyAsync<AdapterConfigurationValidationException>(
                () => provisioner.CreateAsync(draft, token)).ConfigureAwait(false);
            var instance = await provisioner.CreateAsync(draft with
            {
                SecretFields = new Dictionary<string, string?> { ["password"] = "secret-left" },
            }, token).ConfigureAwait(false);
            var topology = await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false);
            Assert.AreEqual("sftp", topology.Roots.Single().UniquenessKey);
            Assert.IsFalse(instance.Configuration.ContainsKey("password"));
            Assert.IsFalse(instance.Configuration.ContainsKey("credentialReference"));
            Assert.IsTrue(instance.CredentialBindings!.ContainsKey("password"));

            byte[] replacement = Encoding.UTF8.GetBytes("accepted replacement");
            var operation = Guid.NewGuid();
            string originalRevision;
            string acceptedRevision;
            await using (var host = new AdapterInstanceProcessSupervisor(catalog, credentials))
            {
                await StartAsync(host).ConfigureAwait(false);
                var page = await host.ReadDirectoryPageAsync(
                    new(instance.InstanceId, "", ReadOnlyMemory<byte>.Empty, 8, "sftp"), token).ConfigureAwait(false);
                var file = page.Entries.Single(entry => entry.RelativePath == "same.txt");
                originalRevision = file.RemoteRevision;
                await using Stream read = await host.ReadRangeAsync(new(instance.InstanceId, "same.txt",
                    ReadOnlyMemory<byte>.Empty, 0, 4, "sftp") { ExpectedRevision = originalRevision }, token).ConfigureAwait(false);
                byte[] actual = new byte[4];
                await read.ReadExactlyAsync(actual, token).ConfigureAwait(false);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"), actual);
                acceptedRevision = await UploadAsync(host).ConfigureAwait(false);
            }

            await using var restarted = new AdapterInstanceProcessSupervisor(catalog, new DpapiCredentialStore(credentialRoot));
            await StartAsync(restarted).ConfigureAwait(false);
            Assert.AreEqual(acceptedRevision, await UploadAsync(restarted).ConfigureAwait(false));
            CollectionAssert.AreEqual(replacement, source.ReadStoredFile("/same.txt"));
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("left"),
                source.ReadStoredFile("/.mp-recovery-" + operation.ToString("N")));

            async Task StartAsync(AdapterInstanceProcessSupervisor host)
            {
                await host.StartAsync(await catalog.ReadAdapterTopologyAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
                while (!host.IsConnected(instance.InstanceId))
                {
                    var state = await catalog.ReadInstanceRuntimeStateAsync(instance.InstanceId, token).ConfigureAwait(false);
                    if (state?.Phase is "Worker failed" or "Worker error")
                        Assert.Fail("The signed Worker failed: " + state.LastErrorCode);
                    await Task.Delay(25, token).ConfigureAwait(false);
                }
                Assert.AreEqual(2, host.GetConnectedProtocolVersion(instance.InstanceId));
            }

            async Task<string> UploadAsync(AdapterInstanceProcessSupervisor host)
            {
                using var content = new MemoryStream(replacement);
                return await host.UploadAsync(new(instance.InstanceId, "same.txt", originalRevision, content,
                    replacement.Length, operation, RootKey: "sftp"), token).ConfigureAwait(false);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
