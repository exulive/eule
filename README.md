# Eule

Oturma süreni takip eden, mola hatırlatan ücretsiz ve minimal bir Windows uygulaması.

## Geliştirme

```powershell
dotnet build Eule.sln
dotnet test
dotnet run --project src/Eule.App
```

| Proje | İçerik |
|---|---|
| `src/Eule.Core` | Zamanlayıcı/mola motoru (`Engine`), ayarlar (`Settings`), SQLite istatistik ve eski veri aktarımı (`Storage`). Arayüzden bağımsız, test edilebilir. |
| `src/Eule.App` | WPF uygulaması: motoru saniyede bir besleyen `AppHost`, günlük kaydı, hata yakalama. |
| `tests/Eule.Tests` | xUnit testleri. |

Geliştirici bayrakları: `--settings`, `--stats`, `--break`, `--eye`.
`EULE_DATA_DIR` ortam değişkeni ayrı bir veri klasörü kullandırır; yalnızca onunla birlikte `--seed-demo` dört haftalık örnek veri yükler.

```powershell
$env:EULE_DATA_DIR = "$env:TEMP\eule-demo"; dotnet run --project src/Eule.App -- --seed-demo --stats
```

Veriler `%APPDATA%\Eule` altında: `settings.json`, `eule.db`, `logs\`.
İlk açılışta "Oturma Sayacı" prototipinin ayarları ve istatistikleri bir kez aktarılır.

## Hatırlatma seviyeleri

| Seviye | Davranış |
|---|---|
| Nazik | Yalnızca bildirim; cevaplanana kadar birkaç dakikada bir tekrarlar. |
| Dengeli | Önce bildirim; kısa süre sonra ya da erteleme hakkı bitince mola ekranı açılır. |
| Kararlı | Mola ekranı hemen açılır; ertelenebilir veya atlanabilir. |
| Zorunlu | Ekran kararır, erteleme/atlama yok. Yalnızca isteğe bağlı acil çıkış (Shift 10 sn). |

Sunum, görüşme veya tam ekran uygulama sırasında mola ertelenir (ayardan kapatılabilir).

## Yol haritası

- [x] **Aşama 0** – .NET 8 çözümü, Git
- [x] **Aşama 1** – Motor + testler, JSON ayarlar, SQLite olay kaydı, Serilog, eski veri aktarımı
- [x] **Aşama 2** – Tasarım sistemi, baykuş karakteri, köşe sayacı, tepsi, ayarlar penceresi, açık/koyu tema, temel mola ekranı
- [x] **Aşama 3** – Akıllı kesinti önleme (sunum, görüşme, tam ekran), köşe sayacının soru olarak çıkması, nefes ve hareket molası, tüm ekranlar, zorunlu mola (yavaş kararma, kısayol engeli, acil çıkış), 20-20-20, mesai saatleri, günlük hedef + seri
- [x] **Aşama 4** – İstatistik ekranı (son 14 gün, haftanın ritmi ısı haritası, en uzun kesintisiz oturuş), çıkarımlar, CSV
- [x] **Aşama 5** – İlk açılış rehberi, Türkçe/İngilizce, baykuş simgesi, Hakkında + gizlilik notu, Velopack kurulum + otomatik güncelleme altyapısı

## Kurulum paketi

```powershell
dotnet tool install -g vpk --version 1.2.161   # Velopack paketiyle aynı sürüm
.\pack.ps1 -Version 0.1.0
```

`releases\Eule-win-Setup.exe` tek tıkla kurar (yönetici izni gerekmez, `%LOCALAPPDATA%\Eule`).
.NET 8 masaüstü çalışma zamanı yoksa kurulum onu da indirir. Kod imzası olmadığı için Windows
SmartScreen ilk açılışta "Ek bilgi → Yine de çalıştır" ister.

**Otomatik güncelleme** bu deponun GitHub Releases sayfasından çalışır
(`src/Eule.App/UpdateService.cs` → `RepoUrl`). Her yeni sürümden sonra:

```powershell
.\pack.ps1 -Version 0.2.0
vpk upload github --repoUrl https://github.com/exulive/eule --publish --releaseName "Eule 0.2.0" --tag v0.2.0 --token (gh auth token)
```
Kurulu kopyalar günde bir kez denetler, yeni sürümü indirir ve Eule kapanınca sessizce kurar.

## Sunucu (eule.adacreative.net)

Not ve dikte API'si: `src/Eule.Server` (ASP.NET Core 8 + PostgreSQL), Türkçe tarih/saat anlama: `src/Eule.Parsing`,
testleri: `tests/Eule.Server.Tests`. Coolify'a kurulum adım adım: **[docs/SUNUCU-KURULUM.md](docs/SUNUCU-KURULUM.md)**.

Yerelde denemek (SQLite ile, ayar gerekmez):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --project src/Eule.Server
```

## Dil

Bütün metinler `src/Eule.Core/Localization/L.cs` içinde, Türkçe ve İngilizce yan yana.
XAML'de `{l:T anahtar}`, kodda `L.T("anahtar")` / `L.F("anahtar", ...)`. İlk açılışta Windows diline göre seçilir.
