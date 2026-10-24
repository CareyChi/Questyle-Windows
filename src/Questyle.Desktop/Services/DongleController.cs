using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Questyle.Desktop.Services;

public sealed record CodecOption(byte Code, string Name)
{
    public override string ToString() => Name;
}

public sealed record AudioSink(string Address, string Name, byte Codec)
{
    public override string ToString() => $"{Name}  ·  {Address}";
    public static AudioSink? Parse(byte[] bytes)
    {
        if (bytes.Length < 7) return null;
        return new(string.Join(":", bytes.Take(6).Select(b => b.ToString("X2"))),
            Encoding.UTF8.GetString(bytes, 6, bytes.Length - 7).TrimEnd('\0'), bytes[^1]);
    }
}

public sealed record DongleSnapshot(string Name, string Version, IReadOnlyList<CodecOption> SupportedCodecs,
    IReadOnlyList<CodecOption> HeadphoneCodecs, string ActiveCodec, string HeadphoneAddress,
    string HeadphoneName, byte Ldac, byte Adaptive, bool GameMode, byte Auracast, DateTime ReadAt);

public sealed class DongleController
{
    // AudioSinkUtil.changeCodecs and fromValue, independently cross-checked
    // against the APK's big-endian supported-codec bit masks.
    public static readonly CodecOption[] Codecs = [new(8,"LDAC"),new(4,"aptX Adaptive"),new(2,"aptX HD"),
        new(1,"aptX"),new(7,"LHDC"),new(9,"AAC"),new(10,"LE Audio"),new(11,"SBC")];
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action<string>? Log;

    public static IReadOnlyList<CodecOption> ParseCodecs(ReadOnlySpan<byte> bytes)
    {
        uint mask = 0; foreach (var value in bytes) mask = (mask << 8) | value;
        return Codecs.Where(c => (mask & (1u << (c.Code - 1))) != 0).ToArray();
    }

