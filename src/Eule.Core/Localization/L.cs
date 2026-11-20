using System.Globalization;

namespace Eule.Core.Localization;

/// <summary>
/// Every user-facing string, Turkish and English side by side. The language is chosen at start-up
/// (changing it needs a restart). Tests can switch language for their own flow with <see cref="Use"/>.
/// </summary>
public static class L
{
    public static readonly string[] Languages = ["tr", "en"];

    static string global = "tr";
    static readonly AsyncLocal<string?> local = new();

    public static string Lang
    {
        get => local.Value ?? global;
        set => global = Languages.Contains(value) ? value : "tr";
    }

    public static CultureInfo Culture => new(Lang == "tr" ? "tr-TR" : "en-GB");

    /// <summary>Language for the system's UI culture: Turkish on Turkish Windows, English otherwise.</summary>
    public static string FromSystem() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";

    /// <summary>Switches the language for the current async flow only (tests).</summary>
    public static IDisposable Use(string lang)
    {
        var prev = local.Value;
        local.Value = lang;
        return new Restore(() => local.Value = prev);
    }

    sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    public static string T(string key) =>
        Table.TryGetValue(key, out var v) ? v[Lang == "en" ? 1 : 0] : key;

    public static string F(string key, params object[] args) => string.Format(Culture, T(key), args);

    public static IReadOnlyDictionary<string, string[]> All => Table;

