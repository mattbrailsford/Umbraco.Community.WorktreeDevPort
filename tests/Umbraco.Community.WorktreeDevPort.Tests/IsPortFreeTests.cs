using System.Net;
using System.Net.Sockets;

namespace Umbraco.Community.WorktreeDevPort.Tests;

public class IsPortFreeTests
{
    [Fact]
    public void A_port_in_use_is_not_free()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.False(WorktreeDevPortKestrelConfiguration.IsPortFree(port));
    }

    [Fact]
    public void A_port_is_free_again_once_released()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.True(WorktreeDevPortKestrelConfiguration.IsPortFree(port));
    }
}
