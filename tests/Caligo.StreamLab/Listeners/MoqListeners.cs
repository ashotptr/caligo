using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
using System.Text.Json;
using Caligo.StreamLab.Media;

namespace Caligo.StreamLab.Listeners;

public static class MoqLite
{
    public const string Broadcast = "caligo/radio/main";

    public static readonly byte[] Setup = { 0x01, 0x01, 0x00 };

    public static byte[] Subscribe(ulong id, string track)
    {
        var body = Varint.Of(id).Concat(Varint.Text(Broadcast)).Concat(Varint.Text(track)).Concat(new byte[] { 0 })
            .Concat(Varint.Of(0)).Concat(Varint.Of(0)).Concat(Varint.Of(0)).Concat(Varint.Of(0)).Concat(Varint.Of(0)).ToArray();

        return Varint.Of(2).Concat(Varint.Message(body)).ToArray();
    }
}

public sealed class OpusSink
{
    private readonly ListenerSession _session;
    private readonly OggOpusWriter _ogg = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OpusSink(ListenerSession session)
    {
        _session = session;
    }

    public int Channels { get; set; } = 2;

    public async ValueTask PacketAsync(ReadOnlyMemory<byte> packet)
    {
        _session.Media(Opus.Samples(packet.Span) / 48.0);

        if (_session.Probe is null)
        {
            return;
        }

        await _gate.WaitAsync();

        try
        {
            await _session.TapAsync(_ogg.Packet(packet.Span, Channels), "ogg");
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class GroupReader
{
    private readonly List<byte> _data = new();
    private int _position;
    private bool _typed;
    private bool _headed;
    private bool _group;

    public async ValueTask FeedAsync(ReadOnlyMemory<byte> bytes, OpusSink sink)
    {
        _data.AddRange(bytes.ToArray());

        while (TryNextPacket(out var packet))
        {
            await sink.PacketAsync(packet);
        }

        if (_position > 65536)
        {
            _data.RemoveRange(0, _position);
            _position = 0;
        }
    }

    private bool TryNextPacket(out byte[] packet)
    {
        packet = Array.Empty<byte>();

        while (true)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_data);
            var position = _position;

            if (!_typed)
            {
                if (!Varint.TryRead(span, ref position, out var type))
                {
                    return false;
                }

                _typed = true;
                _group = type == 0;
                _position = position;

                continue;
            }

            if (!_group)
            {
                _position = span.Length;

                return false;
            }

            if (!_headed)
            {
                if (!Varint.TryRead(span, ref position, out var size) || position + (int)size > span.Length)
                {
                    return false;
                }

                _headed = true;
                _position = position + (int)size;

                continue;
            }

            if (!Varint.TryRead(span, ref position, out _) || !Varint.TryRead(span, ref position, out var length) ||
                position + (int)length > span.Length)
            {
                return false;
            }

            packet = span.Slice(position, (int)length).ToArray();
            _position = position + (int)length;

            return true;
        }
    }
}

public sealed class ObjectsListener : IListener
{
    private readonly Uri _url;

    public ObjectsListener(string url)
    {
        _url = new Uri(url);
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();

        socket.Options.AddSubProtocol("caligo-objects-0");

        await socket.ConnectAsync(_url, cancellationToken);
        session.MarkConnected();

        var sink = new OpusSink(session);
        var buffer = new byte[1 << 17];

        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
                length += result.Count;
            }
            while (!result.EndOfMessage && length < buffer.Length);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            session.Received(length);

            if (result.MessageType != WebSocketMessageType.Binary || length < 4 || buffer[0] != 0x01)
            {
                continue;
            }

            var position = 1;
            var frame = buffer.AsMemory(0, length);

            if (!Varint.TryRead(frame.Span, ref position, out var track) || !Varint.TryRead(frame.Span, ref position, out _) ||
                !Varint.TryRead(frame.Span, ref position, out _))
            {
                continue;
            }

