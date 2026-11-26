using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace TapRoom.Services;

public sealed class SetupManager
{
    private const string PinnedMitmproxyVersion = "12.2.3";
    private const string PinnedMitmproxyZipUrl = "https://downloads.mitmproxy.org/12.2.3/mitmproxy-12.2.3-windows-x86_64.zip";
    private const string PinnedMitmproxyZipSha256 = "04A01EA95AE96DF75058A893E774957D294E69012DAB1F4E256CE2B0C6725483";
    private const string PinnedMitmproxyInstallerUrl = "https://downloads.mitmproxy.org/12.2.3/mitmproxy-12.2.3-windows-x86_64-installer.exe";
    private const string PinnedMitmproxyInstallerSha256 = "844D0D40C4B9D7E68A8FCDB66354255C4480EA43C4CEB783D7E41C7A98FA8CE9";

    private static readonly IReadOnlyDictionary<string, string> PinnedMitmproxyExecutableHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mitmdump.exe"] = "36A45AADEB842185B8064B8F0BE3730E079C9F9C125BC8BE22363332969857BF",
            ["mitmproxy.exe"] = "26B142B7AB13CE2C1D2A38351ED24347782EDED3A8FABECD94EF2240E657D190",
            ["mitmweb.exe"] = "5F9EFA7977BCA48D1031624CC21BEB453C4D7143D746A97EDEA3F75D8DEDEEB4"
        };

    private readonly PortAllocator _ports = new(10000, 10999);

    public event Action<string>? Log;

    public async Task RunAsync(bool trustLocalMachine, MitmproxyInstallMode installMode, bool forceInstall, CancellationToken cancellationToken)
    {
        LogLine("TapRoom setup started.");
        LogLine($"Install trust target: {(trustLocalMachine ? "LocalMachine\\Root" : "CurrentUser\\Root")}");

        if (trustLocalMachine && !IsAdministrator())
            throw new InvalidOperationException("LocalMachine certificate trust requires TapRoom to be run as Administrator. Use CurrentUser trust or restart elevated.");

        var toolset = LocateMitmproxyToolset();
        if (forceInstall || !toolset.IsComplete)
        {
            LogLine(forceInstall
                ? $"Installing mitmproxy using selected method: {installMode}"
                : $"mitmproxy tools were not found. Attempting install using: {installMode}");

            await InstallMitmproxyAsync(installMode, cancellationToken);
            toolset = LocateMitmproxyToolset();
        }
        else
        {
            LogLine("mitmproxy tools were found. Skipping install and verifying existing tools.");
        }

        if (!toolset.IsComplete)
            throw new InvalidOperationException("mitmproxy tools were still not found after install. Open a new terminal/session and verify mitmdump.exe is on PATH.");

        LogLine($"mitmproxy: {toolset.MitmProxyPath}");
        LogLine($"mitmdump : {toolset.MitmDumpPath}");
        LogLine($"mitmweb  : {toolset.MitmWebPath}");

        await TestRunsAsync(toolset.MitmProxyPath!, "--version", cancellationToken);
        await TestRunsAsync(toolset.MitmDumpPath!, "--version", cancellationToken);
        await TestRunsAsync(toolset.MitmWebPath!, "--version", cancellationToken);

        var certPath = await EnsureCaCertificateAsync(toolset.MitmDumpPath!, cancellationToken);
        InstallCertificate(certPath, trustLocalMachine);
        LogLine("TapRoom setup complete.");
    }

    public MitmproxyToolset LocateMitmproxyToolset()
    {
        return new MitmproxyToolset(
            ToolResolver.FindExecutable("mitmproxy.exe"),
            ToolResolver.FindExecutable("mitmdump.exe"),
            ToolResolver.FindExecutable("mitmweb.exe"));
    }

    public async Task<SetupCheckResult> CheckPrerequisitesAsync(CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var toolset = LocateMitmproxyToolset();

        await CheckToolAsync("mitmproxy.exe", toolset.MitmProxyPath, issues, cancellationToken);
        await CheckToolAsync("mitmdump.exe", toolset.MitmDumpPath, issues, cancellationToken);
        await CheckToolAsync("mitmweb.exe", toolset.MitmWebPath, issues, cancellationToken);

        var certPath = GetMitmproxyCaCertificatePath();
        if (!File.Exists(certPath))
            issues.Add($"mitmproxy CA certificate has not been generated yet: {certPath}");

        return new SetupCheckResult(toolset, certPath, issues);
    }

    private async Task CheckToolAsync(string displayName, string? path, List<string> issues, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            issues.Add($"{displayName} was not found.");
            return;
        }

        try
        {
            var result = await RunProcessAsync(path!, "--version", TimeSpan.FromSeconds(8), cancellationToken);
            if (result.ExitCode != 0)
                issues.Add($"{displayName} was found but failed its version check: {path}");
        }
        catch (Exception ex)
        {
            issues.Add($"{displayName} was found but could not be run: {path}. {ex.Message}");
        }
    }

    private async Task InstallMitmproxyAsync(MitmproxyInstallMode installMode, CancellationToken cancellationToken)
    {
        switch (installMode)
        {
            case MitmproxyInstallMode.WindowsInstaller:
                await DownloadAndRunPinnedInstallerAsync(cancellationToken);
                break;

            case MitmproxyInstallMode.StandaloneUserProfile:
                await DownloadAndInstallPinnedMitmproxyAsync(cancellationToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(installMode), installMode, "Unsupported mitmproxy install mode.");
        }
    }

    private async Task DownloadAndRunPinnedInstallerAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PathHelper.LocalAppDataRoot);

        var downloadDir = Path.Combine(PathHelper.LocalAppDataRoot, "Downloads");
        Directory.CreateDirectory(downloadDir);

        var installerPath = Path.Combine(downloadDir, $"mitmproxy-{PinnedMitmproxyVersion}-windows-x86_64-installer.exe");

        if (!File.Exists(installerPath) || !HashMatches(installerPath, PinnedMitmproxyInstallerSha256))
        {
            if (File.Exists(installerPath))
                File.Delete(installerPath);

            LogLine($"Downloading mitmproxy {PinnedMitmproxyVersion} Windows installer.");
            LogLine(PinnedMitmproxyInstallerUrl);
            await DownloadFileAsync(PinnedMitmproxyInstallerUrl, installerPath, cancellationToken);
        }
        else
        {
            LogLine($"Using existing verified installer: {installerPath}");
        }

        var installerHash = ComputeSha256(installerPath);
        LogLine($"Installer SHA256: {installerHash}");
        if (!string.Equals(installerHash, PinnedMitmproxyInstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloaded mitmproxy installer failed SHA-256 verification and will not be run.");

        const string installerArguments = "--mode unattended --unattendedmodeui none";
        LogLine($"Launching mitmproxy installer in unattended mode: {installerArguments}");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = installerArguments,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Failed to launch mitmproxy installer.");

        await process.WaitForExitAsync(cancellationToken);
        LogLine($"mitmproxy installer exited with code {process.ExitCode}.");

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mitmproxy installer failed or was cancelled. Exit code: {process.ExitCode}");

        RemoveTapRoomManagedMitmproxyCurrent();
    }

    private void RemoveTapRoomManagedMitmproxyCurrent()
    {
        var currentRoot = Path.Combine(PathHelper.LocalToolsRoot, "mitmproxy", "current");
        if (!Directory.Exists(currentRoot))
            return;

        try
        {
            Directory.Delete(currentRoot, recursive: true);
            LogLine($"Removed TapRoom-managed standalone tools so the Windows installer version can be used: {currentRoot}");
        }
        catch (Exception ex)
        {
            LogLine($"WARNING: Could not remove TapRoom-managed standalone tools at {currentRoot}: {ex.Message}");
            LogLine("TapRoom may continue to prefer the standalone tools until that folder is removed.");
        }
    }

    private async Task DownloadAndInstallPinnedMitmproxyAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(PathHelper.LocalAppDataRoot);
        Directory.CreateDirectory(PathHelper.LocalToolsRoot);

        var downloadDir = Path.Combine(PathHelper.LocalAppDataRoot, "Downloads");
        Directory.CreateDirectory(downloadDir);

        var zipPath = Path.Combine(downloadDir, $"mitmproxy-{PinnedMitmproxyVersion}-windows-x86_64.zip");
        var installRoot = Path.Combine(PathHelper.LocalToolsRoot, "mitmproxy", PinnedMitmproxyVersion);
        var extractRoot = Path.Combine(installRoot, "extract");
        var currentRoot = Path.Combine(PathHelper.LocalToolsRoot, "mitmproxy", "current");

        if (!File.Exists(zipPath) || !HashMatches(zipPath, PinnedMitmproxyZipSha256))
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);

            LogLine($"Downloading mitmproxy {PinnedMitmproxyVersion} standalone ZIP from official mitmproxy downloads.");
            LogLine(PinnedMitmproxyZipUrl);
            await DownloadFileAsync(PinnedMitmproxyZipUrl, zipPath, cancellationToken);
        }
        else
        {
            LogLine($"Using existing verified download: {zipPath}");
        }

        var zipHash = ComputeSha256(zipPath);
        LogLine($"Downloaded ZIP SHA256: {zipHash}");
        if (!string.Equals(zipHash, PinnedMitmproxyZipSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloaded mitmproxy ZIP failed SHA-256 verification and will not be used.");

        if (Directory.Exists(extractRoot))
            Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);

        LogLine($"Extracting mitmproxy to: {extractRoot}");
        ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

        var exeDirectory = FindDirectoryContainingPinnedExecutables(extractRoot);
        LogLine($"Found mitmproxy executables in: {exeDirectory}");

        VerifyPinnedExecutables(exeDirectory);

        if (Directory.Exists(currentRoot))
            Directory.Delete(currentRoot, recursive: true);
        Directory.CreateDirectory(currentRoot);

        foreach (var exeName in PinnedMitmproxyExecutableHashes.Keys)
        {
            var source = Path.Combine(exeDirectory, exeName);
            var destination = Path.Combine(currentRoot, exeName);
            File.Copy(source, destination, overwrite: true);
        }

        LogLine($"Installed pinned mitmproxy tools to: {currentRoot}");
    }

    private static string FindDirectoryContainingPinnedExecutables(string root)
    {
        var mitmDump = Directory
            .EnumerateFiles(root, "mitmdump.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (mitmDump is null)
            throw new FileNotFoundException("mitmdump.exe was not found in the downloaded mitmproxy archive.");

        var directory = Path.GetDirectoryName(mitmDump) ?? throw new InvalidOperationException("Could not resolve mitmproxy executable directory.");
        foreach (var exeName in PinnedMitmproxyExecutableHashes.Keys)
        {
            if (!File.Exists(Path.Combine(directory, exeName)))
                throw new FileNotFoundException($"{exeName} was not found in the downloaded mitmproxy archive.");
        }

        return directory;
    }

    private void VerifyPinnedExecutables(string directory)
    {
        foreach (var expected in PinnedMitmproxyExecutableHashes)
        {
            var path = Path.Combine(directory, expected.Key);
            var actualHash = ComputeSha256(path);
            LogLine($"{expected.Key} SHA256: {actualHash}");

            if (!string.Equals(actualHash, expected.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{expected.Key} failed SHA-256 verification and will not be used.");
        }
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        await using var remote = await http.GetStreamAsync(url, cancellationToken);
        await using var local = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await remote.CopyToAsync(local, cancellationToken);
    }

    private static bool HashMatches(string path, string expectedSha256)
    {
        try
        {
            return string.Equals(ComputeSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private async Task<string> EnsureCaCertificateAsync(string mitmDumpPath, CancellationToken cancellationToken)
    {
        var certPath = GetMitmproxyCaCertificatePath();

        if (File.Exists(certPath))
        {
            LogLine($"mitmproxy CA certificate already exists: {certPath}");
            return certPath;
        }

        LogLine("Generating mitmproxy CA certificate using a short mitmdump run.");
        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);

        var port = _ports.ReserveNext();
        Process? process = null;
        try
        {
            var args = $"--listen-host 127.0.0.1 --listen-port {port}";
            process = WindowsProcessTools.StartInPrivateConsole(mitmDumpPath, args, AppContext.BaseDirectory);

            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline && !File.Exists(certPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken);
            }

            if (!File.Exists(certPath))
                throw new FileNotFoundException("mitmproxy CA certificate was not generated.", certPath);

            LogLine($"Generated CA certificate: {certPath}");
            return certPath;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                await Task.Run(() =>
                {
                    var stopped = WindowsProcessTools.TrySendCtrlC(process, TimeSpan.FromSeconds(4), out _);
                    if (!stopped && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit(5000)) throw new InvalidOperationException("Setup mitmdump exit could not be confirmed.");
                    }
                });
            }
            _ports.Release(port);
            process?.Dispose();
        }
    }

    private static string GetMitmproxyCaCertificatePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mitmproxy",
            "mitmproxy-ca-cert.cer");
    }

    private void InstallCertificate(string certPath, bool trustLocalMachine)
    {
        var location = trustLocalMachine ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        using var cert = X509CertificateLoader.LoadCertificateFromFile(certPath);
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault();

        if (existing is not null)
        {
            LogLine($"Certificate already trusted in {location}\\Root. Thumbprint: {cert.Thumbprint}");
            return;
        }

        store.Add(cert);
        LogLine($"Certificate trusted in {location}\\Root. Thumbprint: {cert.Thumbprint}");
    }

    private async Task TestRunsAsync(string exe, string arguments, CancellationToken cancellationToken)
    {
        LogLine($"Testing: {exe} {arguments}");
        await RunProcessRequiredAsync(exe, arguments, TimeSpan.FromSeconds(20), cancellationToken);
    }

    private async Task RunProcessRequiredAsync(string exe, string args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(exe, args, timeout, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Command failed: {exe} {args}\nExit code: {result.ExitCode}\n{result.Output}\n{result.Error}");
    }

    private async Task<ProcessResult> RunProcessAsync(string exe, string args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        var error = new List<string>();
        var sync = new object();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (sync) output.Add(e.Data); LogLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (sync) error.Add(e.Data); LogLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var waitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(waitTask, timeoutTask);

        if (completed == timeoutTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Timed out running: {exe} {args}");
        }

        await waitTask;
        await Task.Delay(100, CancellationToken.None);
        lock (sync)
            return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, output), string.Join(Environment.NewLine, error));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void LogLine(string message) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
}

public enum MitmproxyInstallMode
{
    StandaloneUserProfile,
    WindowsInstaller
}

public sealed record MitmproxyToolset(string? MitmProxyPath, string? MitmDumpPath, string? MitmWebPath)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(MitmProxyPath) &&
        !string.IsNullOrWhiteSpace(MitmDumpPath) &&
        !string.IsNullOrWhiteSpace(MitmWebPath);
}

internal sealed record ProcessResult(int ExitCode, string Output, string Error);

public sealed record SetupCheckResult(MitmproxyToolset Toolset, string CaCertificatePath, IReadOnlyList<string> Issues)
{
    public bool IsReady => Issues.Count == 0;
}
