# Eule sunucusunu Coolify'a kurma rehberi

Bu rehber Eule API'sini **https://eule.adacreative.net** adresinde, Coolify üzerinde çalıştırmak içindir.
Toplam süre yaklaşık 20 dakika. Sırasıyla:

1. Alan adını sunucuya yönlendirmek (DNS)
2. Veritabanını kurmak (PostgreSQL)
3. API uygulamasını kurmak
4. İlk hesabı açmak
5. Denemek

> **Ne çalışacak?** Masaüstündeki Eule, söylediğiniz cümleyi bilgisayarda yazıya çevirip bu sunucuya gönderir.
> Sunucu cümleyi anlar ("Cumartesi saat 4'te toplantı var" → Cumartesi 16:00, *Toplantı*), notu hesabınıza kaydeder
> ve baykuşun göstereceği onay cümlesini geri yollar. Ses kaydı sunucuya hiç gelmez, yalnızca metin gelir.

---

## 1. DNS: `eule.adacreative.net` → Coolify sunucusu

`adacreative.net` alan adını yönettiğiniz panelde (Cloudflare, alan adı firması vb.) yeni bir kayıt ekleyin:

| Tür | Ad | Değer | TTL |
|---|---|---|---|
| `A` | `eule` | Coolify sunucunuzun IP adresi | Otomatik / 300 |

- Zaten `*.adacreative.net` için bir **joker (wildcard)** kaydınız varsa bu adımı atlayabilirsiniz.
- **Cloudflare kullanıyorsanız:** İlk kurulumda kaydı **gri bulut (DNS only)** yapın. Coolify'ın Let's Encrypt
  sertifikası alabilmesi için. Sertifika alındıktan sonra isterseniz turuncu buluta çevirebilirsiniz
  (SSL/TLS modu **Full (strict)** olmalı).

Kontrol (birkaç dakika sürebilir):

```powershell
Resolve-DnsName eule.adacreative.net
```

Sunucunuzun IP adresini görüyorsanız tamamdır.

---

## 2. Veritabanı: PostgreSQL

Coolify panelinde:

1. **Projects → + Add** ile `Eule` adında bir proje açın (varsa onu kullanın), **production** ortamına girin.
2. **+ New → Database → PostgreSQL** seçin. Sürüm: **16**. Sunucu: API ile **aynı sunucu**.
3. Ad: `eule-db`. **Start**'a basın.
4. Veritabanı sayfasında:
   - **Make it publicly available:** **kapalı** kalsın. Veritabanı internete açılmamalı, API'ye iç ağdan ulaşılır.
   - Şu değerleri bir kenara not edin: **Username**, **Password**, **Initial Database**,
     ve **Postgres URL (internal)** içindeki sunucu adı (`postgres://kullanici:sifre@`**`BURASI`**`:5432/veritabani`).
5. **Backups** sekmesi → **+ Add** → her gün gece 03:00 (`0 3 * * *`), 14 yedek sakla. *(Önerilir.)*

API'nin istediği bağlantı cümlesi şu biçimde. Değerleri 4. adımda not ettiklerinizle doldurun:

```
Host=SUNUCU_ADI;Port=5432;Database=VERITABANI;Username=KULLANICI;Password=SIFRE
```

Örnek: `Host=pg-abc123xyz;Port=5432;Database=postgres;Username=postgres;Password=uzun-rastgele-sifre`

---

## 3. API uygulaması

### 3.1 Uygulamayı oluşturun

Aynı projede **+ New → Public Repository**:

| Alan | Değer |
|---|---|
| Repository URL | `https://github.com/exulive/eule` |
| Branch | `main` |
| Build Pack | **Dockerfile** |
| Base Directory | `/` |
| Dockerfile Location | `/src/Eule.Server/Dockerfile` |

**Continue** deyin, sonra uygulama ayarlarında (**Configuration → General**):

| Alan | Değer |
|---|---|
| Name | `eule-api` |
| Domains | `https://eule.adacreative.net` |
| Ports Exposes | `8080` |
| Watch Paths | `src/Eule.Server/**` ve alt satıra `src/Eule.Parsing/**` |

*Watch Paths*, masaüstü uygulamasında yaptığınız değişikliklerin sunucuyu gereksiz yere yeniden kurmasını önler.

### 3.2 Sağlık kontrolü

**Configuration → Healthcheck**:

| Alan | Değer |
|---|---|
| Enabled | açık |
| Path | `/health` |
| Port | `8080` |

### 3.3 Gizli değerler (Environment Variables)