    public async Task<T> RunAsync<T>(Func<Session, T> action, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var devices = UsbDkTransport.Enumerate();
                // StopRedirect re-enumerates the composite device. A second
                // operation may arrive before Windows has made it visible again.
                for (int attempt = 0; devices.Count == 0 && attempt < 10; attempt++)
                {
                    ct.ThrowIfCancellationRequested(); Thread.Sleep(400); devices = UsbDkTransport.Enumerate();
                }
                if (devices.Count == 0) throw new IOException("没有找到 Questyle USB Dongle，请检查 USB 连接。");
                if (devices.Count > 1) throw new IOException("检测到多个 Dongle，请先只保留要设置的一个。");
                using var raw = new UsbDkTransport();
                raw.Log += s => Log?.Invoke(s);
                Log?.Invoke("开始 USB 控制会话（音频暂时中断）");
                try
                {
                    raw.Open(devices[0]);
                    Thread.Sleep(300);
                    var session = new Session(raw, s => Log?.Invoke(s), ct);
                    session.Handshake();
                    return action(session);
                }
                finally { raw.Dispose(); Log?.Invoke("已释放 USB 设备，Windows 正在恢复音频接口。"); }
            }, ct);
        }
        finally { gate.Release(); }
    }

    public Task<DongleSnapshot> ReadAsync(CancellationToken ct = default) => RunAsync(s => s.Snapshot(), ct);

    public sealed class Session(IQuestyleTransport raw, Action<string> log, CancellationToken ct)
    {
        private byte sequence = 1;
        private bool cleaningUp;
        public byte[] Send(byte command, byte subCommand, byte[]? data = null)
        {
            if (!cleaningUp) ct.ThrowIfCancellationRequested();
            Thread.Sleep(90);
            sequence = sequence == 255 ? (byte)1 : (byte)(sequence + 1);
            var frame = QuestyleProtocol.Encode(sequence, command, subCommand, data ?? []);
            log("TX " + Convert.ToHexString(frame));
            var reply = raw.Exchange(frame, TimeSpan.FromSeconds(2));
            if (!QuestyleProtocol.TryDecode(reply, out var response) || response!.Sequence != sequence)
                throw new IOException("设备响应的长度、序号或校验和不正确。");
            return response.Data;
        }

        public void Handshake()
        {
            var nonce = Send(0x10, 0);
            var result = Send(0x10, 1, nonce);
            RequireAck(result);
            log("Questyle 双阶段握手成功");
        }

        public static void RequireAck(byte[] bytes)
        {
            if (bytes.Length != 1 || bytes[0] != 0x80)
                throw new IOException("设备没有确认此操作：" + Convert.ToHexString(bytes));
        }

        public void SetCodec(byte codec)
        {
            if (!Codecs.Any(c => c.Code == codec)) throw new ArgumentOutOfRangeException(nameof(codec));
            var supported = ParseCodecs(Send(0x80, 2));
            if (!supported.Any(c => c.Code == codec)) throw new IOException("此 Dongle 不支持所选编解码。");
            RequireAck(Send(0x50, codec));
        }
        public void SetLdac(byte value) { if (value is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(value)); RequireAck(Send(0xe0, value)); }
        public void SetAdaptive(byte value) { if (value is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(value)); RequireAck(Send(0xf0, value)); }
        public void SetGame(bool value) => RequireAck(Send(0x11, value ? (byte)2 : (byte)1));
        public void SetAuracast(bool value) => RequireAck(Send(0x22, value ? (byte)1 : (byte)0));
        public void Rename(string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name.Trim());
            if (bytes.Length is < 1 or > 22) throw new ArgumentException("名称须为 1–22 个 UTF-8 字节（与 APK 限制一致）。");
            RequireAck(Send(0x90, 1, bytes));
        }
        public void Connect(string address) => RequireAck(Send(0xc0, 0, ParseAddress(address)));
        public void Disconnect() => RequireAck(Send(0xc0, 1));
        public void Forget(string address) => RequireAck(Send(0xc0, 2, ParseAddress(address)));
        private static byte[] ParseAddress(string address)
        {
            var bytes = Convert.FromHexString(address.Replace(":", ""));
            if (bytes.Length != 6) throw new ArgumentException("蓝牙地址必须包含 6 个字节。");
            return bytes;
        }
        public AudioSink[] Search(TimeSpan duration)
        {
            var found = new Dictionary<string, AudioSink>();
            RequireAck(Send(0xb0, 0));
            try
            {
                var until = DateTime.UtcNow + duration;
                while (DateTime.UtcNow < until)
                {
                    ct.ThrowIfCancellationRequested();
                    var sink = AudioSink.Parse(Send(0xb0, 1));
                    if (sink != null) found[sink.Address] = sink;
                    Thread.Sleep(300);
                }
                return found.Values.ToArray();
            }
            finally
            {
                cleaningUp = true;
                try { RequireAck(Send(0xb0, 2)); }
                catch (Exception ex) { log("退出搜索模式失败：" + ex.Message); }
                finally { cleaningUp = false; }
            }
        }

        public DongleSnapshot Snapshot()
        {
            string name = Encoding.UTF8.GetString(Send(0x90, 2)).TrimEnd('\0');
            var version = Send(0x20, 0);
            // APK parse consumes 8 bytes; the UI displays major.minor.patch.build.
            string versionText = version.Length == 8 ? $"{version[0]}.{version[1]}.{version[6]}.{version[7]}" : Convert.ToHexString(version);
            var supported = ParseCodecs(Send(0x80, 2));
            var headphones = ParseCodecs(Send(0x80, 1));
            var status = Send(0x30, 2);
            string codec = status.Length >= 2 ? ParseCodecs(status.AsSpan(0, 2)).FirstOrDefault()?.Name ?? "未识别" : "未连接";
            string address = status.Length >= 10 ? string.Join(":", status.Skip(4).Take(6).Select(v => v.ToString("X2"))) : "";
            string sinkName = "";
            if (status.Length >= 10 && status.Skip(4).Take(6).Any(v => v != 0))
            {
                try { sinkName = AudioSink.Parse(Send(0x30, 1, status.Skip(4).Take(6).ToArray()))?.Name ?? ""; }
                catch (TimeoutException) { log("此固件未返回耳机名称"); }
            }
            var ldac = Send(0xe0, 255);
            var adaptive = Send(0xf0, 255);
            var game = Send(0x11, 255);
            var aura = Send(0x22, 255);
            return new(name, versionText, supported, headphones, codec, address, sinkName,
                ldac.FirstOrDefault(), adaptive.FirstOrDefault(), game.FirstOrDefault() == 2, aura.FirstOrDefault(), DateTime.Now);
        }
    }
}
