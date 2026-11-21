using System.Text.RegularExpressions;

namespace BluettiWatcher.Tests;

/// <summary>
/// A text pass never takes visual features away: the page keeps every element id, section heading, stat tile, card and chart
/// of the 1.10.4 page, in the same order.
/// </summary>
public sealed partial class PageI18nTests
{
    // the 1.10.4 page's markup inside <main>, in document order (the modes panel as redesigned in 1.10.6)
    private const string Ids1104 = "title,readAt,gridChip,ecoChip,planChip,staleChip,maxChip,noteChip,modeSeg,planLabel,planSeg,modeLine,langSeg,ring,"
        + "zoneR,zoneA,arc,ticks,pct,wh,trend,load,spark,loadSub,left,leftSub,cutK,cut,cutSub,cutSub2,reach,volt,voltSub,today,todaySub,todaySub2,"
        + "dtekCard,dtekChip,dtekPill,dtekGrid,dtekMore,dtekBody,dtekNote,rangeSeg,chart,count,battCard,battTitle,battKpis,hours,cycles,loadStats,cuts,setCard,setMsg,modesHib,"
        + "srcCard,cloudChip,srcSeg,srcResetBox,srcReset,cloudSignIn,cloudSignOut,cloudLine,cloudLink,rows,stationCard,stationBody,modesCard,modesMore,"
        + "modesStatus,modesMsg,modesList,modesEdit,modesRules";   // 1.10.6: no switch of its own in the modes panel; its settings row became the Settings card above the reading source
    private const string Headings1104 = "dtekTitle,dtekDetails,batteryAndLoad,energyPerHour,cyclesTitle,loadRangeTitle,powerCuts,setTitle,srcTitle,lastReadings,stationOwn,modesTitle";
    private const string Tiles1104 = "loadNow,timeLeft,thisCut,reaches,output,today";

    [GeneratedRegex(@" id=""([^""]+)""")]
    private static partial Regex MarkupId();

    [GeneratedRegex(@"<(?:h2|summary)[^>]*data-i18n=""([^""]+)""")]
    private static partial Regex HeadingKey();

    [GeneratedRegex(@"class=""k""[^>]*data-i18n=""([^""]+)""")]
    private static partial Regex TileKey();

    [GeneratedRegex(@"class=""card")]
    private static partial Regex CardClass();

    private static string MainMarkup()
    {
        string page = Page();
        int a = page.IndexOf("<main>", StringComparison.Ordinal), b = page.IndexOf("</main>", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "no <main> in the page");
        return page[a..b];
    }

    [Fact]
    public void Page_keeps_every_element_id_of_1_10_4_in_order()
    {
        Assert.Equal(Ids1104, string.Join(',', MarkupId().Matches(MainMarkup()).Select(m => m.Groups[1].Value)));
    }

    [Fact]
    public void Page_keeps_every_section_heading_and_stat_tile_of_1_10_4_in_order()
    {
        string main = MainMarkup();
        Assert.Equal(Headings1104, string.Join(',', HeadingKey().Matches(main).Select(m => m.Groups[1].Value)));
        Assert.Equal(Tiles1104, string.Join(',', TileKey().Matches(main).Select(m => m.Groups[1].Value)));
    }

    [Fact]
    public void Page_keeps_the_cards_and_charts_of_1_10_4()
    {
        string main = MainMarkup();
        Assert.Equal(20, CardClass().Count(main));   // 1.10.6: the Settings card
        Assert.Equal(5, main.Split("<svg", StringSplitOptions.None).Length - 1);   // brand, ring, sparkline, battery and load chart, energy per hour
    }
}
