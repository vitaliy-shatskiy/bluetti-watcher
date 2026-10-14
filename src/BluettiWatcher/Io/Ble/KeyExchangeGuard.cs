// The pre-key-exchange message layout ("**", body, 2-byte sum) follows bluetti-bt-lib 0.1.8 (bluetooth/encryption.py,
// class Message, hexsum, msg_challenge), https://github.com/Patrick762/bluetti-bt-lib, under this notice:
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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace BluettiWatcher.Io.Ble;

/// <summary>The station opened the encrypted protocol's key exchange; the native reader speaks the unencrypted protocol only.</summary>
internal sealed class EncryptedStationException : Exception
{
    internal const string Cause =
        "the station started the encrypted Bluetooth handshake (key exchange \"**\"); the native reader reads unencrypted stations only, so the cloud reader answers instead";

    public EncryptedStationException() : base(Cause) { }
    public EncryptedStationException(string message) : base(message) { }
    public EncryptedStationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The library has no per-model encryption flag: <c>use_encryption</c> is the caller's choice, and its <c>bluetti-detect</c>
/// learns it at run time (an encrypted read first, then a plain one). An encrypted station opens with a plain
/// pre-key-exchange message, <c>2A 2A body sum_hi sum_lo</c> (the challenge: body <c>01 04</c> and a 4-byte seed), that
/// no Modbus answer can be (those start with unit 0x01).
/// </summary>
internal static class KeyExchange
{
    /// <summary>The pre-key-exchange types a station sends unencrypted (encryption.py MessageType: CHALLENGE, CHALLENGE_ACCEPTED).</summary>
    private static readonly byte[] PlainTypes = [1, 3];

    /// <summary>True for a whole, well-formed plain pre-key-exchange message: magic, type and the body's 16-bit sum all agree.</summary>
    public static bool IsPreKeyMessage(ReadOnlySpan<byte> note)
    {
        if (note.Length < 5 || note[0] != (byte)'*' || note[1] != (byte)'*') return false;
        var body = note[2..^2];
        if (!PlainTypes.Contains(body[0])) return false;
        int sum = 0;
        foreach (byte b in body) sum += b;
        return note[^2] == (byte)(sum >> 8) && note[^1] == (byte)sum;
    }

    /// <summary>The first bytes of a link, which may still turn out to be a pre-key message: <c>**</c>, then a plain type (a prefix of one is enough).</summary>
    public static bool MayBePreKey(ReadOnlySpan<byte> head) =>
        head.Length > 0 && head[0] == (byte)'*' && (head.Length < 2 || head[1] == (byte)'*') && (head.Length < 3 || PlainTypes.Contains(head[2]));

    /// <summary>True when the bytes received so far from the start of the link begin with a whole, well-formed pre-key message (it may arrive split over notifications).</summary>
    public static bool StartsWithPreKeyMessage(ReadOnlySpan<byte> head)
    {
        for (int length = 5; length <= head.Length; length++)
            if (IsPreKeyMessage(head[..length])) return true;
        return false;
    }
}

/// <summary>
/// Wraps a link: a pre-key-exchange message on the notification channel ends the poll with
/// <see cref="EncryptedStationException"/> (the reader turns it into a miss with that text); everything else passes unchanged.
/// </summary>
internal sealed class KeyExchangeGuardLink(IBleLink inner) : IBleLink
{
    private readonly GuardedReader _reader = new(inner.Notifications);

    public ChannelReader<byte[]> Notifications => _reader;

    public Task WriteAsync(ReadRequest request, CancellationToken ct) => inner.WriteAsync(request, ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// The first notifications of a link are held while they could still be the start of a pre-key message (it may arrive split over
    /// notifications: "**" alone, then the rest); the first byte of a Modbus answer is 0x01, so a plain answer is never held. Held notifications
    /// that turn out plain are passed on in order. A whole pre-key message anywhere later is caught as before.
    /// </summary>
    private sealed class GuardedReader(ChannelReader<byte[]> notes) : ChannelReader<byte[]>
    {
        /// <summary>The longest start held while undecided: past it the link is plain.</summary>
        private const int MaxHead = 64;

        private readonly List<byte> _head = [];
        private readonly List<byte[]> _held = [];
        private readonly Queue<byte[]> _ready = new();
        private bool _decided;
        private bool _encrypted;

        public override Task Completion => notes.Completion;

        public override bool TryRead([MaybeNullWhen(false)] out byte[] item)
        {
            while (true)
            {
                if (_encrypted) throw new EncryptedStationException();
                if (_ready.TryDequeue(out item)) return true;
                if (!notes.TryRead(out var raw)) return false;
                Take(raw);
            }
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            _encrypted ? ValueTask.FromException<bool>(new EncryptedStationException())
            : _ready.Count > 0 ? ValueTask.FromResult(true)
            : notes.WaitToReadAsync(cancellationToken);

        public override async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_encrypted) throw new EncryptedStationException();
                if (_ready.TryDequeue(out var item)) return item;
                Take(await notes.ReadAsync(cancellationToken));
            }
        }

        private void Take(byte[] raw)
        {
            if (_decided)
            {
                if (KeyExchange.IsPreKeyMessage(raw)) Encrypted();
                _ready.Enqueue(raw);
                return;
            }
            _head.AddRange(raw);
            _held.Add(raw);
            if (KeyExchange.StartsWithPreKeyMessage(CollectionsMarshal.AsSpan(_head))) Encrypted();
            if (_head.Count < MaxHead && KeyExchange.MayBePreKey(CollectionsMarshal.AsSpan(_head))) return;
            _decided = true;
            foreach (var h in _held) _ready.Enqueue(h);
            _held.Clear();
            _head.Clear();
        }

        private void Encrypted()
        {
            _encrypted = true;
            throw new EncryptedStationException();
        }
    }
}