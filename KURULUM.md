# Kurulum Rehberi

Bu rehber sıfırdan namaz vakitleri API'sini ayağa kaldırır. Sırayla takip et — her adımın çıktısı bir sonraki adımda gerekecek.

> Süre: ~30–45 dakika  
> Gereksinimler: GitHub hesabı, Cloudflare hesabı (ücretsiz), Diyanet API credential

---

## 0. Önkoşullar

| Gereksinim | Nasıl alınır |
|---|---|
| Diyanet API credential | [basvuru-formu.html](basvuru-formu.html) doldur, imzala, `dinisleriyk@diyanet.gov.tr`'ye yolla. Onay 1–2 hafta sürebilir. |
| GitHub hesabı | <https://github.com> |
| Cloudflare hesabı | <https://dash.cloudflare.com/sign-up> (ücretsiz, kredi kartı gerekmez) |
| Lokal makinede | Node.js 20+, .NET 8 SDK, Git |

---

## 1. GitHub repo oluştur

```bash
# Lokal makinede
cd c:/zCalismaDosyalari/____Ilker_Proje/Diyanet_API_Dokümasyon/NamazVaktiAPI
git init
git add .
git commit -m "Initial commit"

# GitHub'da yeni repo aç (örnek isim: namaz-vakitleri-api)
git branch -M main
git remote add origin https://github.com/<KULLANICI>/namaz-vakitleri-api.git
git push -u origin main
```

---

## 2. Cloudflare kurulumu

### 2.1. Workers & Pages aktif et

1. <https://dash.cloudflare.com> → soldaki menüde **Workers & Pages** seç.
2. İlk kullanımda subdomain seçimi ister: örn. `mehmet-yz` → URL `https://*.mehmet-yz.workers.dev` olur.

### 2.2. KV namespace'leri oluştur

Cloudflare Dashboard → **Workers & Pages** → **KV** → **Create namespace**.

İki tane oluştur:

| Namespace adı | Açıklama |
|---|---|
| `TOKEN_KV` | Diyanet JWT token saklar |
| `RATE_LIMIT_KV` | IP başına rate-limit sayacı |

Her birinin ID'sini bir kenara yaz (örn. `a1b2c3d4e5f6...`).

### 2.3. API Token oluştur

Cloudflare Dashboard → sağ üst **profil ikonu** → **My Profile** → **API Tokens** → **Create Token**.

**Edit Cloudflare Workers** şablonunu seç → izinler:
- Account → Workers Scripts → Edit
- Account → Workers KV Storage → Edit
- Zone → Workers Routes → Edit
- User → Memberships → Read

Token'ı kopyala. Bu token sadece bir kere gösterilir.

### 2.4. Account ID'yi al

Dashboard sağ panelinde **Account ID** alanı var. Kopyala.

---

## 3. `wrangler.toml`'ı doldur

[worker/wrangler.toml](worker/wrangler.toml) dosyasını düzenle:

```toml
[[kv_namespaces]]
binding = "TOKEN_KV"
id = "BURAYA_TOKEN_KV_ID"     # 2.2'den

[[kv_namespaces]]
binding = "RATE_LIMIT_KV"
id = "BURAYA_RATE_LIMIT_KV_ID"  # 2.2'den

[vars]
GITHUB_PAGES_BASE = "https://<KULLANICI>.github.io/namaz-vakitleri-api/data"
```

Commit et:
```bash
git add worker/wrangler.toml
git commit -m "config: KV namespace ID'leri ve GH Pages URL"
git push
```

---

## 4. Cloudflare secret'larını set et

Lokal makinede:

```bash
cd worker
npm install
npx wrangler login   # tarayıcı açılır, Cloudflare'e bağla

npx wrangler secret put DIYANET_EMAIL
# Sor: prod ortamı için secret değeri → e-postanı yapıştır

npx wrangler secret put DIYANET_PASSWORD
# Sor: prod ortamı için secret değeri → şifrenle yapıştır
```

Doğrula:
```bash
npx wrangler secret list
# DIYANET_EMAIL ve DIYANET_PASSWORD görünmeli
```

---

