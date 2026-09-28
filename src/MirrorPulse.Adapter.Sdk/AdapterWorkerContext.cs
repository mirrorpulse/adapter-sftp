using System.Collections.ObjectModel;

namespace MirrorPulse.Adapter.Sdk;

/// <summary>
/// Non-secret startup context supplied by MirrorPulse to an Adapter Worker.
/// </summary>
public sealed record AdapterWorkerContext
{
    public AdapterWorkerContext(
        Guid instanceId,
        Guid workerSessionId,
        string pipeName,
        IReadOnlyDictionary<string, string> configuration,
        string fileCacheDirectory,
        string transferCacheDirectory)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("An instance ID cannot be empty.", nameof(instanceId));
        }

        if (workerSessionId == Guid.Empty)
        {
            throw new ArgumentException("A Worker session ID cannot be empty.", nameof(workerSessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileCacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(transferCacheDirectory);
        InstanceId = instanceId;
        WorkerSessionId = workerSessionId;
        PipeName = pipeName.Trim();
        Configuration = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(configuration, StringComparer.Ordinal));
        FileCacheDirectory = fileCacheDirectory;
        TransferCacheDirectory = transferCacheDirectory;
    }

    public Guid InstanceId { get; }

    public Guid WorkerSessionId { get; }

    public string PipeName { get; }

    public IReadOnlyDictionary<string, string> Configuration { get; }

    public string FileCacheDirectory { get; }

    public string TransferCacheDirectory { get; }
}

public interface IAdapterWorker
{
    ValueTask RunAsync(AdapterWorkerContext context, CancellationToken cancellationToken = default);
}

public static class AdapterWorkerHost
{
    public static ValueTask RunAsync(IAdapterWorker worker, AdapterWorkerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(context);
        return worker.RunAsync(context, cancellationToken);
    }
}
