using System.Text.Json;
using System.Text.Json.Serialization;
using Hestia.Modules.Alerts.Contracts;

namespace Hestia.Modules.Alerts.Application;

/// <summary>The values of an alert as JSON in one column. The keys are in ordinal order, so the same values give the same text.</summary>
public static class AlertValuesJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Write(IReadOnlyDictionary<string, AlertValue> values) =>
        JsonSerializer.Serialize(new SortedDictionary<string, AlertValue>(values.ToDictionary(), StringComparer.Ordinal), Json);

    public static IReadOnlyDictionary<string, AlertValue> Read(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, AlertValue>>(json, Json) ?? new Dictionary<string, AlertValue>();
}
