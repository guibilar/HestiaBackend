using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Hestia.Testing;

/// <summary>
/// A TCP gate in front of the test broker (T37): a host that connects to <see cref="Address"/> reaches the broker only
/// while the gate is open. Closed, the port refuses connections, as a broker that is down. <see cref="Close"/> also
/// cuts the open connections, as a broker that stops.
/// </summary>
/// <remarks>
/// While the gate is closed, a socket that is bound to the port but does not listen keeps the port (T19): the system
/// refuses connections to it, and no other listener can take it. The test projects run at the same time, with many
/// local listeners (for example <see cref="FakeFinance"/>), so a released port can go to one of them, and the host then
/// reaches an HTTP server in place of a broker that is down.
/// </remarks>
public sealed class BrokerGate : IAsyncDisposable
{
    private readonly Uri _target;
    private readonly int _port;
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;

    /// <summary>Keeps the port while the gate is closed (bound, not listening).</summary>
    private Socket? _reservation;

    /// <param name="target">The broker address, for example <see cref="TestRabbitMq.Address"/>.</param>
    public BrokerGate(string target)
    {
        _target = new Uri(target);
        // A free port, kept by a socket that does not listen: nothing listens on it until Open.
        _reservation = Reserve(0);
        _port = ((IPEndPoint)_reservation.LocalEndPoint!).Port;
        var address = new UriBuilder(_target) { Host = "127.0.0.1", Port = _port };
        Address = address.Uri.ToString();
    }

    /// <summary>The broker address through the gate, with the user and the password of the target.</summary>
    public string Address { get; }

    public bool IsOpen => _listener is not null;

    /// <summary>Starts to accept connections and forwards them to the broker.</summary>
    public void Open()
    {
        if (_listener is not null)
        {
            return;
        }
        _reservation?.Dispose();
        _reservation = null;
        _stop = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        _ = AcceptAsync(_listener, _stop.Token);
    }

    /// <summary>Refuses new connections and cuts the open ones.</summary>
    public void Close()
    {
        _stop?.Cancel();
        var wasOpen = _listener is not null;
        _listener?.Stop();
        _listener = null;
        while (_connections.TryTake(out var connection))
        {
            connection.Dispose();
        }
        if (wasOpen && _reservation is null)
        {
            try
            {
                _reservation = Reserve(_port);
            }
            catch (SocketException)
            {
                // The port is busy for a moment after the listener stops. The gate is closed anyway.
            }
        }
    }

    /// <summary>A socket bound to <paramref name="port"/> on loopback, without a listen: connections to it are refused.</summary>
    private static Socket Reserve(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception) when (stop.IsCancellationRequested || listener.Server is not { IsBound: true })
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            _ = ForwardAsync(client, stop);
        }
    }

    private async Task ForwardAsync(TcpClient client, CancellationToken stop)
    {
        var broker = new TcpClient();
        _connections.Add(client);
        _connections.Add(broker);
        try
        {
            await broker.ConnectAsync(_target.Host, _target.Port, stop);
            var up = client.GetStream().CopyToAsync(broker.GetStream(), stop);
            var down = broker.GetStream().CopyToAsync(client.GetStream(), stop);
            await Task.WhenAny(up, down);
        }
        catch (Exception)
        {
            // A closed gate, or a side that went away.
        }
        finally
        {
            client.Dispose();
            broker.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        _reservation?.Dispose();
        _reservation = null;
        return ValueTask.CompletedTask;
    }
}
