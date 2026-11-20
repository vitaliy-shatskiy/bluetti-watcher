using System.Globalization;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Tray;

/// <summary>
/// The tray's words in the page's language (<see cref="Io.PageSettingsStore.Lang"/>): English or Ukrainian. One fact per
/// line, units after a space, no middle dot. Engine details (event reasons, command errors) stay as the engine wrote them.
/// </summary>
internal sealed class TrayWords
{
    public static readonly TrayWords En = new(uk: false);
    public static readonly TrayWords Uk = new(uk: true);

    private readonly bool _uk;

    private TrayWords(bool uk) => _uk = uk;

    /// <summary>"uk" is Ukrainian; anything else English.</summary>
    public static TrayWords For(string? lang) => string.Equals(lang, "uk", StringComparison.Ordinal) ? Uk : En;

    private string W(string en, string uk) => _uk ? uk : en;

    private static string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

    public string Auto => W("Auto", "Авто");
    public string OpenPanel => W("Open panel", "Відкрити панель");
    public string ExitTray => W("Exit tray", "Закрити значок");

    /// <summary>A built-in mode's English name in this language; a custom or renamed mode keeps its own.</summary>
    public string MenuModeName(string name) => name switch
    {
        "Normal" => W(name, "Звичайний"),
        "Eco" => W(name, "Еко"),
        "Max" => W(name, "Макс"),
        _ => name,
    };

    /// <summary>A flag or built-in mode id as a name; any other id as it is.</summary>
    public string ModeName(string id) => id switch
    {
        "normal" => W("Normal", "Звичайний"),
        "eco" => W("Eco", "Еко"),
        "max" => W("Max", "Макс"),
        "emergency" => W("Emergency", "Аварійний"),
        "unknown" => W("Unknown", "Невідомий"),
        "starting" => W("Starting", "Запуск"),
        _ => id,
    };

    public string Battery(double? soc) => soc is { } v ? W("Battery ", "Батарея ") + TrayIconRenderer.Text(v) + " %" : W("Battery ?", "Батарея ?");
    public string OnMains => W("On mains", "Від мережі");
    public string OnBattery => W("On battery", "Від батареї");
    public string NoReadingSince(string clock) => W("No reading since ", "Немає даних з ") + clock;
    public string NoReadingYet => W("No reading yet", "Даних ще немає");
    public string In(double? watts) => W("In ", "Вхід ") + Watts(watts);
    public string Out(double? watts) => W("Out ", "Вихід ") + Watts(watts);
    private string Watts(double? w) => (w is { } v ? N(v) : "?") + W(" W", " Вт");

    /// <summary>The mode the flag holds, then the user's choice (Auto, or a mode held by hand).</summary>
    public string ModeLine(string modeId, ManualMode choice) => W("Mode ", "Режим ") + ModeName(modeId) + choice switch
    {
        ManualMode.Eco => W(", eco chosen", ", вибрано Еко"),
        ManualMode.Max => W(", max chosen", ", вибрано Макс"),
        _ => W(", auto", ", авто"),
    };

    public string TimeLeft(double hours) => W("Time left ", "Вистачить на ") + Duration(hours);

    /// <summary>Hours as "3 h 20 min" (minutes rounded), "45 min" under an hour.</summary>
    public string Duration(double hours)
    {
        long minutes = (long)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        string h = (minutes / 60).ToString(CultureInfo.InvariantCulture), m = (minutes % 60).ToString(CultureInfo.InvariantCulture);
        return minutes < 60 ? m + W(" min", " хв") : h + W(" h ", " год ") + m + W(" min", " хв");
    }

    /// <summary>"Battery 59 %. " or empty: the lead of a notice text.</summary>
    public string BatteryLead(double? soc) => soc is null ? "" : Battery(soc) + ". ";

    /// <summary>A mode-change detail "eco (reason)" with a built-in mode id named in this language; the reason stays.</summary>
    public string ModeChange(string detail)
    {
        int cut = detail.IndexOf(" (", StringComparison.Ordinal);
        string id = cut < 0 ? detail : detail[..cut];
        return ModeName(id) + detail[id.Length..];
    }

    public string EcoOnTitle => W("Eco mode on", "Еко-режим увімкнено");
    public string EcoOnText => W("The PC runs on the battery, eco is on.", "ПК працює від батареї, еко-режим увімкнено.");
    public string ModesLiftedTitle => W("Modes lifted", "Режими знято");
    public string ModesLiftedText => W("Mains is back, eco and the battery levels are lifted.", "Мережа є, еко-режим і рівні батареї знято.");
    public string BrokerStaleTitle => W("Admin broker out of date", "Адмін-брокер застарів");
    public string PowerModeTitle => W("Power mode", "Режим живлення");
    public string ModeDroppedTitle => W("Mode choice dropped", "Вибір режиму скасовано");
    public string EmergencyTitle => W("Battery emergency", "Аварійний заряд батареї");
    public string EmergencyText => W("Emergency mode is on.", "Аварійний режим увімкнено.");
    public string CriticalTitle => W("Battery critical", "Критичний заряд батареї");
    public string HibernateTitle => W("The PC will hibernate", "ПК перейде в сплячий режим");
    public string ActionFailedTitle => W("Action failed", "Дія не виконана");
    public string ModeRefusedTitle => W("Mode refused", "Режим відхилено");
    public string GridHighTitle => W("Grid voltage high", "Висока напруга мережі");
    public string MainsLostTitle => W("Mains lost", "Мережі немає");
    public string MainsLostText => W("The station runs on its battery.", "Станція працює від батареї.");
    public string MainsBackTitle => W("Mains is back", "Мережа є");
    public string MainsBackText => W("The station is on mains again.", "Станція знову від мережі.");
    public string OverTemperatureTitle => W("Station over temperature", "Перегрів станції");
    public string FaultTitle => W("Station fault", "Збій станції");
    public string WarningTitle => W("Station warning", "Попередження станції");
    public string FaultText(string code) => W("Station fault ", "Збій станції ") + code + ".";
    public string WarningText(string code) => W("Station warning ", "Попередження станції ") + code + ".";

    /// <summary>The over-temperature text; <paramref name="celsius"/> empty when the station sent no temperature.</summary>
    public string OverTemperatureText(string celsius) =>
        W("The station is above its temperature limit", "Температура станції вище межі") + (celsius.Length == 0 ? "" : $" ({celsius} °C)") + ".";

    public string TestTitle => W("Test notification", "Тестове сповіщення");
    public string TestText => W("Bluetti Watcher notifications reach this PC.", "Сповіщення Bluetti Watcher доходять до цього ПК.");
}