            if (track == 0)
            {
                using var catalog = JsonDocument.Parse(buffer.AsMemory(position, length - position));

                foreach (var item in catalog.RootElement.GetProperty("tracks").EnumerateArray())
                {
                    if (item.TryGetProperty("channels", out var channels))
                    {
                        sink.Channels = channels.GetInt32();
                    }
                }
            }
            else if (track == 1 && frame.Span[position] == 0)
            {
                position++;

                if (Varint.TryRead(frame.Span, ref position, out _) && position < length)
                {
                    await sink.PacketAsync(buffer.AsMemory(position, length - position).ToArray());
                }
            }
        }
    }
}

public sealed class MoqLiteListener : IListener
{
    private const ulong Parameters = 0x3f5153300d0a0d0aUL;
    private const ulong PingRequest = 0x348c67529ef8c7bdUL;
    private const ulong PingResponse = 0x348c67529ef8c7beUL;
    private const ulong MaxRecord = 0x0571c59429cd0845UL;
    private const ulong DataWindow = 64 * 1024 * 1024;
    private const ulong StreamGrant = 100;

    private readonly Uri _url;

    public MoqLiteListener(string url)
    {
        _url = new Uri(url);
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();

        socket.Options.AddSubProtocol("qmux-02.moq-lite-06");

        await socket.ConnectAsync(_url, cancellationToken);
        session.MarkConnected();

        var sink = new OpusSink(session);
        var groups = new Dictionary<ulong, GroupReader>();
        var first = new List<byte>();
        var parameters = new List<byte>();

        void Parameter(ulong id, ulong value)
        {
            Varint.Write(parameters, id);
            Varint.Write(parameters, (ulong)Varint.Size(value));
            Varint.Write(parameters, value);
        }

        Parameter(0x01, 30000);
        Parameter(0x04, DataWindow);
        Parameter(0x05, 1 << 20);
        Parameter(0x06, 1 << 20);
        Parameter(0x07, 1 << 20);
        Parameter(0x08, 16);
        Parameter(0x09, StreamGrant);
        Parameter(MaxRecord, 16382);

        Varint.Write(first, Parameters);
        Varint.Write(first, (ulong)parameters.Count);
        first.AddRange(parameters);
        Stream(first, 2, MoqLite.Setup, fin: true);
        Stream(first, 0, MoqLite.Subscribe(1, "audio/hi"), fin: false);

        await socket.SendAsync(first.ToArray(), WebSocketMessageType.Binary, true, cancellationToken);

        var buffer = new byte[1 << 17];
        ulong received = 0;
        ulong granted = DataWindow;
        ulong closed = 0;
        ulong streams = StreamGrant;

        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
                length += result.Count;
            }
            while (!result.EndOfMessage && length < buffer.Length);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            session.Received(length);

            var reply = new List<byte>();
            var position = 0;

            while (position < length)
            {
                var frame = buffer.AsMemory(0, length);

                if (!Varint.TryRead(frame.Span, ref position, out var type))
                {
                    break;
                }

                if (type is >= 0x08 and <= 0x0f)
                {
                    Varint.TryRead(frame.Span, ref position, out var id);

                    ulong offset = 0;
                    var size = (ulong)(length - position);

                    if ((type & 0x04) != 0)
                    {
                        Varint.TryRead(frame.Span, ref position, out offset);
                    }

                    if ((type & 0x02) != 0)
                    {
                        Varint.TryRead(frame.Span, ref position, out size);
                    }

                    var data = buffer.AsMemory(position, (int)size);

                    position += (int)size;
                    received += size;

                    if (id % 4 == 3)
                    {
                        if (!groups.TryGetValue(id, out var reader))
                        {
                            groups[id] = reader = new GroupReader();
                        }

                        await reader.FeedAsync(data, sink);

                        if ((type & 0x01) != 0)
                        {
                            groups.Remove(id);
                            closed++;
                        }
                    }
                }
                else if (type == 0x04 || type == 0x24)
                {
                    Varint.TryRead(frame.Span, ref position, out var id);
                    Varint.TryRead(frame.Span, ref position, out _);
                    Varint.TryRead(frame.Span, ref position, out _);

                    if (type == 0x24)
                    {
                        Varint.TryRead(frame.Span, ref position, out _);
                    }

                    if (groups.Remove(id))
                    {
                        closed++;
                    }
                }
                else if (type is 0x05 or 0x11 or 0x15)
                {
                    Varint.TryRead(frame.Span, ref position, out _);
                    Varint.TryRead(frame.Span, ref position, out _);
                }
                else if (type is 0x10 or 0x12 or 0x13 or 0x14 or 0x16 or 0x17)
                {
                    Varint.TryRead(frame.Span, ref position, out _);
                }
                else if (type is 0x1c or 0x1d)
                {
                    return;
                }
                else if (type is 0x30 or 0x31)
                {
                    var size = (ulong)(length - position);

                    if (type == 0x31)
                    {
                        Varint.TryRead(frame.Span, ref position, out size);
                    }

                    position += (int)size;
                }
                else if (type == PingRequest)
                {
                    Varint.TryRead(frame.Span, ref position, out var sequence);
                    Varint.Write(reply, PingResponse);
                    Varint.Write(reply, sequence);
                }
                else if (type == PingResponse)
                {
                    Varint.TryRead(frame.Span, ref position, out _);
                }
                else if (type == Parameters)
                {
                    Varint.TryRead(frame.Span, ref position, out var size);

                    position += (int)size;
                }
                else if (type != 0x00)
                {
                    session.Fail($"QMux frame type 0x{type:x} is not understood");

                    return;
                }
            }

