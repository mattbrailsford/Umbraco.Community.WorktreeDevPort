namespace Umbraco.Community.WorktreeDevPort.Tests;

/// <summary>
/// Runs against real throwaway git repos and worktrees. Whether a port is free is decided by
/// each test, so no real ports are touched and tests can run in parallel.
/// </summary>
public sealed class GetOrAssignPortTests : IDisposable
{
    private const int BasePort = 50000;
    private const int RangeSize = 10;
    private const int MainPort = 50005;

    private static readonly Func<int, bool> AllFree = _ => true;

    private readonly TempRepo _repo = new();

    public void Dispose() => _repo.Dispose();

    private static Func<int, bool> AllFreeExcept(params int[] busy) => port => !busy.Contains(port);

    private static int Assign(string checkout, Func<int, bool>? isPortFree = null, int? mainPort = MainPort, int basePort = BasePort, int rangeSize = RangeSize)
        => WorktreeDevPortKestrelConfiguration.GetOrAssignPort(checkout, basePort, rangeSize, mainPort, isPortFree ?? AllFree);

    [Fact]
    public void Main_checkout_gets_the_reserved_port_when_free()
    {
        Assert.Equal(MainPort, Assign(_repo.MainCheckout));
        Assert.Equal($"{MainPort}\n", File.ReadAllText(_repo.PortFile(_repo.MainCheckout)));
    }

    [Fact]
    public void Main_checkout_falls_back_to_the_pool_when_the_reserved_port_is_busy()
        => Assert.Equal(BasePort, Assign(_repo.MainCheckout, AllFreeExcept(MainPort)));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Main_checkout_uses_the_pool_when_the_reserved_port_is_turned_off(int? mainPort)
        => Assert.Equal(BasePort, Assign(_repo.MainCheckout, mainPort: mainPort));

    [Fact]
    public void Linked_worktree_never_gets_the_reserved_port()
    {
        var worktree = _repo.AddWorktree("wt");

        Assert.Equal(BasePort, Assign(worktree));
    }

    [Fact]
    public void Linked_worktree_skips_the_reserved_port_inside_the_pool()
    {
        var worktree = _repo.AddWorktree("wt");
        var everythingButReservedBusy = (Func<int, bool>)(port => port == MainPort);

        Assert.Throws<InvalidOperationException>(() => Assign(worktree, everythingButReservedBusy));
    }

    [Fact]
    public void New_worktree_does_not_inherit_the_main_checkouts_port()
    {
        Assert.Equal(MainPort, Assign(_repo.MainCheckout));

        var worktree = _repo.AddWorktree("wt");

        Assert.False(File.Exists(_repo.PortFile(worktree)));
        Assert.Equal(BasePort, Assign(worktree));
    }

    [Fact]
    public void Worktrees_skip_ports_other_worktrees_have_saved_even_when_free()
    {
        var first = _repo.AddWorktree("wt1");
        var second = _repo.AddWorktree("wt2");

        Assert.Equal(BasePort, Assign(first));
        Assert.Equal(BasePort + 1, Assign(second));
    }

    [Fact]
    public void Main_checkout_skips_a_pool_port_a_worktree_has_saved()
    {
        Assert.Equal(BasePort, Assign(_repo.AddWorktree("wt")));

        Assert.Equal(BasePort + 1, Assign(_repo.MainCheckout, AllFreeExcept(MainPort)));
    }

    [Fact]
    public void A_saved_port_is_reused_without_picking_again()
    {
        File.WriteAllText(_repo.PortFile(_repo.MainCheckout), "50003\n");

        Assert.Equal(50003, Assign(_repo.MainCheckout, _ => throw new InvalidOperationException("Should not probe ports.")));
    }

    [Fact]
    public void Each_call_returns_the_same_port()
    {
        var worktree = _repo.AddWorktree("wt");

        Assert.Equal(Assign(worktree), Assign(worktree));
    }