    static readonly Dictionary<string, string[]> Table = new()
    {
        // ---- durations, days
        ["unit.min"] = ["{0} dk", "{0} min"],
        ["unit.hour"] = ["{0} sa", "{0} h"],
        ["unit.hourMin"] = ["{0} sa {1} dk", "{0} h {1} min"],
        ["unit.days"] = ["{0} gün", "{0} days"],
        ["days.short"] = ["Pzt,Sal,Çar,Per,Cum,Cmt,Paz", "Mon,Tue,Wed,Thu,Fri,Sat,Sun"],
        ["days.long"] = ["Pazartesi,Salı,Çarşamba,Perşembe,Cuma,Cumartesi,Pazar", "Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday"],
        ["part.morning"] = ["sabahları", "mornings"],
        ["part.afternoon"] = ["öğleden sonraları", "afternoons"],
        ["part.evening"] = ["akşamları", "evenings"],
        ["part.night"] = ["geceleri", "nights"],

        // ---- state words
        ["caption.working"] = ["molaya", "until break"],
        ["caption.paused"] = ["duraklatıldı", "paused"],
        ["caption.away"] = ["uzaktasın", "away"],
        ["caption.deferred"] = ["meşgulsün", "busy"],
        ["caption.due"] = ["kalkma zamanı", "time to stand"],
        ["caption.break"] = ["mola", "break"],
        ["caption.strict"] = ["zorunlu mola", "enforced break"],
        ["caption.finished"] = ["mola bitti", "break over"],
        ["caption.offhours"] = ["mesai dışı", "off hours"],
        ["time.away"] = ["mola", "break"],
        ["time.deferred"] = ["bekliyor", "waiting"],
        ["time.due"] = ["şimdi", "now"],
        ["time.finished"] = ["hazır", "ready"],
        ["status.working"] = ["Molaya {0}", "Break in {0}"],
        ["status.paused"] = ["Duraklatıldı · {0}", "Paused · {0}"],
        ["status.away"] = ["Uzaktasın, mola sayıldı", "Away, counted as a break"],
        ["status.deferred"] = ["Meşgulsün, mola bekliyor", "You're busy, the break will wait"],
        ["status.due"] = ["Kalkma zamanı", "Time to stand up"],
        ["status.break"] = ["Mola · {0}", "Break · {0}"],
        ["status.finished"] = ["Mola bitti", "Break over"],
        ["status.offhours"] = ["Mesai dışı", "Off hours"],

        // ---- widget
        ["widget.pause"] = ["Duraklat", "Pause"],
        ["widget.resume"] = ["Devam et", "Resume"],
        ["widget.hide"] = ["Gizle (tepsiden geri açılır)", "Hide (reopen from the tray)"],
        ["widget.accept"] = ["Kalktım", "I'm up"],
        ["widget.snooze"] = ["{0} dk sonra", "In {0} min"],
        ["widget.skip"] = ["Atla", "Skip"],
        ["widget.start"] = ["Devam et", "Continue"],
        ["widget.prompt.due"] = ["Biraz kalkıp dolaşmaya ne dersin?", "How about getting up for a little walk?"],
        ["widget.prompt.finished"] = ["Mola bitti. Hazır olduğunda devam edelim.", "Break's over. Let's continue when you're ready."],
        ["widget.prompt.deferred"] = ["Meşgul görünüyorsun, bitince hatırlatacağım.", "You look busy. I'll remind you when you're done."],

        // ---- break screen
        ["break.windowTitle"] = ["Eule · Mola", "Eule · Break"],
        ["break.breatheIn"] =["nefes al", "breathe in"],
        ["break.breatheOut"] = ["ver", "out"],
        ["break.title"] = ["Kalkma zamanı", "Time to stand up"],
        ["break.titleStrict"] = ["Şimdi mola zamanı", "Break time, now"],
        ["break.dimming"] = ["Ekran kararıyor", "The screen is dimming"],
        ["break.dimmingSub"] = ["Kaydını bitir. Birazdan mola başlıyor.", "Save your work. The break starts in a moment."],
        ["break.finished"] = ["Mola bitti", "Break over"],
        ["break.finishedSub"] = ["Hazır olduğunda devam edelim.", "Let's continue when you're ready."],
        ["break.snooze"] = ["{0} dk ertele", "Snooze {0} min"],
        ["break.skip"] = ["Molayı atla", "Skip break"],
        ["break.start"] = ["Çalışmaya başla", "Back to work"],
        ["break.emergency"] = ["Acil bir durum mu var? Shift tuşunu 10 saniye basılı tut.", "An emergency? Hold Shift for 10 seconds."],
        ["eye.text"] = ["Gözlerini dinlendir: uzaktaki bir noktaya bak", "Rest your eyes: look at something far away"],

        ["ex.0.t"] = ["Ayağa kalk", "Stand up"],
        ["ex.0.x"] = ["Önce bir kalk. Omuzlarını gevşet, ağırlığını iki ayağına eşit ver.", "First, stand up. Relax your shoulders and stand evenly on both feet."],
        ["ex.1.t"] = ["Boyun", "Neck"],
        ["ex.1.x"] = ["Başını yavaşça sağa yatır, on saniye bekle. Sonra sola. Zorlamadan.", "Slowly tilt your head to the right and hold for ten seconds. Then left. Gently."],
        ["ex.2.t"] = ["Omuzlar", "Shoulders"],
        ["ex.2.x"] = ["Omuzlarını on kez geriye, on kez öne doğru büyük daireler çizerek çevir.", "Roll your shoulders in big circles, ten times backwards and ten times forwards."],
        ["ex.3.t"] = ["Göğüs", "Chest"],
        ["ex.3.x"] = ["Ellerini arkada birleştir, kollarını hafifçe kaldır, göğsünü aç. On beş saniye.", "Clasp your hands behind you, lift your arms a little and open your chest. Fifteen seconds."],
        ["ex.4.t"] = ["Bel", "Back"],
        ["ex.4.x"] = ["Kollarını yukarı uzat. Önce sağa, sonra sola uzun bir yay gibi eğil.", "Reach your arms up. Bend to the right, then to the left, like a long bow."],
        ["ex.5.t"] = ["Bilekler", "Wrists"],
        ["ex.5.x"] = ["Kolunu öne uzat, parmaklarını diğer elinle kendine doğru çek. İki el de.", "Stretch an arm forward and gently pull the fingers back with the other hand. Both hands."],
        ["ex.6.t"] = ["Bacaklar", "Legs"],
        ["ex.6.x"] = ["Bir ayağını arkaya kıvırıp bileğinden tut, on beş saniye. Sonra diğeri.", "Bend one leg back and hold the ankle for fifteen seconds. Then the other."],
        ["ex.7.t"] = ["Gözler", "Eyes"],
        ["ex.7.x"] = ["Pencereden en uzak noktaya bak. Yirmi saniye sonra gözlerini birkaç kez kırp.", "Look at the farthest point out of the window. After twenty seconds, blink a few times."],
        ["ex.8.t"] = ["Yürü", "Walk"],
        ["ex.8.x"] = ["Birkaç adım at. Bir bardak su doldur ya da pencereye kadar git gel.", "Take a few steps. Fill a glass of water or walk to the window and back."],

        // ---- tray / menu / notifications
        ["menu.today"] = ["Bugün {0} · {1}/{2} mola", "Today {0} · {1}/{2} breaks"],
        ["menu.streak"] = [" · {0} gün seri", " · {0}-day streak"],
        ["menu.breakNow"] = ["Şimdi mola ver", "Take a break now"],
        ["menu.pause"] = ["Duraklat", "Pause"],
        ["menu.resume"] = ["Devam et", "Resume"],
        ["menu.reset"] = ["Sayacı sıfırla", "Reset timer"],
        ["menu.widget"] = ["Köşede göster", "Show in corner"],
        ["menu.stats"] = ["İstatistik", "Statistics"],
        ["menu.settings"] = ["Ayarlar", "Settings"],
        ["menu.exit"] = ["Çıkış", "Quit"],
        ["note.hereTitle"] = ["Eule burada", "Eule is here"],
        ["note.hereText"] = ["Simgeye tıklayarak Eule'yi köşede gösterebilirsin.", "Click the icon to show Eule in the corner."],
        ["note.warnTitle"] = ["Mola yaklaşıyor", "Break coming up"],
        ["note.warnText"] = ["{0} dakika sonra kalkma zamanı.", "Time to stand up in {0} min."],
        ["note.goalTitle"] = ["Bugünkü hedef tamam", "Today's goal reached"],
        ["note.goalText"] = ["Eule seninle gurur duyuyor.", "Eule is proud of you."],
        ["note.goalStreak"] = ["{0} gündür hedeftesin. Eule seninle gurur duyuyor.", "{0} days in a row. Eule is proud of you."],
        ["app.running"] = ["Eule zaten çalışıyor.", "Eule is already running."],
        ["app.error"] = ["Beklenmeyen bir hata oluştu, Eule çalışmaya devam ediyor.\n\nAyrıntılar günlük dosyasına yazıldı:\n{0}",
                         "Something unexpected happened. Eule keeps running.\n\nDetails were written to the log:\n{0}"],

        // ---- settings
        ["set.title"] = ["Ayarlar", "Settings"],
        ["set.stats"] = ["İstatistik", "Statistics"],
        ["set.today"] = ["{0} · bugün {1} oturdun, {2}/{3} mola", "{0} · today {1} sitting, {2}/{3} breaks"],
        ["set.rhythm"] = ["Ritim", "Rhythm"],
        ["set.preset.30"] = ["30 dk · 5 dk mola", "30 min · 5 min break"],
        ["set.preset.60"] = ["Saatte bir · 10 dk", "Hourly · 10 min"],
        ["set.preset.25"] = ["Pomodoro 25 · 5", "Pomodoro 25 · 5"],
        ["set.work"] = ["Oturma süresi", "Sitting time"],
        ["set.workDesc"] = ["Kaç dakikada bir kalkmak istersin?", "How many minutes between breaks?"],
        ["set.break"] = ["Mola süresi", "Break length"],
        ["set.minutes"] = ["Dakika", "Minutes"],
        ["set.goal"] = ["Günlük mola hedefi", "Daily break goal"],
        ["set.goalDesc"] = ["Hedefe ulaştığın günler seriye eklenir. Bilgisayar başında olmadığın günler seriyi bozmaz.",
                            "Days you reach it build your streak. Days away from the computer don't break it."],
        ["set.level"] = ["Hatırlatma biçimi", "How Eule reminds you"],
        ["set.gentle"] = ["Nazik", "Gentle"],
        ["set.gentleDesc"] = ["Eule sadece köşeden seslenir, cevap verene kadar arada bir hatırlatır.",
                              "Eule only calls from the corner and reminds you now and then until you answer."],
        ["set.balanced"] = ["Dengeli", "Balanced"],
        ["set.balancedDesc"] = ["Önce köşeden sorar; bir dakika içinde cevap gelmezse ya da erteleme hakkın biterse mola ekranı açılır.",
                                "Asks from the corner first; the break screen opens if you don't answer within a minute or run out of snoozes."],
        ["set.firm"] = ["Kararlı", "Firm"],
        ["set.firmDesc"] = ["Mola ekranı hemen açılır. Erteleyebilir ya da atlayabilirsin.", "The break screen opens right away. You can snooze or skip it."],
        ["set.strict"] = ["Zorunlu", "Enforced"],
        ["set.strictDesc"] = ["Ekran kararır ve mola bitene kadar açılmaz. Erteleme ya da atlama yok.",
                              "The screen goes dark until the break is over. No snoozing, no skipping."],
        ["set.emergency"] = ["Acil durum çıkışı", "Emergency exit"],
        ["set.emergencyDesc"] = ["Zorunlu molada Shift'i 10 saniye basılı tutarak çıkabilirsin. İstatistiğe 'bozuldu' olarak yazılır.",
                                 "Hold Shift for 10 seconds to leave an enforced break. It is recorded as broken."],
        ["set.snooze"] = ["Erteleme", "Snooze"],
        ["set.snoozeDesc"] = ["Kaç dakika · en fazla kaç kez", "Minutes · how many times at most"],
        ["set.snoozeTip"] = ["Erteleme süresi (dk)", "Snooze length (min)"],
        ["set.maxSnoozeTip"] = ["Arka arkaya en fazla erteleme", "Most snoozes in a row"],
        ["set.warn"] = ["Önceden haber ver", "Heads-up"],
        ["set.warnDesc"] = ["Saniye. Eule molaya bu kadar kala uyanıp sana bakar. 0 = kapalı.",
                            "Seconds. Eule wakes up and looks at you this long before the break. 0 = off."],
        ["set.busy"] = ["Meşgulken bekle", "Wait while I'm busy"],
        ["set.busyDesc"] = ["Sunum, görüşme ya da tam ekran uygulama sırasında mola, bitene kadar bekler.",
                            "During a presentation, a call or a full-screen app, the break waits until you're done."],
        ["set.behaviour"] = ["Davranış", "Behaviour"],
        ["set.away"] = ["Masadan kalkmak mola sayılsın", "Leaving the desk counts as a break"],
        ["set.awayDesc"] = ["Mola süresi kadar bilgisayara dokunmazsan sayaç baştan başlar.",
                            "If you don't touch the computer for a break's length, the timer starts over."],
        ["set.autoRestart"] = ["Mola bitince kendiliğinden devam et", "Continue on its own after a break"],
        ["set.autoRestartDesc"] = ["Kapalıysa sayaç sen 'Devam et' diyene kadar bekler.", "When off, the timer waits until you press Continue."],
        ["set.eye"] = ["Göz molası (20-20-20)", "Eye break (20-20-20)"],
        ["set.eyeDesc"] = ["20 dakikada bir, ekranın üstünde 20 saniyelik küçük bir hatırlatma. Tıklamaları engellemez.",
                           "Every 20 minutes, a small 20-second reminder at the top of the screen. Clicks pass through."],
        ["set.cover"] = ["Molada tüm ekranları kapla", "Cover all screens during breaks"],
        ["set.coverDesc"] = ["Birden fazla monitörün varsa. Zorunlu molada her zaman açıktır.", "If you have more than one monitor. Always on for enforced breaks."],
        ["set.sound"] = ["Ses", "Sound"],
        ["set.widget"] = ["Eule köşede dursun", "Keep Eule in the corner"],
        ["set.widgetDesc"] = ["Kapalıyken yalnızca görev çubuğu simgesi kalır.", "When off, only the taskbar icon remains."],
        ["set.startup"] = ["Windows açılınca başlat", "Start with Windows"],
        ["set.hours"] = ["Mesai saatleri", "Work hours"],
        ["set.hoursOnly"] = ["Sadece mesai saatlerinde hatırlat", "Only remind me during work hours"],
        ["set.hoursDesc"] = ["Dışında Eule sessizce uyur.", "Outside them Eule quietly sleeps."],
        ["set.look"] = ["Görünüm", "Appearance"],
        ["set.theme.system"] = ["Sistem", "System"],
        ["set.theme.light"] = ["Açık", "Light"],
        ["set.theme.dark"] = ["Koyu", "Dark"],
        ["set.language"] = ["Dil", "Language"],
        ["set.languageRestart"] = ["Dil, Eule yeniden başlayınca değişir.", "The language changes when Eule restarts."],
        ["set.restart"] = ["Yeniden başlat", "Restart"],
        ["set.about"] = ["Hakkında", "About"],
        ["set.version"] = ["Sürüm {0}", "Version {0}"],
        ["set.privacy"] = ["Eule ücretsizdir. Notların hesabına, eule.adacreative.net sunucusuna kaydedilir. Mola istatistiklerin ve ses kaydın bu bilgisayardan çıkmaz.",
                           "Eule is free. Your notes are saved to your account on eule.adacreative.net. Your break statistics and your voice never leave this computer."],
        ["set.account"] = ["Hesap", "Account"],
        ["set.signedInAs"] = ["{0} olarak giriş yapıldı", "Signed in as {0}"],
        ["set.signedOut"] = ["Giriş yapılmadı", "Not signed in"],
        ["set.signedOutDesc"] = ["Not almak için hesabınla giriş yap. Molalar girişsiz de çalışır.", "Sign in to take notes. Breaks work without signing in."],
        ["set.pending"] = ["{0} not gönderilmeyi bekliyor", "{0} notes waiting to be sent"],
        ["set.signIn"] = ["Giriş yap", "Sign in"],
        ["set.signOut"] = ["Çıkış yap", "Sign out"],

        // ---- sign-in
        ["login.title"] = ["Eule hesabına giriş", "Sign in to Eule"],
        ["login.text"] = ["Notların ve hatırlatmaların hesabına kaydedilir. Ses kaydın bilgisayarından çıkmaz.",
                          "Your notes and reminders are saved to your account. Your voice never leaves this computer."],
        ["login.email"] = ["E-posta", "Email"],
        ["login.password"] = ["Şifre", "Password"],
        ["login.submit"] = ["Giriş yap", "Sign in"],
        ["login.later"] = ["Şimdi değil", "Not now"],
        ["login.noAccount"] = ["Hesabın yok mu? Şimdilik hesapları yönetici açıyor.", "No account? For now, accounts are created by the administrator."],
        ["login.missing"] = ["E-posta ve şifreni yaz.", "Enter your email and password."],
        ["login.errInvalid"] = ["E-posta ya da şifre hatalı.", "Wrong email or password."],
        ["login.errNetwork"] = ["Sunucuya ulaşılamadı. İnternet bağlantını kontrol et.", "Couldn't reach the server. Check your internet connection."],
        ["login.errRate"] = ["Çok fazla deneme oldu. Bir dakika sonra tekrar dene.", "Too many attempts. Try again in a minute."],
        ["login.errServer"] = ["Sunucuda bir sorun var. Biraz sonra tekrar dene.", "The server has a problem. Try again a little later."],
        ["login.welcome"] = ["Hoş geldin, {0}!", "Welcome, {0}!"],

        // ---- note bubble
        ["note.placeholder"] = ["Ne hatırlatayım? Örn. Cumartesi saat 4'te toplantı", "What should I remember? e.g. Cumartesi saat 4'te toplantı"],
        ["note.send"] = ["Kaydet", "Save"],
        ["note.sending"] = ["Kaydediyorum…", "Saving…"],
        ["note.undo"] = ["Geri al", "Undo"],
        ["note.undone"] = ["Geri aldım, not silindi.", "Undone, the note was deleted."],
        ["note.queued"] = ["İnternet yok gibi. Notunu sakladım, bağlantı gelince kendim göndereceğim.", "Looks like there's no internet. I kept your note and will send it when you're back online."],
        ["note.needsSignIn"] = ["Önce giriş yapmalısın. Notunu sakladım, girişten sonra göndereceğim.", "Sign in first. I kept your note and will send it after you sign in."],
        ["note.rejected"] = ["Bu notu kaydedemedim.", "I couldn't save this note."],
        ["note.deliveredTitle"] = ["Bekleyen notun kaydedildi", "Your waiting note was saved"],
        ["note.signedOutTitle"] = ["Oturumun sona erdi", "Your session ended"],
        ["note.signedOutText"] = ["Not almaya devam etmek için yeniden giriş yap.", "Sign in again to keep taking notes."],
        ["menu.note"] = ["Not yaz", "Write a note"],
        ["menu.signIn"] = ["Giriş yap", "Sign in"],
        ["set.folder"] = ["Veri klasörü", "Data folder"],
        ["set.checkUpdate"] = ["Güncellemeleri denetle", "Check for updates"],
        ["set.updateNone"] = ["Eule güncel.", "Eule is up to date."],
        ["set.updateFound"] = ["Yeni sürüm {0} hazır.", "Version {0} is ready."],
        ["set.updateInstall"] = ["Güncelle ve yeniden başlat", "Update and restart"],
        ["set.updateOff"] = ["Bu kopya otomatik güncellenmiyor (kurulum yapılmadan çalışıyor).", "This copy doesn't update itself (it runs without being installed)."],
        ["set.updateError"] = ["Güncelleme denetlenemedi. İnternet bağlantını kontrol et.", "Couldn't check for updates. Check your internet connection."],
        ["set.updateChecking"] = ["Denetleniyor…", "Checking…"],
        ["set.updateNoFeed"] = ["Otomatik güncelleme için yayın kaynağı henüz ayarlanmadı.", "No release feed is configured for automatic updates yet."],
        ["note.updateTitle"] = ["Eule'nin yeni sürümü hazır", "A new version of Eule is ready"],
        ["note.updateText"] = ["Eule kapanınca sessizce kurulacak.", "It will be installed quietly when Eule closes."],

        // ---- statistics
        ["stats.title"] = ["İstatistik", "Statistics"],
        ["stats.weekSit"] = ["bu hafta oturma", "sitting this week"],
        ["stats.weekBreaks"] = ["bu hafta mola", "breaks this week"],
        ["stats.streak"] = ["seri", "streak"],
        ["stats.lastWeek"] = ["geçen hafta {0}", "last week {0}"],
        ["stats.lastWeekNone"] = ["geçen hafta veri yok", "no data last week"],
        ["stats.todayGoal"] = ["bugün {0} / {1}", "today {0} / {1}"],
        ["stats.goalMet"] = ["bugünkü hedef tamam", "today's goal reached"],
        ["stats.goal"] = ["hedef: günde {0} mola", "goal: {0} breaks a day"],
        ["stats.recent"] = ["Son 14 gün", "Last 14 days"],
        ["stats.recentSub"] = ["Günlük oturma süresi", "Sitting time per day"],
        ["stats.today"] = ["bugün", "today"],
        ["stats.met"] = ["hedef tuttu", "goal met"],
        ["stats.missed"] = ["tutmadı", "missed"],
        ["stats.barTip"] = ["{0} · {1} oturma · {2} mola", "{0} · {1} sitting · {2} breaks"],
        ["stats.barLongest"] = [" · en uzun {0}", " · longest {0}"],
        ["stats.barMet"] = [" · hedef tuttu", " · goal met"],
        ["stats.barMissed"] = [" · hedef tutmadı", " · goal missed"],
        ["stats.rhythm"] = ["Haftanın ritmi", "Weekly rhythm"],
        ["stats.rhythmSub"] = ["Son 4 haftada, saat başına ortalama oturma", "Average sitting per hour over the last 4 weeks"],
        ["stats.cellTip"] = ["{0} {1:00}:00–{2:00}:00 · ortalama {3} dk", "{0} {1:00}:00–{2:00}:00 · average {3} min"],
        ["stats.less"] = ["az", "less"],
        ["stats.more"] = ["çok ({0} dk)", "more ({0} min)"],
        ["stats.insights"] = ["Eule ne fark etti?", "What Eule noticed"],
        ["stats.insightsEmpty"] = ["Birkaç gün kullandıktan sonra burada sana özel gözlemler göreceksin.", "After a few days you'll see observations about your own habits here."],
        ["stats.export"] = ["CSV olarak dışa aktar", "Export as CSV"],
        ["stats.exportDesc"] = ["Tüm günlerin tablosu; Excel'de doğrudan açılır.", "A table of every day; opens directly in Excel."],
        ["stats.exported"] = ["Kaydedildi: {0}", "Saved: {0}"],
        ["stats.exportFailed"] = ["Dosya yazılamadı. Başka bir yer dene.", "Couldn't write the file. Try another location."],
        ["stats.csvFilter"] = ["CSV dosyası (*.csv)|*.csv", "CSV file (*.csv)|*.csv"],
        ["stats.csvName"] = ["eule-istatistik-{0:yyyy-MM-dd}.csv", "eule-statistics-{0:yyyy-MM-dd}.csv"],
        ["csv.header"] = ["Tarih;Oturma (dk);Mola;Uzaklaşma molası;Atlanan;Ertelenen;Acil çıkış;Göz molası;En uzun kesintisiz (dk)",
                          "Date;Sitting (min);Breaks;Away breaks;Skipped;Snoozed;Emergency exits;Eye breaks;Longest unbroken (min)"],

        ["insight.peak"] = ["En çok {0} {1} oturuyorsun.", "You sit the most on {0} {1}."],
        ["insight.longest"] = ["Bu haftaki en uzun kesintisiz oturuşun {0} ({1}).", "Your longest unbroken sitting this week: {0} ({1})."],
        ["insight.less"] = ["Geçen haftaya göre günde ortalama {0} daha az oturdun.", "On average you sat {0} less per day than last week."],
        ["insight.more"] = ["Geçen haftaya göre günde ortalama {0} daha fazla oturdun.", "On average you sat {0} more per day than last week."],
        ["insight.same"] = ["Oturma süren geçen haftayla hemen hemen aynı.", "Your sitting time is about the same as last week."],
        ["insight.rate"] = ["Bu hafta mola verme oranın %{0} ({1} / {2}).", "This week you took {0}% of your breaks ({1} / {2})."],

        // ---- welcome
        ["welcome.title"] = ["Merhaba, ben Eule", "Hi, I'm Eule"],
        ["welcome.text"] = ["Sen çalışırken köşede seninle otururum. Zamanı gelince kalkıp biraz dolaşmanı hatırlatırım.",
                            "I sit with you in the corner while you work, and remind you to get up and move when it's time."],
        ["welcome.rhythmTitle"] = ["Ne sıklıkla kalkalım?", "How often should we get up?"],
        ["welcome.rhythmText"] = ["Sonra Ayarlar'dan değiştirebilirsin.", "You can change this later in Settings."],
        ["welcome.levelTitle"] = ["Nasıl hatırlatayım?", "How should I remind you?"],
        ["welcome.startup"] = ["Windows açılınca beni de başlat", "Start me with Windows"],
        ["welcome.next"] = ["Devam", "Next"],
        ["welcome.back"] = ["Geri", "Back"],
        ["welcome.done"] = ["Başlayalım", "Let's go"],
        ["welcome.privacy"] = ["Ücretsiz. Notların hesabında, ses kaydın bu bilgisayarda kalır.", "Free. Your notes live in your account; your voice stays on this computer."],
    };
}