            if (granted - received < DataWindow / 2)
            {
                granted = received + DataWindow;
                Varint.Write(reply, 0x10);
                Varint.Write(reply, granted);
            }

            if (streams - closed < StreamGrant / 2)
            {
                streams = closed + StreamGrant;
                Varint.Write(reply, 0x13);
                Varint.Write(reply, streams);
            }

            if (reply.Count > 0)
            {
                await socket.SendAsync(reply.ToArray(), WebSocketMessageType.Binary, true, cancellationToken);
            }
        }
    }

    private static void Stream(List<byte> record, ulong id, byte[] data, bool fin)
    {
        Varint.Write(record, fin ? 0x0fUL : 0x0eUL);
        Varint.Write(record, id);
        Varint.Write(record, 0);
        Varint.Write(record, (ulong)data.Length);
        record.AddRange(data);
    }
}

public sealed class MoqQuicListener : IListener
{
    private readonly string _host;
    private readonly int _port;

    public MoqQuicListener(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task RunAsync(ListenerSession session, CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC needs libmsquic in this image");
        }

        var addresses = await Dns.GetHostAddressesAsync(_host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken);
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(addresses.First(), _port),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            MaxInboundUnidirectionalStreams = 1000,
            MaxInboundBidirectionalStreams = 16,
            IdleTimeout = TimeSpan.FromSeconds(30),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { new("moq-lite-06") },
                TargetHost = "localhost",
                CertificateChainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    DisableCertificateDownloads = true,
                    TrustMode = X509ChainTrustMode.CustomRootTrust
                },
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        await using var connection = await QuicConnection.ConnectAsync(options, cancellationToken);

        session.MarkConnected();

        await using (var setup = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken))
        {
            await setup.WriteAsync(MoqLite.Setup, completeWrites: true, cancellationToken);
        }

        await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);

        await control.WriteAsync(MoqLite.Subscribe(1, "audio/hi"), cancellationToken);

        var sink = new OpusSink(session);
        var readers = new List<Task>();

        try
        {
            while (true)
            {
                var stream = await connection.AcceptInboundStreamAsync(cancellationToken);

                readers.Add(ReadAsync(stream, session, sink, cancellationToken));
                readers.RemoveAll(task => task.IsCompleted);
            }
        }
        finally
        {
            await Task.WhenAll(readers.Select(task => task.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private static async Task ReadAsync(QuicStream stream, ListenerSession session, OpusSink sink, CancellationToken cancellationToken)
    {
        await using (stream)
        {
            var reader = new GroupReader();
            var buffer = new byte[16384];

            try
            {
                int read;

                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    session.Received(read);

                    await reader.FeedAsync(buffer.AsMemory(0, read), sink);
                }
            }
            catch (QuicException)
            {
            }
        }
    }
}
