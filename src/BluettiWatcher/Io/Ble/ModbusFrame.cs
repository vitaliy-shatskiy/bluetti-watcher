// Frame layout ported from bluetti-bt-lib 0.1.8 (registers/DeviceRegister.py, registers/ReadableRegisters.py),
// https://github.com/Patrick762/bluetti-bt-lib, under this notice:
//
// MIT License
//
// Copyright (c) 2025 Patrick762
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Globalization;

namespace BluettiWatcher.Io.Ble;

/// <summary>CRC-16/MODBUS: polynomial 0xA001 (reflected 0x8005), initial 0xFFFF, sent low byte first.</summary>
internal static class ModbusCrc
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}

/// <summary>
/// One read of holding registers. Its constructor is private and takes no frame: the frame always comes from
/// <see cref="ModbusRead.Encode"/>, so a link can send nothing else.
/// </summary>
internal sealed class ReadRequest
{
    private ReadRequest(int start, int quantity)
    {
        _frame = ModbusRead.Encode(ModbusRead.ReadHoldingRegisters, start, quantity);
        Start = start;
        Quantity = quantity;
    }

    /// <summary>Only <see cref="ModbusRead.Request"/> calls this.</summary>
    internal static ReadRequest Of(int start, int quantity) => new(start, quantity);

    private readonly byte[] _frame;
    public int Start { get; }
    public int Quantity { get; }
    /// <summary>The 8 bytes to write: unit, function 0x03, address and quantity big-endian, CRC low byte first.</summary>
    public ReadOnlyMemory<byte> Frame => _frame;
    /// <summary>The answer's length: unit, function, byte count, 2 bytes per register, CRC.</summary>
    public int ExpectedLength => 2 * Quantity + 5;
}

/// <summary>
/// READ ONLY, FOREVER: the only Modbus frame this program can build is function 0x03 (read holding registers), and never
/// over a control register (2000 and up). There is no write, pairing, protection-level or encryption code.
/// </summary>
internal static class ModbusRead
{
    public const byte UnitId = 0x01;
    public const byte ReadHoldingRegisters = 0x03;
    /// <summary>The first control register: V1 (UPS mode 3001, AC switch 3007, power off 3060...) and V2 (AC switch 2011, DC 2012, eco 2014-2021) switches all lie at or above it, while no read field does (V1 maps end at register 88, V2 below 2000): never read, never written.</summary>
    public const int ControlRegisterFloor = 2000;
    /// <summary>The most registers one request asks for.</summary>
    public const int MaxQuantity = 32;

    public static ReadRequest Request(int start, int quantity) => ReadRequest.Of(start, quantity);

    /// <summary>The encoder itself; any function code but 0x03 is refused, as is any range reaching the control registers.</summary>
    internal static byte[] Encode(byte function, int start, int quantity)
    {
        if (function != ReadHoldingRegisters)
            throw new ArgumentOutOfRangeException(nameof(function), function, "read only: the only Modbus function is 0x03 (read holding registers)");
        if (quantity is < 1 or > MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, string.Create(CultureInfo.InvariantCulture, $"1 to {MaxQuantity} registers"));
        if (start < 0 || start + quantity > ControlRegisterFloor)
            throw new ArgumentOutOfRangeException(nameof(start), start, string.Create(CultureInfo.InvariantCulture, $"read only: registers {ControlRegisterFloor} and up are never touched"));
        var frame = new byte[8];
        frame[0] = UnitId;
        frame[1] = function;
        frame[2] = (byte)(start >> 8);
        frame[3] = (byte)start;
        frame[4] = (byte)(quantity >> 8);
        frame[5] = (byte)quantity;
        ushort crc = ModbusCrc.Compute(frame.AsSpan(0, 6));
        frame[6] = (byte)crc;
        frame[7] = (byte)(crc >> 8);
        return frame;
    }
}

internal enum AssemblyState { NeedMore, Complete, Exception, Invalid }

