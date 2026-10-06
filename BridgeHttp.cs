using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DynamicIsland;

/// <summary>
/// The face of the bridge a phone meets in its browser: a page reached by the QR code the island shows, and the few
/// roads that lead from that page inward. It is written over raw sockets rather than over the listener Windows offers,
/// because that listener demands a machine-level permission an island has no business asking for.
/// </summary>
sealed class BridgeHttp
{
    const int Headroom = 8192; // bytes of request line and headers before the body is counted
    const int WaitSeconds = 20; // how long a connection may dangle before it is given up on

    static readonly Encoding Utf8 = new UTF8Encoding(false);
    readonly Bridge _bridge;

    public BridgeHttp(Bridge bridge) => _bridge = bridge;

    public async Task RunAsync(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, Bridge.Port);
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // another island, or its own updater, took the door first: the phone will find that one
            App.Log(ex);
            return;
        }

        using (token.Register(listener.Stop))
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; } // the listener was stopped from under us

                _ = Task.Run(() => Serve(client, token), token);
            }
        }
    }

    async Task Serve(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.SendTimeout = client.ReceiveTimeout = WaitSeconds * 1000;
                using NetworkStream stream = client.GetStream();
                Request? request = await Read(stream, token);
                if (request == null) return;

                Answer answer = request.TooBig
                    ? new Answer(413, """{"ok":false,"error":"Файл слишком большой"}""")
                    : Await(request, client);
                byte[] body = Utf8.GetBytes(answer.Said);
                await Write(stream, Head(answer.Status, body.Length, answer.Said.StartsWith('<')),
                    request.Kind == "HEAD" ? [] : body, token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // a phone that hung up mid-sentence is not a fault of ours
            }
        }
    }

    readonly record struct Answer(int Status, string Said);

    /// <summary>What the door answers, in one place so that every road is seen beside its neighbours.</summary>
    Answer Await(Request request, TcpClient client)
    {
        if (request.Key != _bridge.Key)
            return new Answer(403, One("Ключ не тот", "Страница открыта по старой QR-коде. Покажите острову свежую."));
        if (request.Kind == "GET")
        {
            return request.Road switch
            {
                "/" or "/i" => new Answer(200, Page()),
                "/ping" => new Answer(200, """{"ok":true,"name":"остров"}"""),
                _ => new Answer(404, One("Ни такой дороги", "Страница острова живёт по корню этого адреса.")),
            };
        }
        if (request.Kind != "POST")
            return new Answer(405, One("Так нельзя", "Страница умеет отправлять текст и файлы."));

        _bridge.Visit((client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "телефон");
        return request.Road switch
        {
            "/clip" => Say(request),
            "/upload" => Send(request),
            _ => new Answer(404, One("Ни такой дороги", "Страница острова живёт по корню этого адреса.")),
        };
    }

    Answer Say(Request request)
    {
        string text = request.Field("text") ?? (request.Type.StartsWith("text/", StringComparison.Ordinal) ? request.Text : "");
        text = text.Trim('\r', '\n', ' ', '\0');
        if (text.Length == 0) return new Answer(400, One("Пусто", "На странице нет ни одного знака."));
        if (text.Length > 20000) text = text[..20000]; // a whole book is a file, not a saying
        _bridge.Take(text);
        return new Answer(200, """{"ok":true,"kind":"text"}""");
    }

    Answer Send(Request request)
    {
        List<Bridge.Part> parts = request.Parts.Where(p => p.Bytes.Length > 0 && p.File.Length > 0).ToList();
        if (parts.Count == 0) return new Answer(400, One("Ни одного файла", "Телефон прислал форму без содержимого."));
        foreach (Bridge.Part part in parts) _bridge.Take(part.File, part.Type, part.Bytes);
        return new Answer(200, $$"""{"ok":true,"kind":"files","count":{{parts.Count}}}""");
    }

    /// <summary>
    /// The head and the beginning of the body. A request whose headers never finish, or whose body is promised larger
    /// than the bridge carries, is turned away before it can hold the door open.
    /// </summary>
    static async Task<Request?> Read(NetworkStream stream, CancellationToken token)
    {
        var head = new byte[Headroom];
        int read = 0;
        int end = -1;
        while (read < Headroom && (end = Index(head, 0, read, CrLfLf)) < 0)
        {
            int got = await stream.ReadAsync(head.AsMemory(read, Headroom - read), token);
            if (got == 0) return null;
            read += got;
        }
        if (end < 0) return null;

        var text = Utf8.GetString(head, 0, end);
        string[] lines = text.Split(CrLf, StringSplitOptions.None);
        if (lines.Length < 2 || lines[0].Split(' ').Length < 2) return null;

        Dictionary<string, string> fields = Fields(lines);
        var request = new Request
        {
            Kind = lines[0].Split(' ')[0].Trim().ToUpperInvariant(),
            Road = lines[0].Split(' ')[1] ?? "/",
            Headers = fields,
            Type = fields.GetValueOrDefault("content-type", ""),
        };
        if (request.Road.Contains('?'))
        {
            (request.Road, string query) = (request.Road[..request.Road.IndexOf('?')], request.Road[(request.Road.IndexOf('?') + 1)..]);
            request.Query = Query(query);
        }
        request.Key = request.Query.GetValueOrDefault("k", "");

        long promised = long.TryParse(request.Headers.GetValueOrDefault("content-length", ""), out long given) ? given : 0;
        if (promised > Bridge.LargestUpload)
        {
            request.TooBig = true;
            return request;
        }

        // a phone usually sends its headers and its body in one breath, so the signs after the blank line are already
        // the beginning of that body; taking them again from the stream would wait for bytes that never come
        int inHand = read - end - CrLfLf.Length;
        var body = new byte[promised];
        for (int at = 0; at < promised;)
        {
            int free = (int)(promised - at);
            int copy = at == 0 ? Math.Min(inHand, free) : 0;
            if (copy > 0)
            {
                Array.Copy(head, end + CrLfLf.Length, body, at, copy);
                at += copy;
                continue;
            }
            int got = await stream.ReadAsync(body.AsMemory(at, free), token);
            if (got == 0) break; // a phone that stopped talking halfway
            at += got;
        }
        request.Body = body;
        return request;
    }

    static Dictionary<string, string> Fields(string[] lines)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0) found[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        return found;
    }

    static Dictionary<string, string> Query(string text)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equal = pair.IndexOf('=');
            string name = equal < 0 ? pair : pair[..equal];
            string value = equal < 0 ? "" : pair[(equal + 1)..];
            found[WebUtility.UrlDecode(name)] = WebUtility.UrlDecode(value.Replace('+', ' '));
        }
        return found;
    }

    static async Task Write(NetworkStream stream, string head, byte[] body, CancellationToken token)
    {
        await stream.WriteAsync(Utf8.GetBytes(head), token);
        if (body.Length > 0) await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    static string Head(int status, int length, bool page) =>
        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Отказ")}\r\n"
        + $"Content-Type: {(page ? "text/html" : "application/json")}; charset=utf-8\r\n"
        + $"Content-Length: {length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";

    /// <summary>The page a phone shows after reading the QR code: one box for words, one for files, nothing else.</summary>
    string Page() => $$"""
        <!doctype html><html lang="ru"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Мост острова</title>
        <style>
        body{margin:0;padding:22px 18px 40px;background:#0b0c0e;color:#f2f3f5;
          font:16px/1.45 -apple-system,"Segoe UI",Roboto,sans-serif}
        h1{font-size:19px;margin:0 0 4px}
        p{margin:0 0 22px;color:#9aa0aa;font-size:13px}
        section{margin-bottom:26px}
        textarea{width:100%;min-height:96px;box-sizing:border-box;border-radius:16px;border:1px solid #26282e;
          background:#15171b;color:#f2f3f5;padding:13px;font:16px/1.4 inherit;resize:vertical}
        button{width:100%;margin-top:10px;padding:13px;border:0;border-radius:16px;background:#f2f3f5;color:#0b0c0e;
          font:600 15px/1 inherit}
        button.wait{opacity:.45}
        input[type=file]{color:#9aa0aa;font-size:13px}
        .mark{display:block;margin-top:9px;min-height:18px;font-size:13px;color:#7ee2a8}
        .bad{color:#ff9d87}
        </style></head><body>
        <h1>Мост острова</h1>
        <p>{{Escape(_bridge.Address)}} · текст уходит в буфер обмена, файл — на полку.</p>
        <section>
          <textarea id="t" placeholder="Что передать на компьютер"></textarea>
          <button id="b">Отправить текст</button><span class="mark" id="m"></span>
        </section>
        <section>
          <input type="file" id="f" multiple accept="image/*,video/*,.pdf,.txt,.zip">
          <button id="u">Положить на полку</button><span class="mark" id="n"></span>
        </section>
        <script>
        const key = new URLSearchParams(location.search).get('k') || '{{Escape(_bridge.Key)}}';
        const link = (road) => road + '?k=' + encodeURIComponent(key);
        async function post(road, body, mark, back) {
          const button = document.querySelector(back).parentElement.querySelector('button');
          button.classList.add('wait');
          try {
            const answer = await (await fetch(link(road), { method: 'POST', body })).json();
            mark.textContent = answer.ok ? (answer.kind === 'files' ? 'Файлов на полке: ' + answer.count : 'Текст передан') : (answer.error || 'Не вышло');
            mark.classList.toggle('bad', !answer.ok);
            if (answer.ok && road === '/clip') document.getElementById('t').value = '';
          } catch (e) { mark.textContent = 'Связь потеряна'; mark.classList.add('bad'); }
          button.classList.remove('wait');
        }
        document.getElementById('b').onclick = () => {
          const text = document.getElementById('t').value;
          post('/clip', new URLSearchParams({ text }), document.getElementById('m'), '#b');
        };
        document.getElementById('u').onclick = () => {
          const files = document.getElementById('f').files;
          if (!files.length) { document.getElementById('n').textContent = 'Файл не выбран'; return; }
          const form = new FormData();
          for (const file of files) form.append('file', file, file.name);
          post('/upload', form, document.getElementById('n'), '#u');
        };
        </script></body></html>
        """;

    static string One(string title, string said) =>
        $$"""<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{{Escape(title)}}</title></head><body style="margin:24px;font:16px/1.5 -apple-system,'Segoe UI',sans-serif;background:#0b0c0e;color:#f2f3f5"><h1 style="font-size:19px">{{Escape(title)}}</h1><p style="color:#9aa0aa">{{Escape(said)}}</p></body></html>""";

    /// <summary>Signs that cannot be mistaken for the end of a document.</summary>
    static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    static readonly byte[] CrLfLf = "\r\n\r\n"u8.ToArray();
    static readonly string[] CrLf = ["\r\n"];

    static int Index(byte[] haystack, int from, int length, ReadOnlySpan<byte> needle)
    {
        for (int at = from; at + needle.Length <= from + length; at++)
            if (haystack.AsSpan(at, needle.Length).SequenceEqual(needle)) return at;
        return -1;
    }

    /// <summary>A request that reached the door, with its body already read and its form already opened.</summary>
    sealed class Request
    {
        public string Kind = "GET";
        public string Road = "/";
        public string Key = "";
        public string Type = "";
        public bool TooBig;
        public byte[] Body = [];
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);

        public string Text => Utf8.GetString(Body);

        /// <summary>The parts of a multipart body: files and plain fields alike.</summary>
        public List<Bridge.Part> Parts
        {
            get
            {
                _parts ??= Bridge.Split(Body, Boundary());
                return _parts;
            }
        }

        List<Bridge.Part>? _parts;

        public string? Field(string name) =>
            Headers.GetValueOrDefault("content-type", "").StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                ? Parts.FirstOrDefault(p => p.Name == name)?.Text
                : Form().GetValueOrDefault(name);

        Dictionary<string, string> Form()
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in Text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equal = pair.IndexOf('=');
                found[WebUtility.UrlDecode(equal < 0 ? pair : pair[..equal])] =
                    WebUtility.UrlDecode(equal < 0 ? "" : pair[(equal + 1)..].Replace('+', ' '));
            }
            return found;
        }

        string Boundary()
        {
            string marker = "boundary=";
            Type = Headers.GetValueOrDefault("content-type", "");
            int at = Type.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return "";
            string value = Type[(at + marker.Length)..].Trim();
            int cut = value.IndexOf(';');
            if (cut >= 0) value = value[..cut];
            return value.Trim('"', ' ');
        }
    }
}