    [Theory]
    [MemberData(nameof(SavedPortCase.All), MemberType = typeof(SavedPortCase))]
    public void Saved_text_is_reused_only_when_it_is_a_valid_port(SavedPortCase testCase)
    {
        var portFile = _repo.PortFile(_repo.MainCheckout);
        File.WriteAllText(portFile, testCase.Text);

        var port = Assign(_repo.MainCheckout, mainPort: null, basePort: 40000, rangeSize: 10);

        Assert.Equal(testCase.Port ?? 40000, port);
        if (testCase.Port is null)
            Assert.Equal("40000\n", File.ReadAllText(portFile));
    }

    [Fact]
    public void A_junk_port_saved_by_another_worktree_does_not_block_anything()
    {
        var first = _repo.AddWorktree("wt1");
        File.WriteAllText(_repo.PortFile(first), "junk");

        Assert.Equal(BasePort, Assign(_repo.AddWorktree("wt2")));
    }

    [Fact]
    public void A_full_range_throws_a_clear_error()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Assign(_repo.MainCheckout, _ => false));

        Assert.Contains($"{BasePort}-{BasePort + RangeSize - 1}", error.Message);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(50000, 0)]
    [InlineData(65530, 10)]
    public void An_impossible_range_throws(int basePort, int rangeSize)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Assign(_repo.MainCheckout, basePort: basePort, rangeSize: rangeSize));

    [Fact]
    public void Outside_a_git_repo_still_returns_a_port_and_saves_nothing()
    {
        var folder = _repo.AddPlainFolder("plain");

        Assert.Equal(MainPort, Assign(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [Fact]
    public void Worktrees_starting_at_the_same_moment_get_different_ports()
    {
        var worktrees = Enumerable.Range(1, 6).Select(i => _repo.AddWorktree($"wt{i}")).ToList();

        // A slow port check widens the gap between picking a port and saving it.
        var slowAllFree = (Func<int, bool>)(_ => { Thread.Sleep(20); return true; });
        using var start = new Barrier(worktrees.Count);

        var ports = worktrees
            .AsParallel()
            .WithDegreeOfParallelism(worktrees.Count)
            .Select(worktree =>
            {
                start.SignalAndWait();
                return Assign(worktree, slowAllFree);
            })
            .ToList();

        Assert.Equal(worktrees.Count, ports.Distinct().Count());
    }

    [Fact]
    public void The_lock_is_released_afterwards()
    {
        Assign(_repo.MainCheckout);

        Assert.False(File.Exists(LockFile()));
    }

    [Fact]
    public void A_lock_left_by_a_crashed_process_is_ignored()
    {
        var lockFile = LockFile();
        File.WriteAllText(lockFile, "");
        File.SetLastWriteTimeUtc(lockFile, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(MainPort, Assign(_repo.MainCheckout));
    }

    private string LockFile()
        => Path.Combine(
            Path.GetFullPath(Path.Combine(_repo.MainCheckout, TempRepo.Git(_repo.MainCheckout, "rev-parse", "--git-common-dir"))),
            WorktreeDevPortKestrelConfiguration.LockFileName);

    [Fact]
    public void Git_alias_prints_the_saved_port_in_every_worktree()
    {
        var worktree = _repo.AddWorktree("wt");

        var mainPort = Assign(_repo.MainCheckout);
        var worktreePort = Assign(worktree);

        Assert.Equal(mainPort.ToString(), TempRepo.Git(_repo.MainCheckout, "wdp-port"));
        Assert.Equal(worktreePort.ToString(), TempRepo.Git(worktree, "wdp-port"));
    }

    [Fact]
    public void Git_alias_is_written_once()
    {
        Assign(_repo.MainCheckout);
        Assign(_repo.AddWorktree("wt"));

        var values = TempRepo.Git(_repo.MainCheckout, "config", "--get-all", $"alias.{WorktreeDevPortKestrelConfiguration.GitAliasName}");
        Assert.Single(values.Split('\n'));
    }
}
