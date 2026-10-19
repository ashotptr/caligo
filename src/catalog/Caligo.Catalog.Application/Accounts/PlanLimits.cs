using System.Globalization;
using System.Text.Json;

namespace Caligo.Catalog.Application.Accounts;

public static class PlanLimits
{
    public const string StreamsField = "devices_household_can_watch_at_same_time";
    public const int MaxStreamsAllowed = 16;

    public static int? MaxStreams(string? planInfo)
    {
        if (string.IsNullOrWhiteSpace(planInfo))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(planInfo);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(StreamsField, out var streams))
            {
                return null;
            }

            int value;

            if (streams.ValueKind == JsonValueKind.Number)
            {
                if (!streams.TryGetInt32(out value))
                {
                    return null;
                }
            }
            else if (streams.ValueKind != JsonValueKind.String ||
                     !int.TryParse(streams.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return null;
            }

            return value < 1 ? null : Math.Min(value, MaxStreamsAllowed);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
