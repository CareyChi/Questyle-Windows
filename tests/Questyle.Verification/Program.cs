using Questyle.Desktop.Services;
using System.Text;
using System.Text.Json;

if (args.Contains("--snapshot") || args.Contains("--same-settings"))
{
    UsbDkTransport.HelperPath = Path.GetFullPath("reverse/tools/usbdk-runtime/UsbDk Runtime Library/UsbDk Runtime Library/UsbDkHelper.dll");
    var controller = new DongleController(); controller.Log += Console.WriteLine;
    var current = await controller.ReadAsync();
    Console.WriteLine(JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }));
    if (args.Contains("--same-settings"))
    {
        var gameMode = current.GameMode ?? throw new InvalidOperationException("Unknown game mode response; cannot write it back.");
        var after = await controller.RunAsync(s => { s.SetGame(gameMode); if (current.ActiveCodec == "LDAC" && current.Ldac is >= 1 and <= 3) s.SetLdac(current.Ldac); if (current.ActiveCodec == "aptX Adaptive" && current.Adaptive is >= 1 and <= 2) s.SetAdaptive(current.Adaptive); return s.Snapshot(); });
        if (after.Ldac != current.Ldac || after.Adaptive != current.Adaptive || after.GameMode != current.GameMode) throw new Exception("Hardware readback mismatch");
        Console.WriteLine("PASS real device acknowledged the original game setting and read back unchanged settings. Codec-specific writes were checked only for the active codec.");
    }
    return;
}

if (args.Contains("--raw-list") || args.Contains("--raw-query"))
{
    UsbDkTransport.HelperPath = Path.GetFullPath("reverse/tools/usbdk-runtime/UsbDk Runtime Library/UsbDk Runtime Library/UsbDkHelper.dll");
    foreach (var device in UsbDkTransport.Enumerate())
    {
        Console.WriteLine(device);
        Console.WriteLine(device.DeviceId);
        Console.WriteLine(Convert.ToHexString(UsbDkTransport.Configuration(device)));
        if (args.Contains("--raw-query"))
        {
            using var transport = new UsbDkTransport();
            transport.Open(device);
            await Task.Delay(700);
            byte sequence = 1;
            QuestyleResponse Send(byte command, byte sub, byte[]? payload = null)
            {
                Thread.Sleep(180);
                var frame = QuestyleProtocol.Encode(++sequence, command, sub, payload ?? []);
                Console.WriteLine("TX " + Convert.ToHexString(frame));
                var data = transport.Exchange(frame, TimeSpan.FromSeconds(3));
                Console.WriteLine("RX " + Convert.ToHexString(data));
                if (!QuestyleProtocol.TryDecode(data, out var response)) throw new Exception("invalid response");
                return response!;
            }
            var challenge = Send(0x10, 0);
            Send(0x10, 1, challenge.Data);
            foreach (var (name, cmd, sub) in new (string, byte, byte)[] { ("Model", 0x15, 1), ("Name", 0x90, 2), ("Serial", 0x15, 3), ("Version", 0x20, 0), ("Dongle Codecs", 0x80, 2), ("Sink Codecs", 0x80, 1), ("Status", 0x30, 2), ("LDAC", 0xe0, 255), ("aptX", 0xf0, 255), ("Game", 0x11, 255), ("Auracast", 0x22, 255) })
            {
                try
                {
                    var rawReply = Send(cmd, sub);
                    Console.WriteLine($"{name}: marker {rawReply.Marker:X2}, data {Convert.ToHexString(rawReply.Data)} | {Encoding.UTF8.GetString(rawReply.Data)}");
                }
                catch (Exception ex) { Console.WriteLine(name + ": " + ex.Message); }
            }
        }
    }
    return;
}

