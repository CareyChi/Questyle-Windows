namespace Questyle.Desktop.Services;

public interface IQuestyleTransport
{
    byte[] Exchange(byte[] packet, TimeSpan timeout);
}

public sealed record QuestyleResponse(byte Sequence, byte Marker, byte[] Data);

public static class QuestyleProtocol
{
    // Recovered from APK classes u2.a.b and s2.h.u. Requests and responses
    // have DIFFERENT layouts: response byte 4 is length, not sub-command.
    public static byte[] Encode(byte sequence, byte command, byte subCommand, ReadOnlySpan<byte> data)
    {
        if (data.Length > 56) throw new ArgumentOutOfRangeException(nameof(data), "USB 数据不能超过 56 字节");
        var frame = new byte[data.Length + 7];
        frame[0] = 0x51; frame[1] = 0x4c; frame[2] = sequence;
        frame[3] = command; frame[4] = subCommand; frame[5] = (byte)data.Length;
        data.CopyTo(frame.AsSpan(6));
        frame[^1] = unchecked((byte)-frame.AsSpan(0, frame.Length - 1).ToArray().Sum(x => x));
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out QuestyleResponse? response)
    {
        response = null;
        if (frame.Length < 6 || frame[0] != 0x51 || frame[1] != 0x4c) return false;
        int length = frame[4];
        if (length > frame.Length - 6) return false;
        // Android ignores the response checksum; validate it here before accepting data.
        int sum = 0;
        for (int i = 0; i < length + 6; i++) sum += frame[i];
        if ((sum & 255) != 0) return false;
        response = new(frame[2], frame[3], frame.Slice(5, length).ToArray());
        return true;
    }
}