**Configuration → Environment Variables** → **Developer view** / **+ Add**. Hiçbirini *Build Variable* yapmayın.

| Ad | Değer |
|---|---|
| `ConnectionStrings__Default` | 2. adımda hazırladığınız bağlantı cümlesi |
| `Jwt__SigningKey` | En az 32 karakterlik rastgele bir anahtar (aşağıdaki komutla üretin) |

Anahtarı kendi bilgisayarınızda üretin (PowerShell):

```powershell
-join ((48..57)+(65..90)+(97..122) | Get-Random -Count 64 | ForEach-Object {[char]$_})
```

> **Bu anahtarı kimseyle paylaşmayın ve GitHub'a koymayın.** Anahtarı değiştirirseniz bütün kullanıcıların
> oturumu kapanır ve yeniden giriş yapmaları gerekir. Kasıtlı olarak bunu istiyorsanız kullanışlı bir yöntemdir.

Değişkenlerin adlarındaki **çift alt çizgi** (`__`) önemlidir. .NET bunu `ConnectionStrings:Default` olarak okur.

### 3.4 Kurun

Sağ üstten **Deploy**. İlk kurulum 2–4 dakika sürer. **Logs** sekmesinde şunu görmelisiniz:

```
Application started. Press Ctrl+C to shut down.
Hosting environment: Production
```

Veritabanı tabloları ilk açılışta kendiliğinden oluşturulur.

Tarayıcıda açın: **https://eule.adacreative.net/health** → `{"status":"ok","version":"0.1.0"}`

---

## 4. İlk hesabı açın

Kayıt ekranı henüz yok. Hesapları sunucunun terminalinden siz açarsınız.

Coolify'da `eule-api` → **Terminal** → konteyneri seçin → **Connect**, sonra:

```bash
dotnet Eule.Server.dll user add siz@adacreative.net --name "Ali"
```

Şifreyi iki kez sorar (yazarken görünmez, en az 8 karakter). Şifre komut satırına yazılmadığı için geçmişte kalmaz.

Diğer komutlar:

```bash
dotnet Eule.Server.dll user list                          # hesaplar, not sayıları, son giriş
dotnet Eule.Server.dll user passwd siz@adacreative.net    # şifre değiştir (tüm oturumlar kapanır)
dotnet Eule.Server.dll user disable biri@adacreative.net  # hesabı kapat (oturumlar hemen biter)
dotnet Eule.Server.dll user enable biri@adacreative.net   # yeniden aç
```

---

## 5. Deneyin

Kendi bilgisayarınızda PowerShell'de (e-posta ve şifreyi kendi hesabınızla değiştirin):

```powershell
$api = "https://eule.adacreative.net"
$login = Invoke-RestMethod "$api/api/auth/login" -Method Post -ContentType "application/json" -Body (@{ email = "siz@adacreative.net"; password = Read-Host "Şifre" } | ConvertTo-Json)
$h = @{ Authorization = "Bearer $($login.accessToken)" }
$body = [Text.Encoding]::UTF8.GetBytes((@{ clientId = [guid]::NewGuid(); text = "Cumartesi saat 4'te toplantı var, unutma"; timeZone = "Europe/Istanbul" } | ConvertTo-Json))
(Invoke-RestMethod "$api/api/dictations" -Method Post -Headers $h -ContentType "application/json; charset=utf-8" -Body $body).reply
```

Beklenen cevap: `Kaydettim: Toplantı · Cumartesi 21 Kasım, 16:00` (tarih, o günkü takvime göre değişir).

Kayıtlı notlar:

```powershell
Invoke-RestMethod "$api/api/notes" -Headers $h | Format-Table title, dueAt, text
```

---

## Sonraki güncellemeler

Sunucu kodu `main` dalına gönderildiğinde yeni sürümü kurmanın iki yolu var:

- **Elle:** Coolify'da `eule-api` → **Redeploy**.
- **Otomatik:** Coolify'da **Sources → + Add → GitHub App** ile GitHub hesabınızı bağlayın ve uygulamayı bu
  kaynaktan yeniden oluşturun. Bundan sonra `src/Eule.Server/` ya da `src/Eule.Parsing/` altındaki her değişiklik
  kendiliğinden kurulur. Herkese açık depo olarak eklenen uygulamalar otomatik kurulmaz.

Veritabanı değişiklikleri (migration) uygulama açılırken kendiliğinden uygulanır.

---

## Güvenlik notları

