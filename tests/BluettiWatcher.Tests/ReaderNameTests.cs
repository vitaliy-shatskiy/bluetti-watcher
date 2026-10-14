namespace BluettiWatcher.Tests;

/// <summary>The accepted set of powerMode.extraReaders: ASCII letters, digits, _ . - and space, 1 to 64 characters, nothing else.</summary>
public class ReaderNameTests
{
    private static string? Problem(string name)
    {
        var cfg = new StationConfig();
        cfg.PowerMode.ExtraReaders = [name];
        return cfg.ExtraReadersProblem;
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Z")]
    [InlineData("0")]
    [InlineData("_")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData(" ")]
    [InlineData("My Reader-2.x_y")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz01234567_. -")]   // 64 characters, every kind
    public void Accepted(string name) => Assert.Null(Problem(name));

    [Fact]
    public void Sixty_four_characters_pass_and_sixty_five_do_not()
    {
        Assert.Null(Problem(new string('a', 64)));
        Assert.NotNull(Problem(new string('a', 65)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("name\n")]
    [InlineData("\nname")]
    [InlineData("a\tb")]
    [InlineData("a,b")]
    [InlineData("a;b")]
    [InlineData("a/b")]
    [InlineData("DOMAIN\\user")]
    [InlineData("a\"b")]
    [InlineData("a@b")]
    [InlineData("é")]            // e acute: a letter, but not ASCII
    [InlineData("Ж")]            // Cyrillic Zhe
    [InlineData("１")]            // full-width digit one
    [InlineData("٠")]            // Arabic-Indic digit zero
    [InlineData("a\0b")]
    public void Refused(string name) => Assert.NotNull(Problem(name));

    [Fact]
    public void A_null_name_is_refused()
    {
        var cfg = new StationConfig();
        cfg.PowerMode.ExtraReaders = [null!];
        Assert.NotNull(cfg.ExtraReadersProblem);
    }
}
