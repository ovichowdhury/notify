using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Notify.Test.TestSupport;

public sealed record FakeSmtpMessage(string? From, IReadOnlyList<string> Recipients, string Data);

/// <summary>
/// Minimal in-process SMTP server (plain text, no TLS). Accepts every message unless
/// <see cref="RejectRecipient"/> says otherwise, optionally requires AUTH PLAIN, and records
/// the peak number of simultaneous connections so tests can assert real parallelism.
/// </summary>
public sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _active;
    private int _peak;

    public int Port { get; }
    public ConcurrentQueue<FakeSmtpMessage> Messages { get; } = new();
    public int PeakConcurrentConnections => Volatile.Read(ref _peak);
    public int ConnectionsAccepted => _connections;
    private int _connections;

    /// <summary>Return true to answer RCPT TO with 550 for that address.</summary>
    public Func<string, bool>? RejectRecipient { get; set; }

    /// <summary>Artificial processing time per message, so overlapping connections are observable.</summary>
    public TimeSpan DataDelay { get; set; } = TimeSpan.Zero;

    /// <summary>When set, advertises AUTH PLAIN and requires these credentials before MAIL FROM.</summary>
    public (string Username, string Password)? RequiredCredentials { get; set; }

    public FakeSmtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>A loopback port that nothing listens on.</summary>
    public static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            Interlocked.Increment(ref _connections);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var active = Interlocked.Increment(ref _active);
        int peak;
        do { peak = Volatile.Read(ref _peak); }
        while (active > peak && Interlocked.CompareExchange(ref _peak, active, peak) != peak);

        try
        {
            using (client)
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.Latin1, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

                await writer.WriteLineAsync("220 fake ESMTP ready");

                string? from = null;
                var rcpts = new List<string>();
                var data = new StringBuilder();
                var inData = false;
                var authenticated = false;

                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) return;

                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            if (DataDelay > TimeSpan.Zero) await Task.Delay(DataDelay, _cts.Token);
                            Messages.Enqueue(new FakeSmtpMessage(from, rcpts.ToList(), data.ToString()));
                            data.Clear(); rcpts.Clear(); from = null;
                            await writer.WriteLineAsync("250 OK queued");
                        }
                        else
                        {
                            data.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
                        }
                        continue;
                    }

                    var parts = line.Split(' ', 2);
                    var cmd = parts[0].ToUpperInvariant();
                    var arg = parts.Length > 1 ? parts[1] : string.Empty;

                    switch (cmd)
                    {
                        case "EHLO":
                            await writer.WriteLineAsync("250-fake");
                            if (RequiredCredentials is not null) await writer.WriteLineAsync("250-AUTH PLAIN");
                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 fake");
                            break;
                        case "AUTH":
                        {
                            var ok = false;
                            var pieces = arg.Split(' ', 2);
                            if (pieces.Length == 2 && pieces[0].Equals("PLAIN", StringComparison.OrdinalIgnoreCase) && RequiredCredentials is { } creds)
                            {
                                try
                                {
                                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(pieces[1])).Split('\0');
                                    ok = decoded.Length == 3 && decoded[1] == creds.Username && decoded[2] == creds.Password;
                                }
                                catch { ok = false; }
                            }
                            authenticated = ok;
                            await writer.WriteLineAsync(ok ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication failed");
                            break;
                        }
                        case "MAIL":
                            if (RequiredCredentials is not null && !authenticated)
                            {
                                await writer.WriteLineAsync("530 5.7.0 Authentication required");
                                break;
                            }
                            from = ExtractAddress(arg);
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "RCPT":
                        {
                            var address = ExtractAddress(arg);
                            if (RejectRecipient?.Invoke(address) == true)
                            {
                                await writer.WriteLineAsync("550 5.1.1 User unknown");
                            }
                            else
                            {
                                rcpts.Add(address);
                                await writer.WriteLineAsync("250 OK");
                            }
                            break;
                        }
                        case "DATA":
                            inData = true;
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            break;
                        case "RSET":
                            from = null; rcpts.Clear(); data.Clear();
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "NOOP":
                            await writer.WriteLineAsync("250 OK");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 5.5.2 Command not implemented");
                            break;
                    }
                }
            }
        }
        catch
        {
            // Connection dropped or server shutting down.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private static string ExtractAddress(string arg)
    {
        var start = arg.IndexOf('<');
        var end = arg.IndexOf('>');
        if (start >= 0 && end > start) return arg[(start + 1)..end];
        var colon = arg.IndexOf(':');
        return colon >= 0 ? arg[(colon + 1)..].Trim() : arg.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { /* ignore */ }
        _cts.Dispose();
    }
}
