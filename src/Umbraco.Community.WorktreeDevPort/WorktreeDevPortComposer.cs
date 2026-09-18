using System.Diagnostics;
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
    public const string ConfigKey = "worktreedevport.port";
    public const int DefaultBasePort = 44300;
    public const int DefaultRangeSize = 100;

    public void Configure(KestrelServerOptions options)
    {
        if (!hostEnvironment.IsDevelopment())
            return;

        // Respect an explicit URL/port if one was configured, rather than overriding it.
        var urls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"];
        if (!string.IsNullOrEmpty(urls))
        {
            foreach (var url in urls.Split(';'))
            {
                var uri = new Uri(url);
                options.Listen(IPAddress.Loopback, uri.Port, o =>
                {
                    if (uri.Scheme == "https")
                        o.UseHttps();
                });
            }
            return;
        }

        var basePort = configuration.GetValue("WorktreeDevPort:BasePort", DefaultBasePort);
        var rangeSize = configuration.GetValue("WorktreeDevPort:RangeSize", DefaultRangeSize);

        options.Listen(IPAddress.Loopback, GetOrAssignPort(basePort, rangeSize), o => o.UseHttps());
    }

    /// <summary>
    /// Returns the port already assigned to this worktree, or picks the first free port
    /// in range and saves it so future runs (and other tools) reuse the same one.
    /// </summary>
    public static int GetOrAssignPort(int basePort = DefaultBasePort, int rangeSize = DefaultRangeSize)
    {
        // Enables per-worktree config files; harmless if already set. This itself lives in
        // the shared .git/config, so it only ever needs to succeed once per clone.
        RunGit("config extensions.worktreeConfig true");

        var existing = RunGit($"config --worktree --get {ConfigKey}");
        if (int.TryParse(existing, out var existingPort))
            return existingPort;

        for (var port = basePort; port < basePort + rangeSize; port++)
        {
            if (IsPortFree(port))
            {
                RunGit($"config --worktree {ConfigKey} {port}");
                return port;
            }
        }

        throw new InvalidOperationException($"No free port found in range {basePort}-{basePort + rangeSize - 1}.");
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
