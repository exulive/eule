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
- [ ] **Aşama 4** – İstatistik ekranı, ısı haritası, haftalık içgörü, CSV
- [ ] **Aşama 5** – İlk açılış rehberi, TR/EN, simge, Velopack kurulum + otomatik güncelleme
