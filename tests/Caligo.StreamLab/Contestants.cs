using Caligo.StreamLab.Listeners;

namespace Caligo.StreamLab;

public sealed record Contestant(
    string Name,
    string Server,
    string Protocol,
    string Container,
    Func<LabContext, IListener> Create,
    bool Browser = false,
    bool Live = true,
    bool Finite = false);

public sealed class LabContext
{
    public required LabSettings Settings { get; init; }

    public required SourceClock Clock { get; init; }

    public required Func<CancellationToken, Task<ChromeHost>> Browser { get; init; }

    public required int Seconds { get; init; }
}

public static class Contestants
{
    public static readonly IReadOnlyList<Contestant> All = new List<Contestant>
    {
        new("caligo-radio", "caligo", "http-mp3", "caligo-dev-caligo-1",
            _ => new ProgressiveListener("http://caligo-lab:9001/radio/", "mp3"), Live: false),
        new("caligo-files", "caligo", "range-mp3", "caligo-dev-caligo-1",
            _ => RangeListener.FirstSongOf("http://caligo-lab:8080/music/playlist.json"), Live: false, Finite: true),
        new("relay-mp3", "relay", "http-mp3", "caligo-dev-relay-1",
            _ => new ProgressiveListener("http://relay-lab:8000/live/", "mp3")),
        new("relay-opus", "relay", "ogg-opus", "caligo-dev-relay-1",
            _ => new ProgressiveListener("http://relay-lab:8000/live/opus", "ogg")),
        new("relay-objects", "relay", "objects-ws", "caligo-dev-relay-1",
            _ => new ObjectsListener("ws://relay-lab:8000/live/moq")),
        new("relay-moq-lite", "relay", "moq-lite-qmux", "caligo-dev-relay-1",
            _ => new MoqLiteListener("ws://relay-lab:8000/live/moq")),
        new("relay-quic", "relay", "moq-lite-quic", "caligo-dev-relay-1",
            _ => new MoqQuicListener("relay-lab", 8000)),
        new("icecast", "icecast", "http-mp3", "caligo-dev-icecast-1",
            _ => new ProgressiveListener("http://icecast-lab:8000/stream", "mp3")),
        new("mediamtx-llhls", "mediamtx", "ll-hls", "caligo-dev-mediamtx-1",
            _ => new HlsListener("http://mediamtx-lab:8888/radio/index.m3u8", "mp4")),
        new("mediamtx-webrtc", "mediamtx", "webrtc", "caligo-dev-mediamtx-1",
            context => new BrowserListener(context.Browser, context.Clock, context.Settings.ProbeOrigin + "/probe/whep.html",
                "http://mediamtx-lab:8889/radio/whep", context.Seconds), Browser: true),
        new("mediamtx-srt", "mediamtx", "srt", "caligo-dev-mediamtx-1",
            context => new FfmpegListener(context.Settings.Ffmpeg, "srt://mediamtx-lab:8890?streamid=read:radio")),
        new("mediamtx-rtmp", "mediamtx", "rtmp", "caligo-dev-mediamtx-1",
            context => new FfmpegListener(context.Settings.Ffmpeg, "rtmp://mediamtx-lab:1935/radio-aac")),
        new("mediamtx-rtsp", "mediamtx", "rtsp", "caligo-dev-mediamtx-1",
            context => new FfmpegListener(context.Settings.Ffmpeg, "rtsp://mediamtx-lab:8554/radio", "-rtsp_transport", "tcp")),
        new("nginx-hls", "nginx", "hls", "caligo-dev-lab-web-1",
            _ => new HlsListener("http://web-lab:8080/hls/radio.m3u8", "mpegts")),
        new("nginx-dash", "nginx", "dash", "caligo-dev-lab-web-1",
            _ => new DashListener("http://web-lab:8080/dash/radio.mpd")),
        new("nginx-range", "nginx", "range-mp3", "caligo-dev-lab-web-1",
            _ => new RangeListener("http://web-lab:8080/files/radio.mp3"), Live: false, Finite: true),
        new("ftp", "vsftpd", "ftp", "caligo-dev-lab-ftp-1",
            _ => new FtpListener("ftp-lab", "radio.mp3"), Live: false, Finite: true)
    };
}
