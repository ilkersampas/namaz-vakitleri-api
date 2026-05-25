# Namaz Vakitleri API

Diyanet İşleri Başkanlığı Awqat Salah verilerini mobil uygulamalar için **ücretsiz**, **dayanıklı** ve **hızlı** bir API olarak sunar.

[![CI](https://img.shields.io/badge/CI-passing-brightgreen)]() [![Lisans](https://img.shields.io/badge/lisans-MIT-blue)](#lisans)

> **Hedef kitle:** Android/iOS/Flutter geliştiricileri.  
> **Kuruluş süresi:** ~30–45 dakika ([KURULUM.md](KURULUM.md))  
> **Aylık maliyet:** $0

---

## Mimari

```
   Mobil App
       │
       ▼
┌────────────────────────────┐
│  Cloudflare Worker (edge)  │  ← Rate limit, CORS, token mgmt
└──────────┬─────────────────┘
           │
   ┌───────┴────────┬──────────────────┐
   ▼                ▼                  ▼
Edge Cache    GitHub Pages       Diyanet API
(7 gün)       (statik JSON)      (canlı, son çare)
              (aylık güncel)
```

**Üç katmanlı cache stratejisi:**
1. **Cloudflare Edge Cache** — en hızlı, anında, ücretsiz CDN
2. **GitHub Pages** — aylık güncellenen statik JSON, Diyanet API yedeği
3. **Diyanet API** — sadece ilk istekte veya cache miss durumunda

Bu sayede Diyanet API'sinin kotası (endpoint başına ~5/sn) aşılmadan binlerce mobil cihaza servis verilir.

---

## Özellikler

- ✅ **Sıfır maliyet** — Cloudflare Workers (100K istek/gün) + GitHub Pages + Actions
- ✅ **3 katmanlı cache** — edge → static → origin fallback
- ✅ **Token yönetimi** — KV store + in-flight lock + proaktif refresh
- ✅ **Rate limiting** — IP başına 60 istek/dakika
- ✅ **Otomatik veri tazeleme** — her ayın 1'i GitHub Actions cron
- ✅ **Structured logging** — Cloudflare observability ile debug kolaylığı
- ✅ **CORS açık** — web/PWA'dan da çağrılabilir
- ✅ **Test coverage** — Worker (Vitest+Miniflare) ve Fetcher (xUnit)
- ✅ **Health check** — `/health` endpoint ile monitoring

---

## API Endpoint'leri

Tam liste ve mobil entegrasyon örnekleri için → [MOBILE.md](MOBILE.md)

| Endpoint | Açıklama |
|---|---|
| `GET /api/countries` | Ülke listesi |
| `GET /api/states/{countryId}` | Eyalet listesi (örn. `/2` Türkiye) |
| `GET /api/cities/{stateId}` | İlçe listesi |
| `GET /api/city-detail/{cityId}` | Kıble açısı, Kabe mesafesi |
| `GET /api/prayer-times/daily/{cityId}` | Günlük vakitler |
| `GET /api/prayer-times/weekly/{cityId}` | Haftalık vakitler |
| `GET /api/prayer-times/monthly/{cityId}` | **Aylık (önerilen)** |
| `GET /api/prayer-times/eid/{cityId}` | Bayram namazı |
| `GET /api/prayer-times/ramadan/{cityId}` | Ramazan imsakiye |
| `GET /api/daily-content` | Günün ayet/hadis/duası |
| `GET /health` | Sağlık kontrolü |
| `GET /` | Endpoint listesi (bu sayfa) |

Tüm yanıtlar şu formatta:
```json
{
  "data": [...],
  "success": true,
  "message": null,
  "_source": "github",
  "_cachedAt": "2026-05-25T12:00:00Z"
}
```

`_source` alanı `cache | github | diyanet` değerlerinden birini alır.

---

## Klasör Yapısı

```
NamazVaktiAPI/
├── KURULUM.md              ← İlk kez kuran herkes için
├── MOBILE.md               ← Android/iOS/Flutter entegrasyon
├── basvuru-formu.html      ← Diyanet'e API başvuru formu
├── .github/workflows/
│   ├── ci.yml              ← Worker + Fetcher test
│   ├── deploy-worker.yml   ← Cloudflare deploy
│   └── fetch-prayer-times.yml  ← Aylık veri çekme cron
├── worker/                 ← Cloudflare Worker (JavaScript)
│   ├── src/index.js
│   ├── test/index.test.js
│   ├── vitest.config.js
│   ├── wrangler.toml
│   └── package.json
├── src/
│   ├── DiyanetFetcher/         ← Aylık veri çekici (.NET 8 console)
│   ├── DiyanetFetcher.Tests/   ← xUnit
│   └── DiyanetFetcher.slnx
└── docs/                   ← GitHub Pages (statik JSON)
    ├── index.html
    └── data/
        ├── countries.json
        ├── states/{id}.json
        ├── cities/{id}.json
        ├── city-detail/{id}.json
        ├── prayer-times/{cityId}/{yyyy-MM}.json
        ├── eid/{cityId}/{yyyy}.json
        ├── ramadan/{cityId}/{yyyy}.json
        ├── daily-content/{yyyy-MM-dd}.json
        ├── city-index.json
        └── meta.json
```

---

## Hızlı Başlangıç

### 1) Diyanet başvurusu
[basvuru-formu.html](basvuru-formu.html) → yazdır, imzala, `dinisleriyk@diyanet.gov.tr`'ye yolla. Onay 1–2 hafta sürebilir.

### 2) Detaylı kurulum
[KURULUM.md](KURULUM.md) → sıfırdan deploy'a tüm adımlar.

### 3) Mobil entegrasyon
[MOBILE.md](MOBILE.md) → Android (Kotlin/Retrofit), iOS (Swift/URLSession), Flutter (Dart/dio) örnekleri.

---

## Geliştirme

### Worker (Cloudflare)
```bash
cd worker
npm install
npx wrangler dev      # http://127.0.0.1:8787
npm test              # Vitest + Miniflare
```

### Fetcher (.NET 8)
```bash
cd src
dotnet build DiyanetFetcher.slnx
dotnet test DiyanetFetcher.slnx

# Manuel veri çekme (env: DIYANET_EMAIL / DIYANET_PASSWORD gerekir)
$env:DIYANET_EMAIL="..."
$env:DIYANET_PASSWORD="..."
dotnet run --project DiyanetFetcher
```

### Çalıştırılabilir parametreler (DiyanetFetcher)
| Env | Default | Açıklama |
|---|---|---|
| `DIYANET_EMAIL` | - | Zorunlu |
| `DIYANET_PASSWORD` | - | Zorunlu |
| `OUTPUT_DIR` | `../../docs/data` | JSON çıktı klasörü |
| `COUNTRY_IDS` | `2` | Virgülle ayrılmış ülke ID (örn. `2,5,8`) |
| `INCLUDE_WEEKLY` | `true` | Haftalık vakit dahil |
| `INCLUDE_RAMADAN` | `true` | Ramazan imsakiyesi dahil |
| `INCLUDE_EID` | `true` | Bayram namazı dahil |
| `INCLUDE_CITY_DETAIL` | `true` | Kıble açısı / Kabe mesafesi dahil |
| `PER_CITY_DELAY_MS` | `300` | Şehirler arası bekleme (kota koruma) |

---

## Mimari Kararlar

| Karar | Neden |
|---|---|
| Cloudflare Workers, AWS Lambda değil | Edge'de çalışır, soğuk başlangıç yok, ücretsiz kotası daha geniş, KV namespace dahil |
| Token KV'de, `caches.default`'ta değil | `caches.default` cache stratejisidir, secret store değil — herkese görünür edge cache |
| In-flight lock token refresh için | Aynı anda 50 mobil cihaz token isterse 1 login yeter |
| 3 katmanlı cache | Diyanet API kotası darboğaz; statik JSON ile bunu absorbe et |
| Aylık endpoint öncelikli | 30 günlük veri tek istekte → mobil battery + Worker yükü düşer |
| ISO 8601 hafta numarası | Yıl başı/sonu hafta tutarsızlığını engelle |
| Atomik dosya yazımı (`tmp → rename`) | GitHub Actions yarıda kalırsa yarım JSON GH Pages'e düşmesin |
| xUnit + FluentAssertions | Endüstri standardı, idiomatic .NET test |
| Vitest + Miniflare | Cloudflare Workers için resmi test pool |

---

## Limit ve Kotalar

| Kaynak | Limit | Yorum |
|---|---|---|
| Cloudflare Workers (ücretsiz) | 100K istek/gün | 10K aktif mobil kullanıcı için yeterli |
| Cloudflare KV | 100K okuma/gün | Token + rate-limit için yeterli |
| GitHub Pages | 100 GB/ay | Pratikte sınırsız |
| GitHub Actions | 2000 dk/ay | Aylık veri çekme ~60–90 dk |
| Diyanet API | ~5 req/sn/endpoint | Fetcher rate-limiter buna uyumlu |
| Worker rate-limit (siz) | 60 req/dk/IP | Mobil app için fazlasıyla yeterli |

Daha yüksek trafik beklenirse:
- Cloudflare Workers Paid ($5/ay) → 10M istek/gün
- Veya kendi Diyanet hesabını birden fazla servis arasında pool'la

---

## Sorun Giderme

[KURULUM.md#sorun-giderme](KURULUM.md#sorun-giderme) bölümüne bak.

Cloudflare Worker loglarını canlı izle:
```bash
cd worker
npx wrangler tail
```

---

## Katkı

PR'lara açığım. Lütfen önce:
1. `worker/`: `npm test` geçsin
2. `src/`: `dotnet test src/DiyanetFetcher.slnx` geçsin
3. Yeni endpoint ekliyorsan `MOBILE.md` ve `KURULUM.md`'yi de güncelle

---

## Lisans

MIT

Kaynak: [Diyanet İşleri Başkanlığı — Awqat Salah API](https://awqatsalah.diyanet.gov.tr)

---

## Versiyon

**v2.0.0** — KV token storage, rate limiting, in-flight lock, ISO 8601 week, full GH Pages fallback, structured logging, xUnit + Vitest test coverage, CI/CD workflows.
