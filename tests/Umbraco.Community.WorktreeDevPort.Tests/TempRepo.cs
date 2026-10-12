using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Umbraco.Community.WorktreeDevPort.Tests;

/// <summary>
/// A throwaway git repo (one commit, so worktrees can be added) under the temp folder.
/// </summary>
internal sealed class TempRepo : IDisposable
{
    private static readonly string TestsRoot = Path.Combine(Path.GetTempPath(), "wdp-tests");

    private readonly string _root;

    public TempRepo()
    {
        _root = Path.Combine(TestsRoot, Guid.NewGuid().ToString("N"));
        MainCheckout = Path.Combine(_root, "main");
        Directory.CreateDirectory(MainCheckout);

        Git(MainCheckout, "init", "-q");
        Git(MainCheckout, "-c", "user.name=wdp", "-c", "user.email=wdp@example.com", "commit", "-q", "--allow-empty", "-m", "init");
    }

    public string MainCheckout { get; }

    public string AddWorktree(string name)
    {
        var path = Path.Combine(_root, name);
        Git(MainCheckout, "worktree", "add", "-q", "--detach", path);
        return path;
    }

    /// <summary>
    /// A folder next to the repo that isn't inside any git repo.
    /// </summary>
    public string AddPlainFolder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string PortFile(string checkout)
        => Path.GetFullPath(Path.Combine(checkout, Git(checkout, "rev-parse", "--git-path", WorktreeDevPortKestrelConfiguration.PortFileName)));

    public static string Git(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");

        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Keeps the developer's own git config (signing, hooks, default branch) out of every git
    /// call in this test run, including the ones the package itself makes, and stops git from
    /// finding a repo above the temp folder.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateGit()
    {
        Directory.CreateDirectory(TestsRoot);
        var emptyConfig = Path.Combine(TestsRoot, "empty.gitconfig");
        File.WriteAllText(emptyConfig, "");

        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", emptyConfig);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", TestsRoot);
    }
}
