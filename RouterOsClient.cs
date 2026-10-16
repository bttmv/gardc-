using System.IO;
using System.Net.Sockets;
using System.Text;

namespace WifiManager;

public class RouterOsException : Exception
{
    public RouterOsException(string message) : base(message) { }
}

/// عميل MikroTik RouterOS API (المنفذ 8728) - RouterOS 6.43 وما بعده
public sealed class RouterOsClient : IDisposable
{
    readonly string _host;
    readonly int _port;
    TcpClient? _tcp;
    NetworkStream? _stream;
    readonly SemaphoreSlim _lock = new(1, 1);

    public RouterOsClient(string host, int port) { _host = host; _port = port; }

    public bool Connected => _tcp?.Connected == true && _stream != null;

    public async Task ConnectAsync(string user, string password)
    {
        Dispose();
        _tcp = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await _tcp.ConnectAsync(_host, _port, cts.Token);
        _stream = _tcp.GetStream();
        await CallAsync("/login", new Dictionary<string, string> { ["name"] = user, ["password"] = password });
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    // ---------- ترميز الطول ----------
    static byte[] EncodeLen(int n)
    {
        if (n < 0x80) return new[] { (byte)n };
        if (n < 0x4000) { n |= 0x8000; return new[] { (byte)(n >> 8), (byte)n }; }
        if (n < 0x200000) { n |= 0xC00000; return new[] { (byte)(n >> 16), (byte)(n >> 8), (byte)n }; }
        if (n < 0x10000000)
        {
            n |= unchecked((int)0xE0000000);
            return new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        }
        return new byte[] { 0xF0, (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
    }

    async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = await _stream!.ReadAsync(buf.AsMemory(off, buf.Length - off), ct);
            if (n == 0) throw new IOException("انقطع الاتصال بالراوتر");
            off += n;
        }
    }

    async Task<int> ReadByteAsync(CancellationToken ct)
    {
        var b = new byte[1];
        await ReadExactAsync(b, ct);
        return b[0];
    }

    async Task<int> ReadLenAsync(CancellationToken ct)
    {
        int c = await ReadByteAsync(ct);
        if ((c & 0x80) == 0) return c;
        if ((c & 0xC0) == 0x80) return ((c & 0x3F) << 8) | await ReadByteAsync(ct);
        if ((c & 0xE0) == 0xC0)
        {
            int b1 = await ReadByteAsync(ct), b2 = await ReadByteAsync(ct);
            return ((c & 0x1F) << 16) | (b1 << 8) | b2;
        }
        if ((c & 0xF0) == 0xE0)
        {
            int b1 = await ReadByteAsync(ct), b2 = await ReadByteAsync(ct), b3 = await ReadByteAsync(ct);
            return ((c & 0x0F) << 24) | (b1 << 16) | (b2 << 8) | b3;
        }
        int d1 = await ReadByteAsync(ct), d2 = await ReadByteAsync(ct), d3 = await ReadByteAsync(ct), d4 = await ReadByteAsync(ct);
        return (d1 << 24) | (d2 << 16) | (d3 << 8) | d4;
    }

    async Task WriteWordAsync(string w, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(w);
        await _stream!.WriteAsync(EncodeLen(data.Length), ct);
        await _stream.WriteAsync(data, ct);
    }

    async Task<List<string>> ReadSentenceAsync(CancellationToken ct)
    {
        var words = new List<string>();
        while (true)
        {
            int n = await ReadLenAsync(ct);
            if (n == 0) return words;
            var buf = new byte[n];
            await ReadExactAsync(buf, ct);
            words.Add(Encoding.UTF8.GetString(buf));
        }
    }

    static Dictionary<string, string> Parse(IEnumerable<string> words)
    {
        var d = new Dictionary<string, string>();
        foreach (var w in words)
        {
            if (!w.StartsWith('=')) continue;
            var s = w.Substring(1);
            int i = s.IndexOf('=');
            if (i < 0) d[s] = "";
            else d[s.Substring(0, i)] = s.Substring(i + 1);
        }
        return d;
    }

    /// cmd مثل "/ip/hotspot/user/print" ، queries مثل "?name=abc"
    public async Task<List<Dictionary<string, string>>> CallAsync(
        string cmd, Dictionary<string, string>? attrs = null, string[]? queries = null, int timeoutSec = 20)
    {
        if (_stream == null) throw new IOException("غير متصل بالراوتر");
        await _lock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            var ct = cts.Token;
            await WriteWordAsync(cmd, ct);
            if (attrs != null)
                foreach (var kv in attrs) await WriteWordAsync($"={kv.Key}={kv.Value}", ct);
            if (queries != null)
                foreach (var q in queries) await WriteWordAsync(q, ct);
            await WriteWordAsync("", ct);

            var results = new List<Dictionary<string, string>>();
            string? error = null;
            while (true)
            {
                var s = await ReadSentenceAsync(ct);
                if (s.Count == 0) continue;
                switch (s[0])
                {
                    case "!re": results.Add(Parse(s.Skip(1))); break;
                    case "!trap":
                        var d = Parse(s.Skip(1));
                        error = d.TryGetValue("message", out var m) ? m : "خطأ غير معروف";
                        break;
                    case "!fatal": throw new RouterOsException("خطأ فادح من الراوتر: " + string.Join(" ", s.Skip(1)));
                }
                if (s[0] == "!done") break;
            }
            if (error != null) throw new RouterOsException(error);
            return results;
        }
        finally { _lock.Release(); }
    }
}