## 5. GitHub secret'larını set et

GitHub repo → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**.

| Secret adı | Değer |
|---|---|
| `DIYANET_EMAIL` | Diyanet API e-postan |
| `DIYANET_PASSWORD` | Diyanet API şifren |
| `CLOUDFLARE_API_TOKEN` | 2.3'ten aldığın token |
| `CLOUDFLARE_ACCOUNT_ID` | 2.4'ten aldığın Account ID |
| `WORKER_URL` | İlk deploy'dan sonra: `https://namaz-vakitleri-api.<subdomain>.workers.dev` (deploy sonrası ekleyebilirsin) |

---

## 6. GitHub Pages aktif et

GitHub repo → **Settings** → **Pages**:
- **Source:** Deploy from a branch
- **Branch:** `main`
- **Folder:** `/docs`

Kaydet. Birkaç dakika sonra `https://<KULLANICI>.github.io/<REPO>/` çalışır hâle gelir.

---

## 7. İlk veri çekimi

Burada `docs/data/` klasörünü dolduruyoruz. GitHub Actions üzerinden tetikle:

GitHub repo → **Actions** → **Namaz Vakitlerini Guncelle** → **Run workflow**.

Varsayılan parametreler (`COUNTRY_IDS=2` Türkiye, bayram + ramazan dahil) yeterli. Çalışma süresi: ~60–90 dakika (Türkiye ~970 ilçe için).

İş bittiğinde repo'ya otomatik commit gelir: `chore(data): namaz vakitleri guncellendi YYYY-MM-DD`.

---

## 8. Worker'ı deploy et

İlk deploy GitHub Actions üzerinden:

GitHub repo → **Actions** → **Cloudflare Worker Deploy** → **Run workflow**.

Veya lokal:
```bash
cd worker
npx wrangler deploy
```

Çıktıdaki URL'i not al: `https://namaz-vakitleri-api.<subdomain>.workers.dev`

Bu URL'i `WORKER_URL` GitHub secret olarak ekle (adım 5).

---

## 9. Doğrulama

```bash
# Sağlık kontrolü
curl https://namaz-vakitleri-api.<subdomain>.workers.dev/health

# Beklenen:
# { "healthy": true, "checks": { "worker": "ok", "kv_token": "bound", ... } }

# Türkiye eyaletleri (GH Pages cache'inden gelir)
curl https://namaz-vakitleri-api.<subdomain>.workers.dev/api/states/2

# Kadıköy aylık vakitler
curl https://namaz-vakitleri-api.<subdomain>.workers.dev/api/prayer-times/monthly/9541
```

Her cevabın içinde `_source: "cache" | "github" | "diyanet"` alanı bulunur — hangi katmandan geldiğini görürsün.

---

## 10. Otomatikleştirme (cron)

Workflow zaten cron'da: her ayın 1'i 03:00 UTC'de `fetch-prayer-times` tetiklenir. Manuel çalıştırmak istersen Actions sekmesinden **Run workflow**.

---

## Sorun giderme

### `/health` 503 dönüyor
- `kv_token` veya `kv_rate_limit` `missing` ise → wrangler.toml'da ID eksik ya da yanlış.
- `credentials` `missing` ise → wrangler secret'lar set edilmemiş.

### 401 Unauthorized (Diyanet'ten)
- Diyanet credential yanlış. Önce `https://awqatsalah.diyanet.gov.tr/index.html` üzerinde manual login dene.

### 429 Rate Limited
- IP başına 60 istek/dakika. Mobil app'te exponential backoff implement et (MOBILE.md'ye bak).

### GitHub Actions fetch çok yavaş
- Diyanet API'sinin kotası endpoint başına ~5/saniye. Tüm Türkiye için 60–90 dk normal.
- `INCLUDE_WEEKLY=false` ile başla (mobil aylık veriyi yerelden hesaplayabilir).

### Worker deploy `REPLACE_WITH_` hatası
- wrangler.toml'daki KV ID placeholder'ları doldurmamışsın.

---

## Sonraki adım

Mobil app entegrasyonu için → [MOBILE.md](MOBILE.md)
