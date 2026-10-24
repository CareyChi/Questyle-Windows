using Questyle.Desktop.Services;
using System.Text;
using System.Text.Json;

if (args.Contains("--snapshot") || args.Contains("--same-settings"))
{
    UsbDkTransport.HelperPath = Path.GetFullPath("tools/usbdk-runtime/UsbDk Runtime Library/UsbDk Runtime Library/UsbDkHelper.dll");
    var controller = new DongleController(); controller.Log += Console.WriteLine;
    var current = await controller.ReadAsync();
    Console.WriteLine(JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }));
    if (args.Contains("--same-settings"))
    {
        var after = await controller.RunAsync(s => { s.SetGame(current.GameMode); if (current.ActiveCodec == "LDAC" && current.Ldac is >= 1 and <= 3) s.SetLdac(current.Ldac); if (current.ActiveCodec == "aptX Adaptive" && current.Adaptive is >= 1 and <= 2) s.SetAdaptive(current.Adaptive); return s.Snapshot(); });
        if (after.Ldac != current.Ldac || after.Adaptive != current.Adaptive || after.GameMode != current.GameMode) throw new Exception("Hardware readback mismatch");
        Console.WriteLine("PASS real device acknowledged the original game setting and read back unchanged settings. Codec-specific writes were checked only for the active codec.");
    }
    return;
}

if (args.Contains("--raw-list") || args.Contains("--raw-query"))
{
    UsbDkTransport.HelperPath = Path.GetFullPath("tools/usbdk-runtime/UsbDk Runtime Library/UsbDk Runtime Library/UsbDkHelper.dll");
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
fake.Commands.Clear(); session.SetLdac(1); session.SetLdac(2); session.SetLdac(3); session.SetAdaptive(1); session.SetAdaptive(2);
Check(fake.Commands.SequenceEqual(new (byte, byte)[] { (0xe0, 1), (0xe0, 2), (0xe0, 3), (0xf0, 1), (0xf0, 2) }), "APK LDAC and Adaptive subcommands");
try { session.Rename(new string('中', 8)); throw new Exception("UTF8 length accepted"); } catch (ArgumentException) { Check(true, "rename bound counts UTF8 bytes"); }
session.Connect("01:02:03:04:05:06"); Check(fake.LastPayload.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }), "connect command carries six address bytes");
try { QuestyleProtocol.Encode(1, 1, 1, new byte[57]); throw new Exception("oversized payload accepted"); }
catch (ArgumentOutOfRangeException) { count++; Console.WriteLine("PASS HID payload bound"); }

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
