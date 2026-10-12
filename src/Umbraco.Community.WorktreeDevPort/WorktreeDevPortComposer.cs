using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace Umbraco.Community.WorktreeDevPort;

/// <summary>
/// Makes Kestrel listen on a port assigned to the current git worktree, so every worktree
/// gets its own stable dev port that any tool can look up with a plain <c>git wdp-port</c> call.
/// Only active when the host is running in Development.
/// </summary>
public class WorktreeDevPortComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>, WorktreeDevPortKestrelConfiguration>();
    }
}

/// <summary>
/// Assigns and reuses a port per git worktree, stored in a file in that worktree's own git dir
/// (<c>.git/wdp-port</c>, or <c>.git/worktrees/&lt;name&gt;/wdp-port</c> for a linked worktree), so
/// the value never touches the working tree and is cleaned up automatically when the worktree
/// is removed. Unlike <c>git config --worktree</c>, <c>git worktree add</c> never copies this file
/// into a new worktree, so a worktree can only ever see a port it assigned itself.
/// </summary>
public class WorktreeDevPortKestrelConfiguration(IHostEnvironment hostEnvironment, IConfiguration configuration)
    : IConfigureOptions<KestrelServerOptions>
{
    /// <summary>
    /// The file, inside each worktree's own git dir, that holds its port.
    /// </summary>
    public const string PortFileName = "wdp-port";

    /// <summary>
    /// The file, inside the repo's shared git dir, that only one worktree at a time can hold
    /// while it picks and saves a port.
    /// </summary>
    public const string LockFileName = "wdp-port.lock";

    /// <summary>
    /// The git alias this package adds to the repo's shared config, so any tool can read the
    /// current worktree's port with <c>git wdp-port</c>.
    /// </summary>
    public const string GitAliasName = "wdp-port";

    public const int DefaultBasePort = 44300;
    public const int DefaultRangeSize = 100;

    /// <summary>
    /// The port reserved for the main checkout (not a linked worktree), used when free.
    /// Set to <c>null</c> to disable this and always use the auto-assigned pool.
    /// </summary>
    public const int DefaultMainWorktreePort = 44355;

    private const int MaxPort = 65535;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A lock older than this was left behind by a process that died while holding it.
    /// </summary>
    private static readonly TimeSpan StaleLockAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Matches an ASP.NET Core URL such as <c>https://localhost:5001</c>. Unlike <see cref="Uri"/>,
    /// it also accepts the wildcard hosts ASP.NET Core allows (<c>http://*:5000</c>, <c>https://+:443</c>).
    /// </summary>
    private static readonly Regex UrlPattern = new(
        @"^(?<scheme>https?)://(?<host>\[[^\]]*\]|[^:/]+)(?::(?<port>\d{1,5}))?(?:/.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public void Configure(KestrelServerOptions options)
    {
        if (!hostEnvironment.IsDevelopment())
            return;

        var fixedEndpoints = GetFixedEndpoints(configuration["ASPNETCORE_URLS"] ?? configuration["urls"]);
        if (fixedEndpoints is not null)
        {
            foreach (var (port, https) in fixedEndpoints)
            {
                options.Listen(IPAddress.Loopback, port, o =>
                {
                    if (https)
                        o.UseHttps();
                });
            }
            return;
        }

        var basePort = configuration.GetValue("WorktreeDevPort:BasePort", DefaultBasePort);
        var rangeSize = configuration.GetValue("WorktreeDevPort:RangeSize", DefaultRangeSize);
        var mainWorktreePort = configuration.GetValue<int?>("WorktreeDevPort:MainWorktreePort", DefaultMainWorktreePort);

        options.Listen(IPAddress.Loopback, GetOrAssignPort(basePort, rangeSize, mainWorktreePort), o => o.UseHttps());
    }

    /// <summary>
    /// Reads explicit fixed URLs/ports, so they're respected rather than overridden. Returns
    /// <c>null</c> when none are configured, or when any of them uses a ":0" port. A ":0" port
    /// means "let the OS pick a dynamic one" -- that's exactly what this package exists to
    /// replace, so it's treated the same as no URL being configured at all.
    /// </summary>
    internal static IReadOnlyList<(int Port, bool Https)>? GetFixedEndpoints(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
            return null;

        var endpoints = new List<(int Port, bool Https)>();

        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = UrlPattern.Match(url);
            if (!match.Success)
                throw new FormatException($"Unrecognized URL '{url}'. Expected a form like 'https://localhost:5001'.");

            var https = string.Equals(match.Groups["scheme"].Value, "https", StringComparison.OrdinalIgnoreCase);
            var port = match.Groups["port"].Success
                ? int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture)
                : https ? 443 : 80;

            if (port == 0)
                return null;

            endpoints.Add((port, https));
        }

        return endpoints.Count > 0 ? endpoints : null;
    }

    /// <summary>
    /// Returns the port already assigned to this worktree, or picks one and saves it so future
    /// runs (and other tools) reuse the same one. The main checkout gets <paramref name="mainWorktreePort"/>
    /// when it's free; every worktree gets the first free port from <paramref name="basePort"/> up,
    /// skipping <paramref name="mainWorktreePort"/> so it stays reserved, and skipping any port
    /// another worktree already has saved.
    /// </summary>
    public static int GetOrAssignPort(int basePort = DefaultBasePort, int rangeSize = DefaultRangeSize, int? mainWorktreePort = DefaultMainWorktreePort)
        => GetOrAssignPort(null, basePort, rangeSize, mainWorktreePort, IsPortFree);

    /// <summary>
    /// Same as <see cref="GetOrAssignPort(int, int, int?)"/>, but runs git in
    /// <paramref name="workingDirectory"/> (the current directory when <c>null</c>) and asks
    /// <paramref name="isPortFree"/> whether a port can be used.
    /// </summary>
    internal static int GetOrAssignPort(string? workingDirectory, int basePort, int rangeSize, int? mainWorktreePort, Func<int, bool> isPortFree)
    {
        if (basePort < 1 || rangeSize < 1 || basePort + rangeSize - 1 > MaxPort)
            throw new ArgumentOutOfRangeException(nameof(basePort), $"Port range {basePort}-{basePort + rangeSize - 1} must sit within 1-{MaxPort}.");

        EnsureGitAlias(workingDirectory);

        var gitDirs = GetGitDirs(workingDirectory);
        var portFile = gitDirs is { } dirs ? Path.Combine(dirs.GitDir, PortFileName) : null;

        if (portFile is not null && ReadPortFile(portFile) is int existingPort)
            return existingPort;

        // Picking and saving must happen as one step, or two worktrees starting at the same
        // moment can both see the same port as free and both save it.
        using var assignLock = gitDirs is { } l ? AcquireAssignLock(l.CommonDir) : null;

        // Another process in this same worktree may have saved a port while we waited.
        if (portFile is not null && ReadPortFile(portFile) is int savedWhileWaiting)
            return savedWhileWaiting;

        var isLinked = gitDirs is { } d && !PathEquals(d.GitDir, d.CommonDir);
        var takenByOthers = gitDirs is { } o ? GetPortsSavedByOtherWorktrees(o.GitDir, o.CommonDir) : [];

        // null and 0 both mean "no reserved port", matching the Node package.
        if (mainWorktreePort is int fixedPort and > 0 && !isLinked && isPortFree(fixedPort))
            return SavePort(portFile, fixedPort);

        for (var port = basePort; port < basePort + rangeSize; port++)
        {
            if (port == mainWorktreePort || takenByOthers.Contains(port))
                continue;

            if (isPortFree(port))
                return SavePort(portFile, port);
        }

        throw new InvalidOperationException($"No free port found in range {basePort}-{basePort + rangeSize - 1}.");
    }

    /// <summary>
    /// Takes the repo-wide lock, waiting for whoever holds it. The lock file is deleted when
    /// the returned handle is disposed. One left behind by a crashed process is removed once
    /// it's older than <see cref="StaleLockAge"/>.
    /// </summary>
    private static IDisposable AcquireAssignLock(string commonDir)
    {
        var lockFile = Path.Combine(commonDir, LockFileName);
        var deadline = DateTime.UtcNow + LockTimeout;

        while (true)
        {
            try
            {
                return new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                if (File.Exists(lockFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFile) > StaleLockAge)
                {
                    try { File.Delete(lockFile); }
                    catch (IOException) { }
                    continue;
                }

                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Another process held '{lockFile}' for over {LockTimeout.TotalSeconds:0} seconds. If nothing else is starting, delete that file.", ex);
            }
        }
    }

    /// <summary>
    /// Parses a saved port. Only plain digits in the range 1-65535 count; anything else
    /// (junk, a sign, a trailing suffix, 0) is treated as no saved port, so a fresh one is picked.
    /// </summary>
    internal static int? ParsePort(string text)
        => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= MaxPort
            ? port
            : null;

    private static int? ReadPortFile(string file)
        => File.Exists(file) ? ParsePort(File.ReadAllText(file)) : null;

    private static int SavePort(string? portFile, int port)
    {
        if (portFile is not null)
            File.WriteAllText(portFile, $"{port}\n");

        return port;
    }

    /// <summary>
    /// Adds <c>git wdp-port</c> to the repo's shared config. It lives in the shared .git/config,
    /// so every worktree gets it, and it only needs writing once per clone.
    /// </summary>
    private static void EnsureGitAlias(string? workingDirectory)
    {
        var command = $"!cat \"$(git rev-parse --git-path {PortFileName})\" 2>/dev/null";
        if (RunGit(workingDirectory, "config", "--get", $"alias.{GitAliasName}") != command)
            RunGit(workingDirectory, "config", $"alias.{GitAliasName}", command);
    }

    /// <summary>
    /// The port saved by every other worktree of this repo (main checkout included).
    /// </summary>
    private static HashSet<int> GetPortsSavedByOtherWorktrees(string gitDir, string commonDir)
    {
        var portFiles = new List<string> { Path.Combine(commonDir, PortFileName) };

        var worktreesDir = Path.Combine(commonDir, "worktrees");
        if (Directory.Exists(worktreesDir))
            portFiles.AddRange(Directory.EnumerateDirectories(worktreesDir).Select(dir => Path.Combine(dir, PortFileName)));

        var ownPortFile = Path.Combine(gitDir, PortFileName);
        var ports = new HashSet<int>();

        foreach (var file in portFiles)
        {
            if (PathEquals(file, ownPortFile))
                continue;

            if (ReadPortFile(file) is int port)
                ports.Add(port);
        }

        return ports;
    }

    /// <summary>
    /// For the main checkout, --git-dir and --git-common-dir are the same path. A linked
    /// worktree has its own private --git-dir under the shared repo's .git/worktrees/&lt;name&gt;,
    /// so the two differ. Comparing them avoids relying on "worktrees" appearing in the path,
    /// which the repo's own folder name could also trigger by coincidence.
    /// </summary>
    private static (string GitDir, string CommonDir)? GetGitDirs(string? workingDirectory)
    {
        var gitDir = RunGit(workingDirectory, "rev-parse", "--path-format=absolute", "--git-dir");
        var commonDir = RunGit(workingDirectory, "rev-parse", "--path-format=absolute", "--git-common-dir");

        // No git repo (or git unavailable) here at all -- treat like the main checkout
        // rather than throwing, matching RunGit's own fail-safe-empty behavior.
        if (string.IsNullOrEmpty(gitDir) || string.IsNullOrEmpty(commonDir))
            return null;

        return (Path.GetFullPath(gitDir), Path.GetFullPath(commonDir));
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    internal static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string RunGit(string? workingDirectory, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null)
                return "";

            // Drain stderr alongside stdout so a chatty git can't fill the pipe and hang.
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            _ = stderr.Result;

            return process.ExitCode == 0 ? stdout : "";
        }
        catch
        {
            return "";
        }
    }
}
