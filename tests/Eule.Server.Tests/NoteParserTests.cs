using Eule.Parsing;

namespace Eule.Server.Tests;

public class NoteParserTests
{
    // Wednesday 18 November 2026, 10:00 local time.
    static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0);

    [Theory]
    [InlineData("Cumartesi saat 4'te toplantı var, unutma", "2026-11-21 16:00", "Toplantı")]
    [InlineData("yarın akşam 7 buçukta annemi ara", "2026-11-19 19:30", "Annemi ara")]
    [InlineData("haftaya salı 14:30 dişçi", "2026-11-24 14:30", "Dişçi")]
    [InlineData("saat 9'da rapor gönder", "2026-11-19 09:00", "Rapor gönder")]       // 09:00 has passed today
    [InlineData("sabah 8de koşu", "2026-11-19 08:00", "Koşu")]
    [InlineData("bugün saat dörtte market", "2026-11-18 16:00", "Market")]
    [InlineData("cuma günü saat 5'te sunum", "2026-11-20 17:00", "Sunum")]
    [InlineData("pazarlama toplantısı yarın 10:00", "2026-11-19 10:00", "Pazarlama toplantısı")]
    [InlineData("gece 11de sunucuyu yeniden başlat", "2026-11-18 23:00", "Sunucuyu yeniden başlat")]
    [InlineData("öbür gün 3 buçukta berber", "2026-11-20 15:30", "Berber")]
    [InlineData("Saat 16.45'te otobüs", "2026-11-18 16:45", "Otobüs")]
    [InlineData("bu akşam annemi ara", "2026-11-18 19:00", "Annemi ara")]
    [InlineData("yarın sabah koşu", "2026-11-19 09:00", "Koşu")]
    [InlineData("çarşamba 3te ekip toplantısı", "2026-11-18 15:00", "Ekip toplantısı")] // today is Wednesday
    [InlineData("akşam saat sekizde maç var", "2026-11-18 20:00", "Maç")]
    [InlineData("sabah dokuz buçukta doktor randevusu", "2026-11-19 09:30", "Doktor randevusu")]
    [InlineData("Pazartesi 12'de öğle yemeği Mehmet ile", "2026-11-23 12:00", "Öğle yemeği Mehmet ile")]
    public void Understands_timed_notes(string text, string expected, string title)
    {
        var r = NoteParser.Parse(text, Now);
        Assert.Equal(DateTime.Parse(expected), r.DueLocal);
        Assert.True(r.HasTime);
        Assert.Equal(title, r.Title);
    }

    [Theory]
    [InlineData("yarım saat sonra çayı kapat", "2026-11-18 10:30", "Çayı kapat")]
    [InlineData("10 dakika sonra toplantıya katıl", "2026-11-18 10:10", "Toplantıya katıl")]
    [InlineData("iki saat sonra ilacını iç", "2026-11-18 12:00", "İlacını iç")]
    [InlineData("3 gün sonra kargoyu kontrol et", "2026-11-21 10:00", "Kargoyu kontrol et")]
    public void Understands_relative_times(string text, string expected, string title)
    {
        var r = NoteParser.Parse(text, Now);
        Assert.Equal(DateTime.Parse(expected), r.DueLocal);
        Assert.Equal(title, r.Title);
    }

    [Theory]
    [InlineData("25 Kasım'da fatura son gün", "2026-11-25", "Fatura son gün")]
    [InlineData("2 Ocak'ta vergi beyannamesi", "2027-01-02", "Vergi beyannamesi")]   // next year
    [InlineData("ayın 25'inde kira", "2026-11-25", "Kira")]
    [InlineData("Cumartesi toplantı", "2026-11-21", "Toplantı")]
    public void Day_without_time_is_a_morning_reminder(string text, string expectedDay, string title)
    {
        var r = NoteParser.Parse(text, Now);
        Assert.Equal(DateTime.Parse(expectedDay).AddHours(NoteParser.DefaultHourForDayOnly), r.DueLocal);
        Assert.False(r.HasTime);
        Assert.Equal(title, r.Title);
    }

    [Theory]
    [InlineData("Ahmet'i ara", "Ahmet'i ara")]
    [InlineData("akşam yemeği için alışveriş yap", "Akşam yemeği için alışveriş yap")]
    [InlineData("süt, ekmek ve yumurta al", "Süt, ekmek ve yumurta al")]
    [InlineData("pazarlama sunumunu bitir", "Pazarlama sunumunu bitir")]
    public void Plain_notes_have_no_time(string text, string title)
    {
        var r = NoteParser.Parse(text, Now);
        Assert.Null(r.DueLocal);
        Assert.Equal(title, r.Title);
    }

    [Fact]
    public void Title_falls_back_to_the_whole_sentence()
    {
        var r = NoteParser.Parse("yarın saat 5'te, unutma", Now);
        Assert.Equal(new DateTime(2026, 11, 19, 17, 0, 0), r.DueLocal);
        Assert.Equal("Yarın saat 5'te, unutma", r.Title);
    }

    [Fact]
    public void Describes_the_time_back()
    {
        Assert.Equal("Cumartesi 21 Kasım, 16:00", NoteParser.Describe(new DateTime(2026, 11, 21, 16, 0, 0), true, Now));
        Assert.Equal("Yarın, 09:30", NoteParser.Describe(new DateTime(2026, 11, 19, 9, 30, 0), true, Now));
        Assert.Equal("Bugün, 16:00", NoteParser.Describe(new DateTime(2026, 11, 18, 16, 0, 0), true, Now));
        Assert.Equal("Cumartesi 2 Ocak 2027", NoteParser.Describe(new DateTime(2027, 1, 2, 9, 0, 0), false, Now));
    }
}
