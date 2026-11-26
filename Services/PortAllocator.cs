using System.Net.NetworkInformation;

namespace TapRoom.Services;

public sealed class PortAllocator
{
    private readonly object _lock = new();
    private readonly HashSet<int> _reserved = new();

    public int StartPort { get; }
    public int EndPort { get; }

    public PortAllocator(int startPort = 9000, int endPort = 9999)
    {
        StartPort = startPort;
        EndPort = endPort;
    }

    public int ReserveNext()
    {
        lock (_lock)
        {
            var active = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(e => e.Port)
                .ToHashSet();

            for (var port = StartPort; port <= EndPort; port++)
            {
                if (!_reserved.Contains(port) && !active.Contains(port))
                {
                    _reserved.Add(port);
                    return port;
                }
            }
        }

        throw new InvalidOperationException($"No free TCP ports found in range {StartPort}-{EndPort}.");
    }

    public void Release(int port)
    {
        lock (_lock)
        {
            _reserved.Remove(port);
        }
    }
}
