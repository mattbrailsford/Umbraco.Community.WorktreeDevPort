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
/// gets its own stable dev port that any tool can look up with a plain <c>git config</c> call.
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
/// Assigns and reuses a port per git worktree, stored in that worktree's own git config
/// (<c>git config --worktree</c>), so the value never touches the working tree and is
/// cleaned up automatically when the worktree is removed.
/// </summary>
public class WorktreeDevPortKestrelConfiguration(IHostEnvironment hostEnvironment, IConfiguration configuration)
    : IConfigureOptions<KestrelServerOptions>
{
    public const string ConfigKey = "wdp.port";
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
    /// when it's free and not already claimed; every worktree gets the first free port from
    /// <paramref name="basePort"/> up, skipping <paramref name="mainWorktreePort"/> so it stays reserved.
    /// </summary>
    public static int GetOrAssignPort(int basePort = DefaultBasePort, int rangeSize = DefaultRangeSize, int? mainWorktreePort = DefaultMainWorktreePort)
    {
        // Enables per-worktree config files; harmless if already set. This itself lives in
        // the shared .git/config, so it only ever needs to succeed once per clone.
        RunGit("config extensions.worktreeConfig true");

        var existing = RunGit($"config --worktree --get {ConfigKey}");
        if (int.TryParse(existing, out var existingPort))
            return existingPort;

        if (mainWorktreePort is int fixedPort && !IsLinkedWorktree() && IsPortFree(fixedPort))
        {
            RunGit($"config --worktree {ConfigKey} {fixedPort}");
            return fixedPort;
        }

        for (var port = basePort; port < basePort + rangeSize; port++)
        {
            if (port == mainWorktreePort)
                continue;

            if (IsPortFree(port))
            {
                RunGit($"config --worktree {ConfigKey} {port}");
                return port;
            }
        }

        throw new InvalidOperationException($"No free port found in range {basePort}-{basePort + rangeSize - 1}.");
    }

    private static bool IsLinkedWorktree()
    {
        // For the main checkout, --git-dir and --git-common-dir are the same path.
        // A linked worktree has its own private --git-dir under the shared repo's
        // .git/worktrees/<name>, so the two differ. Comparing them avoids relying
        // on "worktrees" appearing in the path, which the repo's own folder name could
        // also trigger by coincidence.
        var gitDir = RunGit("rev-parse --git-dir");
        var commonDir = RunGit("rev-parse --git-common-dir");

        // No git repo (or git unavailable) here at all -- treat like the main checkout
        // rather than throwing, matching RunGit's own fail-safe-empty behavior.
        if (string.IsNullOrEmpty(gitDir) || string.IsNullOrEmpty(commonDir))
            return false;

        return !string.Equals(Path.GetFullPath(gitDir), Path.GetFullPath(commonDir), StringComparison.Ordinal);
    }

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

    private static string RunGit(string args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process?.StandardOutput.ReadToEnd().Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
