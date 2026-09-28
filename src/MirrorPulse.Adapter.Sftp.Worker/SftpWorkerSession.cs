using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MirrorPulse.Adapter.Sftp.Worker;

public sealed record SftpWorkerConfiguration(
    Uri Endpoint,
    string Username,
    string CredentialReference,
    string? TrustedHostKeySha256 = null)
{
    public void Validate()
    {
        if (!Endpoint.IsAbsoluteUri || !string.Equals(Endpoint.Scheme, "sftp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Endpoint.Host) || !string.IsNullOrWhiteSpace(Endpoint.UserInfo) ||
            !string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment) ||
            string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(CredentialReference) ||
            (TrustedHostKeySha256 is not null &&
                (TrustedHostKeySha256.Length != 43 ||
                 !TrustedHostKeySha256.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                     or >= '0' and <= '9' or '+' or '/'))))
        {
            throw new InvalidDataException("The SFTP endpoint, username, or credential reference is invalid.");
        }
    }
}

public static class SftpWorkerProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        AdapterWorkerProcessArguments arguments;
        try
        {
            arguments = AdapterWorkerProcessArguments.Parse(args);
        }
        catch (ArgumentException)
        {
            return 2;
        }

        await using AdapterNamedPipeClient pipe = await AdapterNamedPipeClient.ConnectAsync(
            arguments.PipeName, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var channel = new AdapterControlChannel(pipe, arguments.InstanceId, arguments.WorkerSessionId);
        Guid helloId = Guid.NewGuid();
        await channel.SendAsync("Hello", helloId, false,
            new { adapterId = "mirrorpulse.sftp", minimumProtocolVersion = 1, maximumProtocolVersion = 1 },
            cancellationToken).ConfigureAwait(false);

        try
        {
            AdapterControlFrame ready = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (ready.MessageType != "Ready" || !ready.IsResponse || ready.RequestId != helloId)
            {
                throw new InvalidDataException("The Host did not accept the SFTP Worker handshake.");
            }

            SftpWorkerConfiguration configuration = ready.Payload.Deserialize<SftpWorkerConfiguration>(JsonOptions)
                ?? throw new InvalidDataException("The Host did not provide SFTP configuration.");
            configuration.Validate();
            Guid credentialId = Guid.NewGuid();
            await channel.SendAsync("CredentialRequest", credentialId, false,
                new { referenceId = configuration.CredentialReference }, cancellationToken).ConfigureAwait(false);
            AdapterControlFrame credential = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (credential.MessageType != "CredentialResponse" || !credential.IsResponse ||
                credential.RequestId != credentialId ||
                credential.Payload.GetProperty("referenceId").GetString() != configuration.CredentialReference)
            {
                throw new InvalidDataException("The Host did not provide the requested SFTP credential.");
            }

            string password = credential.Payload.GetProperty("secret").GetString()
                ?? throw new InvalidDataException("The SFTP credential is empty.");
            using var client = new SftpClient(configuration.Endpoint.Host,
                configuration.Endpoint.IsDefaultPort ? 22 : configuration.Endpoint.Port,
                configuration.Username, password);
            bool hostKeyRejected = false;
            string? acceptedFingerprint = null;
            client.HostKeyReceived += (_, key) =>
            {
                string fingerprint = key.FingerPrintSHA256;
                if (configuration.TrustedHostKeySha256 is not null)
                {
                    key.CanTrust = string.Equals(configuration.TrustedHostKeySha256,
                        fingerprint, StringComparison.Ordinal);
                }
                else
                {
                    Guid challengeId = Guid.NewGuid();
                    using var challengeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        channel.SendAsync("HostKeyChallenge", challengeId, false,
                            new
                            {
                                host = configuration.Endpoint.Host,
                                port = configuration.Endpoint.IsDefaultPort ? 22 : configuration.Endpoint.Port,
                                algorithm = key.HostKeyName,
                                sha256 = fingerprint
                            }, challengeTimeout.Token)
                            .AsTask().GetAwaiter().GetResult();
                        AdapterControlFrame decision = channel.ReadAsync(challengeTimeout.Token)
                            .AsTask().GetAwaiter().GetResult();
                        key.CanTrust = decision.MessageType == "HostKeyDecision" && decision.IsResponse &&
                            decision.RequestId == challengeId &&
                            decision.Payload.GetProperty("sha256").GetString() == fingerprint &&
                            decision.Payload.GetProperty("approved").GetBoolean();
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or
                        InvalidDataException or KeyNotFoundException)
                    {
                        key.CanTrust = false;
                    }
                }

                if (key.CanTrust)
                {
                    acceptedFingerprint = fingerprint;
                }
                else
                {
                    hostKeyRejected = true;
                }
            };
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (hostKeyRejected)
            {
                await channel.SendAsync("Error", helloId, false,
                    new { code = "HostKeyRejected" }, CancellationToken.None).ConfigureAwait(false);
                return 1;
            }

            await channel.SendAsync("Connected", helloId, false,
                new { hostKeySha256 = acceptedFingerprint }, cancellationToken).ConfigureAwait(false);

            var transfer = new SftpWorkerTransferProtocol(channel, client, configuration,
                arguments.InstanceId, arguments.WorkerSessionId);
            while (true)
            {
                AdapterControlFrame command = await channel.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (command.IsResponse)
                {
                    throw new InvalidDataException("The Host sent an unexpected SFTP response.");
                }

                if (command.MessageType == "Stop")
                {
                    await channel.SendAsync("Stopped", command.RequestId, true, new { }, cancellationToken)
                        .ConfigureAwait(false);
                    return 0;
                }

                await transfer.HandleAsync(command, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or KeyNotFoundException)
        {
            await channel.SendAsync("Error", helloId, false,
                new { code = "InvalidConfiguration" }, CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
        catch (SshAuthenticationException)
        {
            await channel.SendAsync("Error", helloId, false,
                new { code = "AuthenticationFailed" }, CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is SshConnectionException or IOException or System.Net.Sockets.SocketException)
        {
            await channel.SendAsync("Error", helloId, false,
                new { code = "ConnectionFailed" }, CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }
}

public sealed class SftpWorkerEntryMarker;