if (args.Contains("--usb"))
{
    foreach (var device in HidTransport.Enumerate())
    {
        Console.WriteLine(device.Description);
        Console.WriteLine(device.Path);
    }
    return;
}
int count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); count++; }
Check(Convert.ToHexString(QuestyleProtocol.Encode(2, 0x10, 0, [])) == "514C0210000051", "APK handshake request / checksum");
var payloadFrame = QuestyleProtocol.Encode(255, 0xc0, 0, [1, 2, 3, 4, 5, 6]);
Check(payloadFrame.Length == 13 && payloadFrame.Sum(x => x) % 256 == 0, "payload length and checksum");
byte[] reply = [0x51, 0x4c, 2, 0, 3, 0x41, 0x42, 0x43, 0x98];
Check(QuestyleProtocol.TryDecode(reply, out var response) && response!.Sequence == 2 && response.Data.SequenceEqual(new byte[] { 0x41, 0x42, 0x43 }), "response differs from request layout");
Check(!QuestyleProtocol.TryDecode(reply.AsSpan(0, 7), out _), "truncated packet");
reply[5] ^= 1;
Check(!QuestyleProtocol.TryDecode(reply, out _), "corrupted packet");
Check(!QuestyleProtocol.TryDecode(new byte[64], out _), "invalid header");
Check(QuestyleProtocol.TryDecode(Convert.FromHexString("514C034101809E"), out var pro2Reply) && pro2Reply!.Marker == 0x41 && pro2Reply.Data[0] == 0x80, "real Pro2 handshake ACK marker is not a status code");
Check(DongleController.ParseCodecs(new byte[] { 6, 0xcb }).Select(c => c.Code).Order().SequenceEqual(new byte[] { 1, 2, 4, 7, 8, 10, 11 }), "real Pro2 codec mask, big endian");
Check(DongleController.ParseCodecs(new byte[] { 5, 11 }).Select(c => c.Code).Order().SequenceEqual(new byte[] { 1, 2, 4, 9, 11 }), "real headphone codec mask");
Check(AudioSink.Parse(new byte[] { 1, 2, 3, 4, 5, 6, (byte)'A', 4 }) is { Address: "01:02:03:04:05:06", Name: "A", Codec: 4 }, "headphone address/name/codec layout");
Check(AudioSink.Parse(new byte[2]) == null, "short headphone payload");
var fake = new AckTransport(); var session = new DongleController.Session(fake, _ => { }, CancellationToken.None);
session.SetGame(true); session.SetGame(false); session.SetAuracast(true); session.SetAuracast(false);
Check(fake.Commands.SequenceEqual(new (byte, byte)[] { (0x11, 2), (0x11, 1), (0x22, 1), (0x22, 0) }), "APK game and Auracast use different boolean encodings");
Check(DongleController.ParseGameMode([1]) == false && DongleController.ParseGameMode([2]) == true && DongleController.ParseGameMode([0x83]) == null && DongleController.ParseGameMode([]) == null && DongleController.ParseGameMode([1, 2]) == null, "unknown game replies do not become a valid microphone setting");
fake.Commands.Clear(); session.SetLdac(1); session.SetLdac(2); session.SetLdac(3); session.SetAdaptive(1); session.SetAdaptive(2);
Check(fake.Commands.SequenceEqual(new (byte, byte)[] { (0xe0, 1), (0xe0, 2), (0xe0, 3), (0xf0, 1), (0xf0, 2) }), "APK LDAC and Adaptive subcommands");
try { session.Rename(new string('中', 8)); throw new Exception("UTF8 length accepted"); } catch (ArgumentException) { Check(true, "rename bound counts UTF8 bytes"); }
session.Connect("01:02:03:04:05:06"); Check(fake.LastPayload.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }), "connect command carries six address bytes");
try { QuestyleProtocol.Encode(1, 1, 1, new byte[57]); throw new Exception("oversized payload accepted"); }
catch (ArgumentOutOfRangeException) { count++; Console.WriteLine("PASS HID payload bound"); }

using (var cancellation = new CancellationTokenSource())
{
    var discovery = new DiscoveryTransport();
    var search = new DongleController.Session(discovery, _ => { }, cancellation.Token);
    var found = new List<AudioSink>();
    try
    {
        search.Search(TimeSpan.FromSeconds(5), sink => { found.Add(sink); if (found.Count == 2) cancellation.Cancel(); });
        throw new Exception("Search ignored cancellation");
    }
    catch (OperationCanceledException)
    {
        Check(found.Count == 2 && discovery.Reads == 3 && discovery.Commands.Take(2).SequenceEqual(new (byte, byte)[] { (0xb0, 0xff), (0xb0, 0) }) && discovery.Commands.Last() == (0xb0, 2) && !discovery.Searching, "live discovery deduplicates results and exits search before returning cancellation");
    }
}

using (var cancellation = new CancellationTokenSource())
{
    var discovery = new DiscoveryTransport { RejectExit = true };
    var search = new DongleController.Session(discovery, _ => { }, cancellation.Token);
    try { search.Search(TimeSpan.FromSeconds(5), _ => cancellation.Cancel()); throw new Exception("Failed exit ACK was ignored"); }
    catch (IOException) { Check(discovery.Commands.Last() == (0xb0, 2), "failed exit-search ACK is reported instead of being swallowed as cancellation"); }
}

using (var cancellation = new CancellationTokenSource())
{
    var discovery = new DiscoveryTransport(searching: true);
    var search = new DongleController.Session(discovery, _ => { }, cancellation.Token);
    try { search.Search(TimeSpan.FromSeconds(5), _ => cancellation.Cancel()); throw new Exception("Search ignored cancellation"); }
    catch (OperationCanceledException)
    {
        Check(discovery.Commands.Take(3).SequenceEqual(new (byte, byte)[] { (0xb0, 0xff), (0xb0, 2), (0xb0, 0) }) && !discovery.Searching, "a leftover pairing mode is stopped before a fresh search instead of receiving start rejection 83");
    }
}