/// <summary>
/// Collects the notifications of one answer until it is whole: <c>01 03 n data[n] crc</c>, n = 2 x quantity; or the
/// exception reply <c>01 83 code crc</c>. Unlike the library (the first notification wins), the length and the CRC are checked.
/// </summary>
internal sealed class ResponseAssembler(ReadRequest request)
{
    private const int ExceptionLength = 5;
    private readonly List<byte> _buffer = new(request.ExpectedLength);

    public byte[]? Body { get; private set; }
    public byte? ExceptionCode { get; private set; }
    public string? Error { get; private set; }
    public int Received => _buffer.Count;
    public AssemblyState State { get; private set; }

    public AssemblyState Feed(ReadOnlySpan<byte> chunk)
    {
        if (State != AssemblyState.NeedMore) return State;
        foreach (byte b in chunk) _buffer.Add(b);
        return State = Check();
    }

    /// <summary>The wait for the rest of the answer ran out: an unfinished answer becomes invalid, naming why.</summary>
    public void TimedOut(string why)
    {
        if (State != AssemblyState.NeedMore) return;
        Error = why;
        State = AssemblyState.Invalid;
    }

    private AssemblyState Check()
    {
        int n = _buffer.Count;
        if (n >= 1 && _buffer[0] != ModbusRead.UnitId) return Invalid(string.Create(CultureInfo.InvariantCulture, $"answer from unit {_buffer[0]}, not {ModbusRead.UnitId}"));
        if (n < 2) return AssemblyState.NeedMore;
        byte function = _buffer[1];
        if (function == (ModbusRead.ReadHoldingRegisters | 0x80))
        {
            if (n < ExceptionLength) return AssemblyState.NeedMore;
            if (n > ExceptionLength) return Invalid(string.Create(CultureInfo.InvariantCulture, $"exception reply longer than {ExceptionLength} bytes ({n})"));
            if (!CrcOk()) return Invalid("bad CRC on the exception reply");
            ExceptionCode = _buffer[2];
            Error = string.Create(CultureInfo.InvariantCulture, $"Modbus exception 0x{_buffer[2]:X2} ({ExceptionName(_buffer[2])}) for registers {request.Start}+{request.Quantity}");
            return AssemblyState.Exception;
        }
        if (function != ModbusRead.ReadHoldingRegisters) return Invalid(string.Create(CultureInfo.InvariantCulture, $"answer with function 0x{function:X2}, not 0x03"));
        if (n < 3) return AssemblyState.NeedMore;
        if (_buffer[2] != 2 * request.Quantity)
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"byte count {_buffer[2]}, expected {2 * request.Quantity} for registers {request.Start}+{request.Quantity}"));
        if (n < request.ExpectedLength) return AssemblyState.NeedMore;
        if (n > request.ExpectedLength) return Invalid(string.Create(CultureInfo.InvariantCulture, $"answer longer than {request.ExpectedLength} bytes ({n})"));
        if (!CrcOk()) return Invalid(string.Create(CultureInfo.InvariantCulture, $"bad CRC for registers {request.Start}+{request.Quantity}"));
        Body = [.. _buffer.GetRange(3, 2 * request.Quantity)];
        return AssemblyState.Complete;
    }

    private bool CrcOk()
    {
        var frame = _buffer.ToArray();
        ushort crc = ModbusCrc.Compute(frame.AsSpan(0, frame.Length - 2));
        return frame[^2] == (byte)crc && frame[^1] == (byte)(crc >> 8);
    }

    private AssemblyState Invalid(string why)
    {
        Error = why;
        return AssemblyState.Invalid;
    }

    private static string ExceptionName(byte code) => code switch
    {
        0x01 => "illegal function",
        0x02 => "illegal data address",
        0x03 => "illegal data value",
        0x04 => "server device failure",
        0x06 => "server device busy",
        _ => "unknown",
    };
}
