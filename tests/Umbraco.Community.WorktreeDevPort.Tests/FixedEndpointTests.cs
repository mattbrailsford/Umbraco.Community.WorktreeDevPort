namespace Umbraco.Community.WorktreeDevPort.Tests;

public class FixedEndpointTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    public void No_urls_means_no_fixed_endpoints(string? urls)
        => Assert.Null(WorktreeDevPortKestrelConfiguration.GetFixedEndpoints(urls));

    [Theory]
    [InlineData("https://127.0.0.1:0")]
    [InlineData("http://localhost:5000;https://localhost:0")]
    public void Any_port_zero_url_falls_back_to_the_worktree_port(string urls)
        => Assert.Null(WorktreeDevPortKestrelConfiguration.GetFixedEndpoints(urls));

    [Theory]
    [InlineData("https://localhost:5001", 5001, true)]
    [InlineData("http://localhost:5000", 5000, false)]
    [InlineData("HTTPS://LOCALHOST:5001", 5001, true)]
    [InlineData("https://localhost", 443, true)]
    [InlineData("http://localhost", 80, false)]
    [InlineData("http://localhost:5000/some/path", 5000, false)]
    [InlineData("http://*:5000", 5000, false)]
    [InlineData("https://+:443", 443, true)]
    [InlineData("http://0.0.0.0:5000", 5000, false)]
    [InlineData("http://[::]:5000", 5000, false)]
    [InlineData("http://[::1]:5000", 5000, false)]
    public void Reads_a_single_url(string url, int port, bool https)
        => Assert.Equal([(port, https)], WorktreeDevPortKestrelConfiguration.GetFixedEndpoints(url));

    [Fact]
    public void Reads_several_urls_and_trims_spaces()
        => Assert.Equal(
            [(5000, false), (5001, true)],
            WorktreeDevPortKestrelConfiguration.GetFixedEndpoints(" http://localhost:5000 ; https://localhost:5001 "));

    [Theory]
    [InlineData("localhost:5000")]
    [InlineData("ftp://localhost:21")]
    public void Throws_a_clear_error_for_an_unrecognized_url(string url)
    {
        var error = Assert.Throws<FormatException>(() => WorktreeDevPortKestrelConfiguration.GetFixedEndpoints(url));
        Assert.Contains(url, error.Message);
    }
}