- Bağlantı yalnızca **HTTPS** üzerinden; sertifikayı Coolify kendisi alır ve yeniler.
- Şifreler veritabanında **tek yönlü özet** olarak tutulur (PBKDF2). Şifreyi kimse göremez, siz de.
- Giriş anahtarı **15 dakika** geçerlidir. Masaüstü uygulaması bunu **30 günlük yenileme anahtarıyla**
  sessizce yeniler. Her yenilemede eski anahtar geçersiz olur. Eski bir anahtar tekrar kullanılırsa
  (çalınmış olabilir) o kullanıcının **bütün oturumları kapatılır**.
- Giriş denemeleri adres başına **dakikada 10** ile sınırlıdır (şifre tahminine karşı).
- Hatalı giriş "kullanıcı yok" ile "şifre yanlış"ı ayırt etmez. Kimin hesabı olduğu dışarıdan anlaşılamaz.
- Bir kullanıcı başka birinin notunu hiçbir şekilde göremez, değiştiremez, silemez.
- **KVKK:** Sunucu artık başkalarının notlarını tutuyor. Veri sorumlusu sizsiniz.
  Kullanıcılara kısa bir aydınlatma metni sunmanız gerekir. Masaüstü uygulamasındaki gizlilik
  cümlesi de bağlantı adımında güncellenecek.

---

## Sorun giderme

| Belirti | Neden / Çözüm |
|---|---|
| Tarayıcıda **502 Bad Gateway** | *Ports Exposes* `8080` değil, ya da uygulama açılamadı. **Logs**'a bakın. |
| Loglarda `Jwt:SigningKey en az 32 karakter olmalı` | `Jwt__SigningKey` eksik ya da kısa. Ekleyip **Redeploy**. |
| Loglarda `ConnectionStrings:Default eksik` | `ConnectionStrings__Default` eksik ya da adı yanlış yazılmış (çift alt çizgi). |
| Loglarda `Failed to connect` / `Name or service not known` | Bağlantı cümlesindeki `Host` yanlış, ya da veritabanı başka sunucuda. *Postgres URL (internal)* içindeki sunucu adını kullanın. |
| Sertifika alınamıyor | DNS kaydı henüz yayılmadı ya da Cloudflare turuncu bulutta. 1. adıma bakın. |
| `/health` → `database_unreachable` | Uygulama çalışıyor ama veritabanına ulaşamıyor. Veritabanı çalışıyor mu, şifre doğru mu? |
| Giriş hep `401` | E-posta/şifre yanlış ya da hesap kapalı (`user list` ile bakın). Şifreyi `user passwd` ile yenileyin. |
| `429 Too Many Requests` | Bir dakikada 10'dan fazla giriş denemesi. Bir dakika bekleyin. |

---

## API özeti

Tüm istekler `https://eule.adacreative.net` altında, JSON ile. `🔒` olanlar `Authorization: Bearer <accessToken>` ister.

| Yöntem | Yol | Ne yapar |
|---|---|---|
| `GET` | `/health` | Sunucu ve veritabanı ayakta mı |
| `POST` | `/api/auth/login` | `{ email, password, device? }` → anahtarlar + kullanıcı |
| `POST` | `/api/auth/refresh` | `{ refreshToken }` → yeni anahtarlar (eskisi geçersiz) |
| `POST` | `/api/auth/logout` | `{ refreshToken }` → oturumu kapatır |
| `GET` 🔒 | `/api/me` | Giriş yapmış kullanıcı |
| `POST` 🔒 | `/api/dictations` | `{ clientId, text, timeZone?, source? }` → `{ action, reply, note }`. Aynı `clientId` tekrar gelirse yeni not açılmaz, ilk sonuç döner. |
| `GET` 🔒 | `/api/notes` | Notlar. `?since=<ISO zaman>` ile o andan sonraki bütün değişiklikler (silinenler dahil). |
| `GET` 🔒 | `/api/notes/{id}` | Tek not |
| `PATCH` 🔒 | `/api/notes/{id}` | `{ title?, dueAt?, hasTime?, done?, clearDue? }` |
| `DELETE` 🔒 | `/api/notes/{id}` | Notu siler ("Geri al") |

**Yapay zeka ajanı için hazırlık:** Her dikte, `dictations` tablosunda hangi ajanın ne yaptığıyla birlikte saklanır.
Şimdilik tek ajan **not kaydetme** (`save-note`). İleride bir yapay zeka ajanı aynı noktaya eklenecek
(`IDictationAgent`); masaüstü uygulaması değişmeden metni göndermeye ve gelen cevabı göstermeye devam edecek.
