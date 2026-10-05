using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Adapter.Sftp.Worker.Tests;

internal sealed class SftpServerFixture(Process process, string storage, int port, string fingerprint, string label) : IAsyncDisposable
{
    public string Storage { get; } = storage;
    public int Port { get; } = port;
    public string Fingerprint { get; } = fingerprint;
    public string Label { get; } = label;

    public static async Task<SftpServerFixture> StartAsync(string label)
    {
        string repository = FindRepository();
        string python = Environment.GetEnvironmentVariable("MP_SFTP_FIXTURE_PYTHON") ??
            Path.Combine(repository, "artifacts", "test-tools", "sftp", "venv", "Scripts", "python.exe");
        if (!File.Exists(python)) throw new InvalidOperationException("Run eng/setup-test-environment.ps1 first.");
        string storage = Path.Combine(Path.GetTempPath(), "mp-sftp-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storage);
        await File.WriteAllTextAsync(Path.Combine(storage, "same.txt"), label);
        await File.WriteAllTextAsync(Path.Combine(storage, "second.txt"), "second-" + label);
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(repository, "eng", "sftp-fixture", "server.py"));
        start.ArgumentList.Add(storage);
        start.ArgumentList.Add(label);
        Process process = Process.Start(start) ?? throw new InvalidOperationException("Fixture launch failed.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string line = await process.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException("Fixture startup failed.");
            using JsonDocument ready = JsonDocument.Parse(line);
            return new(process, storage, ready.RootElement.GetProperty("port").GetInt32(),
                ready.RootElement.GetProperty("sha256").GetString()!, label);
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process.Dispose();
            Directory.Delete(storage, true);
            throw;
        }
    }

    private static string FindRepository()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "eng", "adapter-sdk.lock.json")))
            directory = Directory.GetParent(directory)?.FullName;
        return directory ?? throw new DirectoryNotFoundException("Repository not found.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        process.Dispose();
        Directory.Delete(Storage, true);
    }
}