var rapidDiscovery = new DiscoveryTransport { StartupDelay = TimeSpan.FromSeconds(2), StatusAlways82 = true };
for (int attempt = 0; attempt < 4; attempt++)
{
    using var cancellation = new CancellationTokenSource();
    rapidDiscovery.Started = cancellation.Cancel;
    var search = new DongleController.Session(rapidDiscovery, _ => { }, cancellation.Token);
    try { search.Search(TimeSpan.FromSeconds(5)); throw new Exception("Search ignored immediate cancellation"); }
    catch (OperationCanceledException) { }
    Check(!rapidDiscovery.Searching && rapidDiscovery.Reads == 0 && rapidDiscovery.Commands.Count(command => command == (0xb0, 2)) == attempt + 1, "rapid cancel waits for firmware startup before stopping, even with constant status 82: " + attempt);
}

var lostStart = new DiscoveryTransport { LoseStartReply = true };
try { new DongleController.Session(lostStart, _ => { }, CancellationToken.None).Search(TimeSpan.FromSeconds(5)); throw new Exception("Lost start reply was ignored"); }
catch (TimeoutException)
{
    Check(!lostStart.Searching && lostStart.Commands.TakeLast(2).SequenceEqual(new (byte, byte)[] { (0xb0, 0xff), (0xb0, 2) }), "a lost start ACK still queries and exits pairing mode while reporting the original timeout");
}

var rejectedStart = new DiscoveryTransport { RejectStart = true };
try { new DongleController.Session(rejectedStart, _ => { }, CancellationToken.None).Search(TimeSpan.FromSeconds(5)); throw new Exception("Start rejection was ignored"); }
catch (IOException ex)
{
    Check(ex.Message.EndsWith("83") && !rejectedStart.Searching && rejectedStart.Commands.All(command => command != (0xb0, 2)), "start rejection 83 remains an error and inactive search receives no blind stop command");
}

using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel(); var discovery = new DiscoveryTransport();
    try { new DongleController.Session(discovery, _ => { }, cancellation.Token).Search(TimeSpan.FromSeconds(5)); throw new Exception("Pre-canceled search was started"); }
    catch (OperationCanceledException) { Check(discovery.Commands.Count == 0, "canceling before search begins sends no USB command"); }
}

var connected = new DongleSnapshot("Test", "1", [], [], "aptX Adaptive", "01:02:03:04:05:06", "Headphones", 1, 1, false, 0, DateTime.Now);
Check(connected.HasConnectedHeadphone && !(connected with { HeadphoneAddress = "00:00:00:00:00:00" }).HasConnectedHeadphone && !(connected with { HeadphoneAddress = "" }).HasConnectedHeadphone && !(connected with { ActiveCodec = "未连接" }).HasConnectedHeadphone, "connected status requires both a nonzero headphone address and an active codec");

Console.WriteLine($"{count} checks passed.");

sealed class AckTransport : IQuestyleTransport
{
    public List<(byte, byte)> Commands { get; } = [];
    public byte[] LastPayload { get; private set; } = [];
    public byte[] Exchange(byte[] packet, TimeSpan timeout)
    {
        Commands.Add((packet[3], packet[4])); LastPayload = packet.AsSpan(6, packet[5]).ToArray();
        byte[] ack = [0x51, 0x4c, packet[2], 0x41, 1, 0x80, 0]; ack[^1] = unchecked((byte)-ack.Sum(x => x)); return ack;
    }
}

sealed class DiscoveryTransport(bool searching = false) : IQuestyleTransport
{
    public bool RejectExit { get; init; }
    public bool RejectStart { get; init; }
    public bool LoseStartReply { get; init; }
    public TimeSpan StartupDelay { get; init; }
    public bool StatusAlways82 { get; init; }
    private long? startedAt;
    public Action? Started { get; set; }
    public bool Searching { get; private set; } = searching;
    public List<(byte, byte)> Commands { get; } = [];
    public int Reads { get; private set; }
    public byte[] Exchange(byte[] packet, TimeSpan timeout)
    {
        Commands.Add((packet[3], packet[4]));
        byte[] payload = [0x80];
        if (packet[3] == 0xb0 && packet[4] == 0xff) payload = [Searching && !StatusAlways82 ? (byte)0x80 : (byte)0x82];
        if (packet[3] == 0xb0 && packet[4] == 0)
        {
            if (Searching || RejectStart) payload = [0x83];
            else
            {
                Searching = true; startedAt = System.Diagnostics.Stopwatch.GetTimestamp(); Started?.Invoke();
                if (LoseStartReply) throw new TimeoutException("Simulated lost search-start ACK");
            }
        }
        if (packet[3] == 0xb0 && packet[4] == 2)
        {
            if (RejectExit || startedAt is long timestamp && System.Diagnostics.Stopwatch.GetElapsedTime(timestamp) < StartupDelay) payload = [0x83];
            else Searching = false;
        }
        if (packet[3] == 0xb0 && packet[4] == 1)
        {
            Reads++;
            payload = [1, 2, 3, 4, 5, (byte)(Reads < 3 ? 6 : 7), (byte)'A', 4];
        }
        var reply = new byte[payload.Length + 6];
        reply[0] = 0x51; reply[1] = 0x4c; reply[2] = packet[2]; reply[3] = 0x41; reply[4] = (byte)payload.Length;
        payload.CopyTo(reply, 5); reply[^1] = unchecked((byte)-reply.Sum(value => value));
        return reply;
    }
}
