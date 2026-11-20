using System.Buffers.Binary;
using System.Text;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// The OpenRGB SDK wire format the watcher speaks, at protocol version <see cref="Protocol"/> (a server answers an older
/// client in that client's format; 3 has no segments, zone flags or acknowledgements). A packet is a 16-byte header -
/// <c>ORGB</c>, device index, packet id, payload size, all little-endian - and its payload. Strings are a 16-bit length
/// (with the trailing NUL) and the bytes.
/// </summary>
internal static class OpenRgbPackets
{
    public const uint Protocol = 3;
    public const int HeaderSize = 16;
    /// <summary>A payload larger than this is refused: no OpenRGB device description comes near it.</summary>
    public const int MaxPayload = 16 * 1024 * 1024;

    public const uint ControllerCount = 0;
    public const uint ControllerData = 1;
    public const uint ProtocolVersion = 40;
    public const uint ClientName = 50;
    public const uint ProfileList = 150;
    public const uint LoadProfile = 152;
    public const uint UpdateLeds = 1050;
    public const uint SetCustomMode = 1100;

    private static ReadOnlySpan<byte> Magic => "ORGB"u8;

    public static byte[] Packet(uint device, uint id, ReadOnlySpan<byte> payload)
    {
        var p = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(p);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(p.AsSpan(HeaderSize));
        return p;
    }

    /// <summary>The packet id and payload size of a header; throws <see cref="InvalidDataException"/> on a foreign or oversized one.</summary>
    public static (uint Id, int Size) ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic)) throw new InvalidDataException("not an OpenRGB SDK packet");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (size > MaxPayload) throw new InvalidDataException($"OpenRGB packet of {size} bytes refused");
        return (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]), (int)size);
    }

    public static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    /// <summary>A NUL-terminated string payload (client name, profile name).</summary>
    public static byte[] Text(string value) => [.. Encoding.UTF8.GetBytes(value), 0];

    /// <summary>UpdateLEDs payload: its own size, the colour count, then that many black colours.</summary>
    public static byte[] BlackLeds(int count)
    {
        int size = 4 + 2 + (4 * count);
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)count);
        return b;
    }

    /// <summary>A controller-count reply (protocol below 6: the count alone).</summary>
    public static int Count(ReadOnlySpan<byte> payload) =>
        payload.Length >= 4 ? (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(payload), 1024u) : throw new InvalidDataException("short controller count");

    /// <summary>A profile-list reply: its size, a 16-bit count, then the names.</summary>
    public static IReadOnlyList<string> Profiles(byte[] payload)
    {
        var r = new Reader(payload);
        r.Skip(4);
        int n = r.U16();
        var names = new List<string>(n);
        for (int i = 0; i < n; i++) names.Add(r.Str());
        return names;
    }

    /// <summary>A controller-data reply at protocol 3: name, the modes, zones and LEDs skipped, then the colours.</summary>
    public static OpenRgbDevice Device(int index, byte[] payload)
    {
        var r = new Reader(payload);
        r.Skip(4 + 4);                                     // data size, device type
        string name = r.Str();
        for (int i = 0; i < 5; i++) r.Str();               // vendor, description, version, serial, location
        int modes = r.U16();
        r.Skip(4);                                         // active mode
        for (int m = 0; m < modes; m++)
        {
            r.Str();
            r.Skip(12 * 4);                                // value, flags, speed min/max, brightness min/max, colours min/max, speed, brightness, direction, colour mode
            r.Skip(4 * r.U16());                           // mode colours
        }
        int zones = r.U16();
        for (int z = 0; z < zones; z++)
        {
            r.Str();
            r.Skip(4 + (3 * 4));                           // type, LEDs min/max/count
            r.Skip(r.U16());                               // matrix map, its size in bytes
        }
        int leds = r.U16();
        for (int l = 0; l < leds; l++)
        {
            r.Str();
            r.Skip(4);                                     // LED value
        }
        int colors = r.U16();
        var list = new uint[colors];
        for (int c = 0; c < colors; c++) list[c] = r.U32();
        return new OpenRgbDevice(index, name, list);
    }

    /// <summary>Bounds-checked little-endian reads; running past the end is <see cref="InvalidDataException"/>.</summary>
    private sealed class Reader(byte[] data)
    {
        private int _pos;

        public void Skip(int n)
        {
            if (n < 0 || n > data.Length - _pos) throw new InvalidDataException("OpenRGB packet ends early");
            _pos += n;
        }

        public ushort U16()
        {
            int at = _pos;
            Skip(2);
            return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        }

        public uint U32()
        {
            int at = _pos;
            Skip(4);
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        }

        public string Str()
        {
            int len = U16();
            int at = _pos;
            Skip(len);
            return Encoding.UTF8.GetString(data, at, len).TrimEnd('\0');
        }
    }
}
