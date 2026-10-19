namespace Caligo.StreamLab;

public sealed record Scenario(string Name, string Profile, string Description);

public static class Scenarios
{
    public static readonly IReadOnlyDictionary<string, Scenario> All = new Dictionary<string, Scenario>
    {
        ["baseline"] = new("baseline", "off",
            "the lab network as it is, nothing added"),
        ["broadband"] = new("broadband", "delay_ms=15 jitter_ms=2 rate_kbit=50000 scope=flow",
            "15 ms added on the way to each listener, 50 Mbit/s each"),
        ["cellular"] = new("cellular", "delay_ms=50 jitter_ms=15 loss=0.005 loss_burst=0.3 rate_kbit=4000 queue_ms=150 scope=flow",
            "50 ms with jitter, 0.5 % bursty loss and 4 Mbit/s for each listener"),
        ["lossy"] = new("lossy", "delay_ms=40 jitter_ms=10 loss=0.03 loss_burst=0.4 rate_kbit=8000 scope=flow",
            "3 % bursty loss at 40 ms"),
        ["congested"] = new("congested", "delay_ms=80 jitter_ms=20 rate_kbit=1000 queue_ms=400 scope=flow",
            "1 Mbit/s per listener behind a 400 ms queue"),
        ["shared-uplink"] = new("shared-uplink", "delay_ms=20 rate_kbit=20000 queue_ms=100 scope=link",
            "every listener of a server shares one 20 Mbit/s uplink")
    };
}
