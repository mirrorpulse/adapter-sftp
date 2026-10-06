using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet;

namespace MirrorPulse.Adapter.Sftp.Worker;

public sealed record SftpWorkerConfiguration(Uri Endpoint, string Username,
    string CredentialReference, string? TrustedHostKeySha256 = null)
{
    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri || !string.Equals(Endpoint.Scheme, "sftp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Endpoint.Host) || !string.IsNullOrEmpty(Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment) ||
            string.IsNullOrWhiteSpace(Username) || Username.Length > 256 || Username.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(CredentialReference) ||
            (TrustedHostKeySha256 is not null && (TrustedHostKeySha256.Length != 43 ||
                !TrustedHostKeySha256.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                    or >= '0' and <= '9' or '+' or '/'))))
            throw new InvalidDataException("InvalidConfiguration");
        _ = SftpPathPolicy.Resolve(Endpoint, "");
    }
}

internal sealed record SftpWorkerRoot(string Key, SftpWorkerConfiguration Configuration, SftpClient Client,
    string HostKeySha256, bool AllowsMutations)
{
    private bool _requiresReconnect;
    public void RequireReconnect() => _requiresReconnect = true;
    public async Task EnsureConnectedAsync(CancellationToken token)
    {
        if (!_requiresReconnect && Client.IsConnected) return;
        Client.Disconnect();
        await Client.ConnectAsync(token).ConfigureAwait(false);
        _requiresReconnect = false;
    }
}

internal sealed class SftpWorkerRoots : IDisposable
{
    private readonly Dictionary<string, SftpWorkerRoot?> _roots = new(StringComparer.Ordinal);

    public static async Task<SftpWorkerRoots> CreateAsync(AdapterReady ready, AdapterControlChannel channel, CancellationToken token)
    {
        if (ready.Roots.Count is < 1 or > 64) throw new InvalidDataException("InvalidRoots");
        var roots = new SftpWorkerRoots();
        try
        {
            foreach (AdapterRootBinding binding in ready.Roots)
            {
                if (!binding.Enabled) { roots._roots.Add(binding.RootKey, null); continue; }
                string mutationPolicy = binding.Configuration.GetValueOrDefault("mutationPolicy") ?? "Optimistic";
                if (mutationPolicy is not ("Optimistic" or "ReadOnly")) throw new InvalidDataException("InvalidMutationPolicy");
                var configuration = new SftpWorkerConfiguration(
                    new Uri(binding.Configuration.GetValueOrDefault("endpoint") ?? throw new InvalidDataException("EndpointRequired")),
                    binding.Configuration.GetValueOrDefault("username") ?? throw new InvalidDataException("UsernameRequired"),
                    binding.Configuration.GetValueOrDefault("credentialReference") ?? throw new InvalidDataException("CredentialRequired"),
                    binding.Configuration.GetValueOrDefault("trustedHostKeySha256"));
                configuration.Validate();
                Guid request = Guid.NewGuid();
                await channel.SendAsync("CredentialRequest", request, false,
                    new { rootKey = binding.RootKey, referenceId = configuration.CredentialReference }, token).ConfigureAwait(false);
                AdapterControlFrame response = await channel.ReadAsync(token).ConfigureAwait(false);
                if (response.MessageType != "CredentialResponse" || !response.IsResponse || response.RequestId != request ||
                    response.Payload.GetProperty("referenceId").GetString() != configuration.CredentialReference)
                    throw new InvalidDataException("CredentialRejected");
                string secret = response.Payload.GetProperty("secret").GetString() ?? throw new InvalidDataException("CredentialRequired");
                var client = new SftpClient(configuration.Endpoint.Host,
                    configuration.Endpoint.IsDefaultPort ? 22 : configuration.Endpoint.Port, configuration.Username, secret)
                { OperationTimeout = TimeSpan.FromSeconds(15) };
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
                bool rejected = false;
                string? accepted = null;
                client.HostKeyReceived += (_, key) =>
                {
                    string fingerprint = key.FingerPrintSHA256;
                    key.CanTrust = false;
                    if (accepted is not null)
                        key.CanTrust = accepted == fingerprint;
                    else if (configuration.TrustedHostKeySha256 is not null)
                        key.CanTrust = configuration.TrustedHostKeySha256 == fingerprint;
                    else
                    {
                        Guid challenge = Guid.NewGuid();
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(30));
                        try
                        {
                            channel.SendAsync("HostKeyChallenge", challenge, false,
                                new
                                {
                                    rootKey = binding.RootKey,
                                    host = configuration.Endpoint.Host,
                                    port = configuration.Endpoint.IsDefaultPort ? 22 : configuration.Endpoint.Port,
                                    algorithm = key.HostKeyName,
                                    sha256 = fingerprint
                                }, deadline.Token).AsTask().GetAwaiter().GetResult();
                            AdapterControlFrame decision = channel.ReadAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                            key.CanTrust = decision.MessageType == "HostKeyDecision" && decision.IsResponse &&
                                decision.RequestId == challenge && decision.Payload.GetProperty("rootKey").GetString() == binding.RootKey &&
                                decision.Payload.GetProperty("sha256").GetString() == fingerprint && decision.Payload.GetProperty("approved").GetBoolean();
                        }
                        catch (Exception exception) when (exception is OperationCanceledException or InvalidDataException or KeyNotFoundException or JsonException)
                        { key.CanTrust = false; }
                    }
                    if (key.CanTrust) accepted = fingerprint;
                    else rejected = true;
                };
                try
                {
                    await client.ConnectAsync(token).ConfigureAwait(false);
                    roots._roots.Add(binding.RootKey, new(binding.RootKey, configuration, client,
                        accepted ?? throw new InvalidDataException("HostKeyRejected"), mutationPolicy == "Optimistic"));
                }
                catch (Exception) when (rejected) { client.Dispose(); throw new InvalidDataException("HostKeyRejected"); }
                catch { client.Dispose(); throw; }
            }
            return roots;
        }
        catch { roots.Dispose(); throw; }
    }

    public SftpWorkerRoot Get(string key) => !_roots.TryGetValue(key, out SftpWorkerRoot? root)
        ? throw new InvalidDataException("UnknownRoot") : root ?? throw new InvalidDataException("RootOffline");

    public void RequireReconnect(string key)
    {
        if (_roots.TryGetValue(key, out SftpWorkerRoot? root)) root?.RequireReconnect();
    }

    public object ConnectedPayload => new
    {
        roots = _roots.Values.Where(root => root is not null)
        .Select(root => new { rootKey = root!.Key, hostKeySha256 = root.HostKeySha256 }).ToArray()
    };

    public void Dispose()
    {
        foreach (SftpWorkerRoot? root in _roots.Values) root?.Client.Dispose();
    }
}

internal static class SftpPathPolicy
{
    public static string Resolve(Uri endpoint, string relative)
    {
        if (relative.Length > 4096 || relative.StartsWith('/') || relative.Contains('\\') ||
            relative.Contains(':') || relative.Any(char.IsControl)) throw new InvalidDataException("InvalidPath");
        string prefix = Uri.UnescapeDataString(endpoint.AbsolutePath).TrimEnd('/');
        if (prefix.Contains('\\') || prefix.Contains(':') || prefix.Any(char.IsControl) ||
            prefix.Split('/').Any(part => part is "." or "..")) throw new InvalidDataException("InvalidPath");
        if (relative.Length == 0) return prefix.Length == 0 ? "/" : prefix;
        if (relative.Split('/').Any(part => part.Length == 0 || part is "." or "..")) throw new InvalidDataException("InvalidPath");
        return prefix + "/" + relative;
    }
}
