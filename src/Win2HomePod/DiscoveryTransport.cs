using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinPlay.Core.Dns;
using WinPlay.Core.Mdns;

namespace Win2HomePod;

// Direct DNS-SD queries use an ephemeral source port. Stateful firewalls can return
// these replies even when unsolicited multicast reception on shared :5353 is blocked.
// Hints only help discovery; audio targets always come from fresh DNS-SD responses.
internal sealed class DiscoveryTransport : IMdnsTransport
{
    private readonly MdnsClient? multicast;
    private readonly Socket direct = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly CancellationTokenSource cancel = new();
    private readonly IPAddress[] hints;
    private bool started;
    public event Action<DnsMessage, IPEndPoint>? MessageReceived;

    public DiscoveryTransport()
    {
        direct.Bind(new IPEndPoint(IPAddress.Any, 0));
        try
        {
            var path = Path.Combine(Program.State, "receiver-hints.json");
            hints = (File.Exists(path) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [] : [])
                .Select(IPAddress.Parse).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        }
        catch (Exception ex) { hints = []; Program.Log($"Discovery hints unavailable: {ex.Message}"); }
        try
        {
            multicast = new MdnsClient();
            multicast.MessageReceived += (message, endpoint) => MessageReceived?.Invoke(message, endpoint);
        }
        catch (Exception ex) { Program.Log($"Multicast unavailable; using direct discovery: {ex.Message}"); }
    }
    public void Start()
    {
        if (started) return;
        started = true;
        multicast?.Start();
        _ = Task.Run(Receive);
    }
    public void Query(IReadOnlyList<(string Name, DnsType Type, bool UnicastResponse)> questions)
    {
        try { multicast?.Query(questions); }
        catch (SocketException ex) { Program.Log($"Multicast query: {ex.Message}"); }
        var packet = DnsQueryWriter.BuildQuery(questions.Select(q => (q.Name, q.Type, true)).ToArray());
        foreach (var address in hints)
        {
            try { direct.SendTo(packet, new IPEndPoint(address, 5353)); }
            catch (SocketException ex) { Program.Log($"Direct discovery {address}: {ex.Message}"); }
        }
    }
    private async Task Receive()
    {
        var buffer = new byte[9000];
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                var result = await direct.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancel.Token);
                var endpoint = (IPEndPoint)result.RemoteEndPoint;
                if (endpoint.Port != 5353 || !hints.Contains(endpoint.Address)) continue;
                MessageReceived?.Invoke(DnsMessage.Parse(buffer.AsSpan(0, result.ReceivedBytes)), endpoint);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (FormatException) { }
            catch (SocketException ex) { Program.Log($"Direct discovery receive: {ex.Message}"); }
        }
    }
    public void Dispose()
    {
        cancel.Cancel();
        direct.Dispose();
        multicast?.Dispose();
    }
}
