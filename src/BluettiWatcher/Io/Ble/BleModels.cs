using System.Diagnostics.CodeAnalysis;

namespace BluettiWatcher.Io.Ble;

/// <summary>Decodes one answer's body read from register <paramref name="start"/>.</summary>
internal delegate Dictionary<string, FieldValue> BodyDecoder(int start, ReadOnlySpan<byte> body);

/// <summary>
/// One station model as the native reader polls it: the batched reads for a <see cref="Decision.Reading"/>, the reading's
/// fields (first register and size, for the one-by-one fallback) and the decoder for the whole model's map.
/// </summary>
/// <remarks>
/// <paramref name="HasAcInput"/>: the map has an AC input power field. Without it (EP600, EP760, EP800, EP2000: battery % only) the
/// reading cannot tell mains from battery, so the reader gives none and the cloud fallback answers.
/// <paramref name="HasOutputMode"/>: the map has the station's own output mode; without it the reader derives the mode (<see cref="V1Models.ToReading"/>).
/// </remarks>
internal sealed record BleModel(string Type, int IotVersion, IReadOnlyList<RegisterRange> Plan, IReadOnlyList<RegisterRange> ReadingFields, BodyDecoder Decode,
    bool HasAcInput, bool HasOutputMode);

/// <summary>The models the native reader reads: the library's IoT V1 table, then its V2 table (unencrypted only).</summary>
internal static class BleModels
{
    private static readonly Dictionary<string, BleModel> ByType = Build();

    /// <summary>Every model name, V1 then V2, as the library's DEVICES keys (case-sensitive).</summary>
    public static IReadOnlyList<string> Names { get; } = [.. V1Models.All.Keys, .. V2Models.All.Keys];

    public static bool TryGet(string? type, [NotNullWhen(true)] out BleModel? model)
    {
        model = null;
        return type is not null && ByType.TryGetValue(type, out model);
    }

    private static Dictionary<string, BleModel> Build()
    {
        var all = new Dictionary<string, BleModel>(StringComparer.Ordinal);
        foreach (var (type, map) in V1Models.All)
        {
            var fields = V1Models.ReadingFields(map);
            all.Add(type, new(type, 1, V1Models.Plan(fields), [.. fields.Select(f => new RegisterRange(f.Address, f.Size))], (s, b) => V1Models.Decode(map, s, b),
                fields.Any(f => f.Name == "AC_INPUT_POWER"), fields.Any(f => f.Name == "AC_OUTPUT_MODE")));
        }
        foreach (var (type, map) in V2Models.All)
        {
            var fields = V2Models.ReadingFields(map);
            all.Add(type, new(type, 2, V2Models.Plan(fields), [.. fields.Select(f => new RegisterRange(f.Address, f.Size))], (s, b) => V2Models.Decode(map, s, b),
                fields.Any(f => f.Name == "AC_INPUT_POWER"), fields.Any(f => f.Name == "AC_OUTPUT_MODE")));
        }
        return all;
    }
}
