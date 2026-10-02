using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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

    public void Configure(KestrelServerOptions options)
    {
        if (!hostEnvironment.IsDevelopment())
            return;

        // Respect explicit fixed URLs/ports, rather than overriding them. A ":0" port means
        // "give the OS pick a dynamic one" -- that's exactly what this package exists to
        // replace, so treat it the same as no URL being configured at all.
        var urls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"];
        if (!string.IsNullOrEmpty(urls))
        {
            var parsedUrls = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(u => new Uri(u)).ToList();

            if (parsedUrls.Count > 0 && parsedUrls.All(u => u.Port != 0))
            {
                foreach (var uri in parsedUrls)
                {
                    options.Listen(IPAddress.Loopback, uri.Port, o =>
                    {
                        if (uri.Scheme == "https")
                            o.UseHttps();
                    });
                }
                return;
            }
        }

        var basePort = configuration.GetValue("WorktreeDevPort:BasePort", DefaultBasePort);
        var rangeSize = configuration.GetValue("WorktreeDevPort:RangeSize", DefaultRangeSize);
        var mainWorktreePort = configuration.GetValue<int?>("WorktreeDevPort:MainWorktreePort", DefaultMainWorktreePort);

        options.Listen(IPAddress.Loopback, GetOrAssignPort(basePort, rangeSize, mainWorktreePort), o => o.UseHttps());
    }

    /// <summary>
    /// Returns the port already assigned to this worktree, or picks one and saves it so future
    /// runs (and other tools) reuse the same one. The main checkout gets <paramref name="mainWorktreePort"/>
    /// when it's free; every worktree gets the first free port from <paramref name="basePort"/> up,
    /// skipping <paramref name="mainWorktreePort"/> so it stays reserved, and skipping any port
    /// another worktree already has saved.
    /// </summary>
    public static int GetOrAssignPort(int basePort = DefaultBasePort, int rangeSize = DefaultRangeSize, int? mainWorktreePort = DefaultMainWorktreePort)
    {
        EnsureGitAlias();

        var gitDirs = GetGitDirs();
        var portFile = gitDirs is { } dirs ? Path.Combine(dirs.GitDir, PortFileName) : null;

        if (portFile is not null && File.Exists(portFile) && int.TryParse(File.ReadAllText(portFile).Trim(), out var existingPort))
            return existingPort;

        var isLinked = gitDirs is { } d && !PathEquals(d.GitDir, d.CommonDir);
        var takenByOthers = gitDirs is { } o ? GetPortsSavedByOtherWorktrees(o.GitDir, o.CommonDir) : [];

        if (mainWorktreePort is int fixedPort && !isLinked && IsPortFree(fixedPort))
            return SavePort(portFile, fixedPort);

        for (var port = basePort; port < basePort + rangeSize; port++)
        {
            if (port == mainWorktreePort || takenByOthers.Contains(port))
                continue;

            if (IsPortFree(port))
                return SavePort(portFile, port);
        }

        throw new InvalidOperationException($"No free port found in range {basePort}-{basePort + rangeSize - 1}.");
    }

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
    private static void EnsureGitAlias()
    {
        var command = $"!cat \"$(git rev-parse --git-path {PortFileName})\" 2>/dev/null";
        if (RunGit("config", "--get", $"alias.{GitAliasName}") != command)
            RunGit("config", $"alias.{GitAliasName}", command);
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
            if (PathEquals(file, ownPortFile) || !File.Exists(file))
                continue;

            if (int.TryParse(File.ReadAllText(file).Trim(), out var port))
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
    private static (string GitDir, string CommonDir)? GetGitDirs()
    {
        var gitDir = RunGit("rev-parse", "--path-format=absolute", "--git-dir");
        var commonDir = RunGit("rev-parse", "--path-format=absolute", "--git-common-dir");

        // No git repo (or git unavailable) here at all -- treat like the main checkout
        // rather than throwing, matching RunGit's own fail-safe-empty behavior.
        if (string.IsNullOrEmpty(gitDir) || string.IsNullOrEmpty(commonDir))
            return null;

        return (Path.GetFullPath(gitDir), Path.GetFullPath(commonDir));
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    private static bool IsPortFree(int port)
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

    private static string RunGit(params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
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
