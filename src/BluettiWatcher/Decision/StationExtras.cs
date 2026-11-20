namespace BluettiWatcher.Decision;

/// <summary>
/// What a V2 station reports beyond a <see cref="Reading"/>'s fields (docs/bluetti-protocol.md, pages 100 and
/// 1100); null = its block was not read this poll. Information only: the decision never reads it.
/// </summary>
/// <param name="PackVolt">Register 100, /10 V.</param>
/// <param name="PackAmp">Register 101, /10 A, unsigned in the app: it does not say charge from discharge.</param>
/// <param name="StationMinutesToFull">Register 104; minutes is the likely unit, not confirmed on a device.</param>
/// <param name="StationMinutesToEmpty">Register 105; the same unit.</param>
/// <param name="Warnings">The set warning bits of 126-129 as the app's codes (E113...), empty when none.</param>
/// <param name="Faults">The set fault bits of 133-138 as codes (E1...), empty when none.</param>
/// <param name="TempAmbient">Register 1151, u16 - 40 °C.</param>
/// <param name="TempInverter">Register 1152, the inverter's hottest point.</param>
/// <param name="TempPvDcDc">Register 1153, the PV DC-DC converter's hottest point.</param>
internal sealed record StationExtras(
    double? PackVolt = null, double? PackAmp = null,
    double? StationMinutesToFull = null, double? StationMinutesToEmpty = null,
    IReadOnlyList<string>? Warnings = null, IReadOnlyList<string>? Faults = null,
    double? TempAmbient = null, double? TempInverter = null, double? TempPvDcDc = null)
{
    /// <summary>The hottest of the three temperatures read, null when none was.</summary>
    public double? TempMax => new[] { TempAmbient, TempInverter, TempPvDcDc }.Max();

    /// <summary>Record equality compares lists by reference; two readings with the same codes are the same.</summary>
    public bool Equals(StationExtras? other) =>
        other is not null && PackVolt == other.PackVolt && PackAmp == other.PackAmp
        && StationMinutesToFull == other.StationMinutesToFull && StationMinutesToEmpty == other.StationMinutesToEmpty
        && Same(Warnings, other.Warnings) && Same(Faults, other.Faults)
        && TempAmbient == other.TempAmbient && TempInverter == other.TempInverter && TempPvDcDc == other.TempPvDcDc;

    public override int GetHashCode() => HashCode.Combine(PackVolt, PackAmp, StationMinutesToEmpty, TempMax, Warnings?.Count, Faults?.Count);

    private static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
}
