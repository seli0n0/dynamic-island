using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicIsland;

/// <summary>
/// The other face of the bridge: the one an Android phone running KDE Connect reaches without a browser. The island
/// puts itself on the network exactly as the desktop program of that family does — a shout on the local broadcast
/// ports, a TLS listener behind it, and the same length-prefixed JSON the phones already speak. What the phone sends
/// inward (its battery, its notices, its clipboard, a file) goes to the same places the QR page goes to.
/// </summary>
sealed class BridgeKde
{
    const int TcpPort = 1716;
    static readonly int[] ShoutPorts = [1714, 1715, 1716, 1717];
    static readonly TimeSpan Again = TimeSpan.FromSeconds(25); // how often the island repeats that it is here
    const int LargestBody = 64 * 1024; // a packet of JSON only; files come behind it as raw bytes

    readonly Bridge _bridge;
    readonly string _id = Id();
    readonly X509Certificate2 _certificate = SelfSigned();
    readonly Dictionary<string, Phone> _live = [];
    readonly object _gate = new();
    Phone? _pending;
    CancellationToken _turns;

    public BridgeKde(Bridge bridge) => _bridge = bridge;

    /// <summary>Runs the shout and the listener together until the bridge is closed.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        _turns = token;
        var shout = Task.Run(() => ListenUdp(token), token);
        var door = Task.Run(() => ListenTcp(token), token);
        _ = Announce(token);
        await Task.WhenAll(shout, door);
    }

    // ------------------------------------------------------------- who is on the network

    async Task ListenUdp(CancellationToken token)
    {
        foreach (int port in ShoutPorts)
        {
            var listener = new UdpClient(AddressFamily.InterNetwork);
            listener.Client.EnableBroadcast = true;
            try { listener.Client.Bind(new IPEndPoint(IPAddress.Any, port)); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Dispose(); // the real KDE Connect desktop is on this machine: it will answer the phones
                continue;
            }
            _ = Hear(listener, token);
        }
        await Task.CompletedTask;

        async Task Hear(UdpClient listener, CancellationToken cancelled)
        {
            try
            {
                while (!cancelled.IsCancellationRequested)
                {
                    UdpReceiveResult datagram = await listener.ReceiveAsync(cancelled);
                    Identity? come = Identity.Of(datagram.Buffer);
                    if (come == null || come.Id == _id) continue;
                    _bridge.Visit(come.Name);
                    Reach(come, cancelled);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or JsonException)
            {
                listener.Dispose();
            }
        }
    }

    /// <summary>Says, to every address the phone might be listening on, that an island of this family is here.</summary>
    async Task Announce(CancellationToken token)
    {
        var clients = ShoutPorts.Select(_ => new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true }).ToList();
        byte[] message = Bytes(Hello());
        try
        {
            while (!token.IsCancellationRequested)
            {
                // one address that has gone is no reason to stop saying it: the next round speaks to the rest
                try
                {
                    foreach (UdpClient client in clients)
                        foreach (IPAddress broadcast in Broadcasts())
                            foreach (int port in ShoutPorts)
                                await client.SendAsync(message, new IPEndPoint(broadcast, port), token);
                }
                catch (SocketException) { }
                await Task.Delay(Again, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            foreach (UdpClient client in clients) client.Dispose();
        }
    }

    /// <summary>The machine's own broadcast addresses, so that a phone on any of several networks is reached.</summary>
    static IEnumerable<IPAddress> Broadcasts()
    {
        yield return IPAddress.Broadcast;
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            foreach (UnicastIPAddressInformation one in nic.GetIPProperties().UnicastAddresses)
                if (one.Address.AddressFamily == AddressFamily.InterNetwork && one.IPv4Mask != null)
                    yield return Broadcast(one.Address, one.IPv4Mask);
        }
    }

    static IPAddress Broadcast(IPAddress address, IPAddress mask)
    {
        byte[] a = address.GetAddressBytes(), m = mask.GetAddressBytes();
        var net = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) net[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(net);
    }

    // ------------------------------------------------------------- the TLS door

    async Task ListenTcp(CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Any, TcpPort);
        try { listener.Start(); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
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
                catch (SocketException) { break; }
                _ = Task.Run(() => Open(client, null, token), token);
            }
        }
    }

    /// <summary>Crosses to a phone that shouted, or welcomes one that came. Either way the same talk follows.</summary>
    bool Reach(Identity come, CancellationToken token)
    {
        lock (_gate)
        {
            if (_live.ContainsKey(come.Id)) return true;
            _live[come.Id] = new Phone(come.Id); // held at once, so two arrivals do not open two doors
        }
        _ = Task.Run(() => Open(null, come, token), token);
        return true;
    }

    async Task Open(TcpClient? direct, Identity? come, CancellationToken token)
    {
        Phone? phone = null;
        try
        {
            TcpClient client = direct ?? new TcpClient();
            if (direct == null) await client.ConnectAsync(come!.Address, come.Port, token);
            client.NoDelay = true;

            SslStream secure = new(new NetworkStream(client.Client, true), true);
            if (direct != null)
            {
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = true,
                    // a phone makes its own certificate, so no chain of trust can ever verify it; the owner judges the
                    // fingerprint instead, and the talk below refuses anything that was not agreed to
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    EnabledSslProtocols = SslProtocols.None,
                }, token);
            }
            else
            {
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "kdeconnect",
                    ClientCertificates = [_certificate],
                    // a phone's certificate is made by the phone: it is judged by its fingerprint, shown to the owner
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                }, token);
            }

            if (secure.RemoteCertificate is not X509Certificate2 peer) { secure.Dispose(); return; }

            // a phone that came to us is only known by its address until it says who it is
            phone = new Phone(come?.Id ?? (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "phone")
            {
                Secure = secure,
                Fingerprint = Fingerprint(peer),
            };
            phone.Paired = Trusted(phone.Fingerprint);
            lock (_gate) _live[phone.Id] = phone;

            await Send(phone, Hello(), token); // both sides say who they are before anything else
            await Talk(phone, token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
            or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // a phone that went away mid-sentence; the shout will bring it back
            App.Log(ex);
        }
        finally
        {
            if (phone != null)
            {
                bool waiting;
                lock (_gate)
                {
                    _live.Remove(phone.Id);
                    waiting = ReferenceEquals(_pending, phone);
                    if (waiting) _pending = null;
                }
                // a phone that left without an answer takes its request with it; the page stops asking
                if (waiting) _bridge.Answer(phone.Name, false);
            }
        }
    }

    /// <summary>Reads packets until the phone stops talking.</summary>
    async Task Talk(Phone phone, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Packet? come = await Read(phone, token);
            if (come == null) return;
            await Handle(phone, come.Value, token);
        }
    }

    async Task Handle(Phone phone, Packet packet, CancellationToken token)
    {
        JsonElement body = packet.Body;
        // only the two words that settle who stands at the door are heard from a stranger; everything else a phone
        // sends asks for a place in this machine, and so waits until the owner has agreed to that phone
        if (phone.Paired == false && packet.Type is not ("kdeconnect.identity" or "kdeconnect.pair")) return;

        switch (packet.Type)
        {
            case "kdeconnect.identity":
                string named = Text(body, "deviceId");
                if (named.Length > 0 && named != phone.Id)
                    lock (_gate)
                    {
                        _live.Remove(phone.Id);
                        phone.Id = named;
                        _live[named] = phone;
                    }
                phone.Name = Text(body, "name");
                if (phone.Paired) await Pair(phone, token); // a phone we already trust does not ask again
                break;

            case "kdeconnect.pair" when Ask(packet) && phone.Paired == false:
                // the phone has asked; whether it stands inside is the owner's call, made on the page, and this
                // channel stays silent until that call is given
                lock (_gate) _pending = phone;
                phone.Pending = true;
                _bridge.Ask(new Bridge.Asking(phone.Name, phone.Fingerprint, Fingerprint(_certificate)));
                break;

            case "kdeconnect.pair" when Ask(packet):
                // a phone we already stand paired with asks again after every joining; the yes is said back rather
                // than leave it waiting for an answer the owner gave in a run that has ended
                await Send(phone, Frame("kdeconnect.pair", PairBody(phone, true), packet.Id), token);
                break;

            case "kdeconnect.pair": // the phone dropped us, or answered a pair request of its own
                phone.Paired = false;
                phone.Pending = false;
                lock (_gate) if (ReferenceEquals(_pending, phone)) _pending = null;
                Untrust(phone.Fingerprint);
                await Send(phone, Frame("kdeconnect.pair", PairBody(phone, false), packet.Id), token);
                break;

            case "kdeconnect.device.battery":
                _bridge.Report(phone.Name, Number(body, "currentCharge") is int now ? now : Number(body, "charge") ?? 0);
                break;

            case "kdeconnect.notification":
                _bridge.Tell(Text(body, "appName"), Text(body, "title"), Text(body, "text"));
                break;

            case "kdeconnect.clipboard" or "kdeconnect.clipboard.connectivity":
                string said = Text(body, "content");
                if (said.Length > 0) _bridge.Take(said);
                break;

            case "kdeconnect.share.request":
                await Share(phone, packet, token);
                break;

            case "kdeconnect.keepalive":
                break; // a hello with nothing in it, but it keeps the door warm
        }
    }

    /// <summary>
    /// A file the phone announced and then wrote straight onto the same channel: the length was promised in the
    /// packet, the bytes follow it, and only then is the transfer answered.
    /// </summary>
    async Task Share(Phone phone, Packet packet, CancellationToken token)
    {
        long size = Number(packet.Body, "payloadSize") ?? 0;
        if (size <= 0 || size > Bridge.LargestUpload) return;

        var bytes = new byte[size];
        await phone.Secure.ReadExactlyAsync(bytes, token);
        _bridge.Take(Text(packet.Body, "fileName"), Text(packet.Body, "mimeType"), bytes);
        await Send(phone, Frame("kdeconnect.share.request", new JsonObject
        {
            ["fileName"] = Text(packet.Body, "fileName"),
            ["payloadTransferOk"] = true,
        }, packet.Id), token);
    }

    static bool Ask(Packet packet) =>
        packet.Body.ValueKind == JsonValueKind.Object &&
        packet.Body.TryGetProperty("pair", out JsonElement value) && value.ValueKind == JsonValueKind.True;

    // ------------------------------------------------------------- packets, as the family counts them

    /// <summary>A packet is four signs of length, the JSON, and a zero the family leaves at the end of every one.</summary>
    static async Task<Packet?> Read(Phone phone, CancellationToken token)
    {
        var length = new byte[4];
        try { await phone.Secure.ReadExactlyAsync(length, token); }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException) { return null; }

        int size = (length[0] << 24) | (length[1] << 16) | (length[2] << 8) | length[3];
        if (size is <= 0 or > LargestBody) return null;

        var payload = new byte[size];
        await phone.Secure.ReadExactlyAsync(payload, token);
        string text = Encoding.UTF8.GetString(payload).TrimEnd('\0');
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return new Packet(
                text,
                doc.RootElement.GetProperty("type").GetString() ?? "",
                doc.RootElement.TryGetProperty("id", out JsonElement id) ? id.GetInt64() : 0,
                doc.RootElement.TryGetProperty("body", out JsonElement body) ? body.Clone() : default);
        }
        catch (JsonException) { return null; }
    }

    async Task Send(Phone phone, string json, CancellationToken token)
    {
        try
        {
            await phone.Door.WaitAsync(token);
            try
            {
                byte[] payload = Bytes(json);
                var length = new[]
                {
                    (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length,
                };
                await phone.Secure.WriteAsync(length, token);
                await phone.Secure.WriteAsync(payload, token);
                await phone.Secure.FlushAsync(token);
            }
            finally { phone.Door.Release(); }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }

    static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json + "\0");

    string Hello() => Frame("kdeconnect.identity", new JsonObject
    {
        ["deviceId"] = _id,
        ["name"] = "Dynamic Island",
        ["type"] = "desktop",
        ["deviceType"] = "laptop",
        ["version"] = 7,
        ["tcpPort"] = TcpPort,
        ["certificate"] = Fingerprint(_certificate),
        ["incoming_interfaces"] = new JsonArray("ethernet", "wireless"),
        ["outgoing_interfaces"] = new JsonArray("ethernet", "wireless"),
        ["supportedCapabilities"] = new JsonObject(),
    });

    static string Frame(string type, JsonObject body, long id = 0)
    {
        var packet = new JsonObject { ["type"] = type, ["body"] = body };
        if (id != 0) packet["id"] = id;
        return packet.ToJsonString(new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    JsonObject PairBody(Phone phone, bool pair) => new()
    {
        ["pair"] = pair,
        ["from"] = _id,
        ["deviceId"] = _id,
        ["deviceName"] = "Dynamic Island",
        ["accessLevel"] = "trusted",
        ["certificate"] = Fingerprint(_certificate),
        ["toBePaired"] = phone.Id,
    };

    /// <summary>Both sides must hear the pair before the phone lets anything through.</summary>
    async Task Pair(Phone phone, CancellationToken token)
    {
        if (phone.Paired) return;
        await Send(phone, Frame("kdeconnect.pair", PairBody(phone, true)), token);
    }

    /// <summary>
    /// The owner's answer to the phone waiting at the door. Nothing is trusted before it is said; a refusal is carried
    /// back over the same channel as the plain no the family uses, so that the phone stops asking.
    /// </summary>
    public void Answer(bool yes)
    {
        Phone? phone;
        lock (_gate)
        {
            phone = _pending;
            _pending = null;
        }
        if (phone == null) return;

        phone.Pending = false;
        if (yes)
        {
            phone.Paired = true;
            Trust(phone);
        }
        _ = Task.Run(() => Send(phone, Frame("kdeconnect.pair", PairBody(phone, yes)), _turns), _turns);
        _bridge.Answer(phone.Name, yes);
    }

    // ------------------------------------------------------------- trust, kept between runs

    static void Trust(Phone phone)
    {
        try
        {
            List<string> known = Phones().ToList();
            string entry = $"{phone.Id}\t{phone.Name}\t{phone.Fingerprint}";
            if (!known.Contains(entry))
            {
                known.Add(entry);
                File.WriteAllText(PhoneFile, string.Join("\n", known));
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Forgets a phone the owner refused, or that unpaired itself, so that it has to ask again.</summary>
    static void Untrust(string fingerprint)
    {
        if (fingerprint.Length == 0) return; // nothing to pick by, and everything would go
        try
        {
            List<string> known = Phones().ToList();
            List<string> kept = known.Where(line => !line.EndsWith(fingerprint, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count != known.Count) File.WriteAllText(PhoneFile, string.Join("\n", kept));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static string PhoneFile => Path.Combine(Bridge.Folder, "phones.txt");

    static IEnumerable<string> Phones()
    {
        try
        {
            return File.Exists(PhoneFile)
                ? File.ReadAllLines(PhoneFile).Where(line => line.Length > 0).ToList()
                : [];
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return [];
        }
    }

    // ------------------------------------------------------------- small helpers

    static string Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    static int? Number(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;

    static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(certificate.GetCertHash()).ToLowerInvariant();

    /// <summary>Whether this certificate was let in before, in a run of the island that has long ended.</summary>
    static bool Trusted(string fingerprint) =>
        fingerprint.Length > 0 && Phones().Any(line => line.EndsWith(fingerprint, StringComparison.OrdinalIgnoreCase));

    /// <summary>The name this island answers to on the network. It stays with the machine, not with the run.</summary>
    static string Id()
    {
        string saved = Settings.BridgePhoneId;
        if (saved.Length > 0) return saved;
        var bytes = new byte[5];
        RandomNumberGenerator.Fill(bytes);
        string fresh = "island-" + Convert.ToHexString(bytes).ToLowerInvariant();
        Settings.BridgePhoneId = fresh;
        return fresh;
    }

    static X509Certificate2 SelfSigned()
    {
        try
        {
            string saved = Path.ChangeExtension(PhoneFile, ".pfx");
            if (File.Exists(saved))
                return new X509Certificate2(saved, (string?)null, X509KeyStorageFlags.Exportable);

            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN=Dynamic Island ({Environment.MachineName})"), key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));

            X509Certificate2 made = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(9));
            File.WriteAllBytes(saved, made.Export(X509ContentType.Pfx));
            return new X509Certificate2(saved, (string?)null, X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            throw;
        }
    }

    /// <summary>A phone on the far side of one TLS channel.</summary>
    sealed class Phone(string id)
    {
        public string Id { get; set; } = id;
        public string Name { get; set; } = id;
        public string Fingerprint { get; set; } = "";
        public bool Paired { get; set; }

        /// <summary>Whether this phone's request is the one shown on the page, waiting for the owner.</summary>
        public bool Pending { get; set; }

        public SslStream Secure = null!;

        /// <summary>One packet at a time on the channel: the answer to a pair can come from the page, not from the talk.</summary>
        public SemaphoreSlim Door { get; } = new(1, 1);
    }

    readonly record struct Packet(string Raw, string Type, long Id, JsonElement Body);

    /// <summary>An identity as it arrives on the broadcast ports, before any TLS channel exists.</summary>
    sealed record Identity(string Id, string Name, string Address, int Port)
    {
        public static Identity? Of(byte[] datagram)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(datagram).TrimEnd('\0'));
                JsonElement body = doc.RootElement.GetProperty("body");
                if (doc.RootElement.GetProperty("type").GetString() != "kdeconnect.identity") return null;
                string address = body.GetProperty("address").GetString() ?? "";
                int port = body.TryGetProperty("tcpPort", out JsonElement given) ? given.GetInt32() : TcpPort;
                return new Identity(body.GetProperty("deviceId").GetString() ?? "",
                    body.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "", address, port);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
