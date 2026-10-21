using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlHub.Services.Devices;

internal static partial class Program
{
    private static async Task CheckE4981ATcpBoundariesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var settings = new TcpConnectionSettings
        {
            Host = IPAddress.Loopback.ToString(), Port = ((IPEndPoint)listener.LocalEndpoint).Port
        };
        using var client = new E4981ATcpClient();
        var closed = 0;
        client.ConnectionClosed += _ => closed++;

        // No response, a partial response, and cancellation all lose request/response alignment.
        foreach (var mode in new[] { "timeout", "partial-timeout", "cancel" })
        {
            await client.ConnectAsync(settings, deadline.Token);
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(socket.GetStream());
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var pending = client.QueryAsync("*TRG", 500, requestCancellation.Token);
            Require(await reader.ReadLineAsync(deadline.Token) == "*TRG", "Missing trigger on loopback.");
            if (mode == "partial-timeout")
                await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("0,4.00"), deadline.Token);
            if (mode == "cancel") requestCancellation.Cancel();
            try
            {
                await pending;
                throw new Exception($"{mode}: unfinished response was accepted.");
            }
            catch (TimeoutException) when (mode != "cancel") { }
            catch (OperationCanceledException) when (mode == "cancel") { }
            Require(!client.IsConnected, $"{mode}: stale connection remained usable.");
            try
            {
                await client.QueryAsync("*TRG", 500, deadline.Token);
                throw new Exception("A new test must require reconnection after an unfinished response.");
            }
            catch (InvalidOperationException) { }

            // A late old-product response must not be used on a newly connected stream.
            try { await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("0,4E-7,0.2,2\n"), deadline.Token); }
            catch (IOException) { }
        }
        Require(closed == 3, "Every interrupted in-flight command must notify connection closure exactly once.");

        await client.ConnectAsync(settings, deadline.Token);
        using (var socket = await listener.AcceptTcpClientAsync(deadline.Token))
        using (var reader = new StreamReader(socket.GetStream()))
        {
            var pending = client.QueryAsync("*TRG", 1000, deadline.Token);
            Require(await reader.ReadLineAsync(deadline.Token) == "*TRG", "Reconnect did not send a fresh trigger.");
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("2,3E-12"), deadline.Token);
            await Task.Delay(20, deadline.Token);
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(",0.2,11\r\n"), deadline.Token);
            Require(await pending == "2,3E-12,0.2,11", "Split response or reconnection retained old-product bytes.");

            // Cancellation while waiting for the lock has not sent anything and must preserve the active query.
            pending = client.QueryAsync("*TRG", 1000, deadline.Token);
            Require(await reader.ReadLineAsync(deadline.Token) == "*TRG", "Missing active trigger.");
            using var queuedCancellation = new CancellationTokenSource();
            var queued = client.QueryAsync("*TRG", 1000, queuedCancellation.Token);
            queuedCancellation.Cancel();
            try { await queued; throw new Exception("Queued cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Require(client.IsConnected, "An unsent cancelled query closed the healthy active query.");
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("0,4E-7,0.2,2\n"), deadline.Token);
            Require(await pending == "0,4E-7,0.2,2", "Queued cancellation damaged active response.");

            // Extra complete responses cannot be silently consumed by subsequent tests.
            pending = client.QueryAsync("*TRG", 1000, deadline.Token);
            Require(await reader.ReadLineAsync(deadline.Token) == "*TRG", "Missing trigger before extra response.");
            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("0,4E-7,0.2,2\n0,4E-7,0.2,2\n"), deadline.Token);
            await pending;
            try
            {
                await client.QueryAsync("*TRG", 500, deadline.Token);
                throw new Exception("An extra old response was accepted as the next measurement.");
            }
            catch (InvalidDataException) { }
            Require(!client.IsConnected && closed == 4, "Unexpected buffered response must invalidate the stream.");
        }
        Console.WriteLine("PASS: E4981A missing/partial/late responses, timeout/cancellation disconnect, clean reconnect, split TCP frames, queued cancellation and residual-response rejection.");
    }

}
