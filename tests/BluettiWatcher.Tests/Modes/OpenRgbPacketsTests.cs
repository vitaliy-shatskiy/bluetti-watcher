using System.Text;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>The SDK wire format at protocol 3, against replies built field by field as OpenRGB 1.1's server writes them.</summary>
public sealed class OpenRgbPacketsTests
{
    private static void Str(BinaryWriter w, string s)
    {
        byte[] b = [.. Encoding.UTF8.GetBytes(s), 0];
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    /// <summary>A controller with one mode (two mode colours), one zone with a 2x1 matrix map, and two LEDs lit red and black.</summary>
    private static byte[] Controller()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0u);                                         // data size (not checked)
        w.Write(0);                                          // device type
        Str(w, "Z790 GAMING X");
        foreach (var s in (string[])["Gigabyte", "RGB Fusion 2 USB", "1.0", "", "HID: x"]) Str(w, s);
        w.Write((ushort)1);
        w.Write(0);                                          // active mode
        Str(w, "Direct");
        for (int i = 0; i < 12; i++) w.Write((uint)i);
        w.Write((ushort)2);
        w.Write(0x0000FFu);
        w.Write(0x00FF00u);
        w.Write((ushort)1);
        Str(w, "ARGB header");
        w.Write(1);
        w.Write(0u); w.Write(32u); w.Write(2u);
        w.Write((ushort)16);                                  // matrix: height, width, two cells
        w.Write(1u); w.Write(2u); w.Write(0u); w.Write(1u);
        w.Write((ushort)2);
        Str(w, "LED 1"); w.Write(0u);
        Str(w, "LED 2"); w.Write(1u);
        w.Write((ushort)2);
        w.Write(0x000000FFu);
        w.Write(0xFF000000u);
        return ms.ToArray();
    }

    [Fact]
    public void Device_ControllerReply_ReadsTheNameAndTheLedColours()
    {
        var d = OpenRgbPackets.Device(3, Controller());

        Assert.Equal("Z790 GAMING X", d.Name);
        Assert.Equal(3, d.Index);
        Assert.Equal([0x000000FFu, 0xFF000000u], d.Colors);
        Assert.False(d.IsOff);
    }

    [Fact]
    public void Device_OnlyTheAlphaByteSet_CountsAsOff() =>
        Assert.True(new OpenRgbDevice(0, "x", [0xFF000000u, 0u]).IsOff);

    [Fact]
    public void Device_TruncatedReply_IsInvalidData()
    {
        byte[] whole = Controller();
        Assert.Throws<InvalidDataException>(() => OpenRgbPackets.Device(0, whole[..^3]));
    }

    [Fact]
    public void Profiles_ListReply_ReadsEveryName()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0u);
        w.Write((ushort)2);
        Str(w, "Normal");
        Str(w, "Night");

        Assert.Equal(["Normal", "Night"], OpenRgbPackets.Profiles(ms.ToArray()));
    }

    [Fact]
    public void BlackLeds_Payload_CarriesItsOwnSizeAndZeroColours()
    {
        byte[] p = OpenRgbPackets.BlackLeds(3);

        Assert.Equal(18, p.Length);
        Assert.Equal(18u, BitConverter.ToUInt32(p, 0));
        Assert.Equal(3, BitConverter.ToUInt16(p, 4));
        Assert.All(p[6..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ReadHeader_ForeignMagic_IsInvalidData() =>
        Assert.Throws<InvalidDataException>(() => OpenRgbPackets.ReadHeader(new byte[16]));

    [Fact]
    public void BuiltInEco_ListsTheLightingActionDisabled()
    {
        var eco = BuiltInModes.Defaults(60, 60).Modes.Single(m => m.Id == BuiltInModes.Eco);
        Assert.False(eco.Actions.Single(a => a.TypeId == RgbLightingModule.Id).Enabled);
    }
}
