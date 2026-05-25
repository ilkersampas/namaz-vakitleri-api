# Mobil App Entegrasyon Rehberi

Bu döküman namaz vakitleri API'sini Android (Kotlin), iOS (Swift) ve Flutter (Dart) mobil uygulamalarda nasıl kullanacağını anlatır.

> Base URL: `https://namaz-vakitleri-api.ilkersenel1957.workers.dev`  
> Tüm endpoint'ler `GET` ve CORS açık.  
> Auth gerekmez (rate limit: IP başına 60/dk).

---

## Endpoint Özeti

| Endpoint | Açıklama | Cache TTL | Tipik Kullanım |
|---|---|---|---|
| `/api/countries` | Ülke listesi | 7 gün | İlk kurulumda |
| `/api/states/{countryId}` | Eyalet/şehir listesi | 7 gün | Lokasyon seçimi |
| `/api/cities/{stateId}` | İlçe listesi | 7 gün | Lokasyon seçimi |
| `/api/city-detail/{cityId}` | Kıble açısı, Kabe mesafesi | 7 gün | Pusula için |
| `/api/prayer-times/monthly/{cityId}` | **Aylık vakitler (önerilen)** | 7 gün | Tek istekle tüm ay |
| `/api/prayer-times/daily/{cityId}` | Günlük vakit | 6 saat | Günlük widget |
| `/api/prayer-times/weekly/{cityId}` | Haftalık | 1 gün | — |
| `/api/prayer-times/eid/{cityId}` | Bayram namazı | 30 gün | Yılda 2 kere |
| `/api/prayer-times/ramadan/{cityId}` | Ramazan imsakiye | 7 gün | Ramazan ayında |
| `/api/daily-content` | Günün ayet/hadis/duası | 12 saat | Ana ekran widget |

### Yanıt formatı

```jsonc
{
  "data": [ /* veri */ ],
  "success": true,
  "message": null,
  "_source": "github",      // cache | github | diyanet
  "_cachedAt": "2026-05-25T12:00:00Z"
}
```

`_source` alanı ile mobil tarafta cache hit ratio ölçebilirsin.

---

## Önerilen mimari (mobil)

```
Kullanıcı açtı
   ↓
1) Lokal SQLite cache var mı? (offline-first)
   ↓ evet → göster, arka planda 2'yi tetikle
   ↓ hayır → 2'ye git
2) /api/prayer-times/monthly/{cityId}
   ↓ başarılı → SQLite'a yaz, göster
   ↓ başarısız (offline) → kullanıcıyı uyar
```

**Anahtar prensip:** Aylık veriyi tek seferde çek, ay boyunca lokal'den oku. Günde 1 kere ETag/If-Modified-Since ile tazele.

---

## Android (Kotlin) — Retrofit + Coroutines

### Gradle bağımlılıkları (build.gradle.kts)
```kotlin
dependencies {
    implementation("com.squareup.retrofit2:retrofit:2.11.0")
    implementation("com.squareup.retrofit2:converter-moshi:2.11.0")
    implementation("com.squareup.okhttp3:logging-interceptor:4.12.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
}
```

### API tanımı
```kotlin
data class ApiResponse<T>(
    val data: T?,
    val success: Boolean,
    val message: String?,
    val _source: String?,
)

data class PrayerTime(
    val fajr: String,
    val sunrise: String,
    val dhuhr: String,
    val asr: String,
    val maghrib: String,
    val isha: String,
    val gregorianDateShort: String?,
    val hijriDateShort: String?,
)

interface NamazApi {
    @GET("api/prayer-times/monthly/{cityId}")
    suspend fun monthly(@Path("cityId") cityId: Int): ApiResponse<List<PrayerTime>>

    @GET("api/states/{countryId}")
    suspend fun states(@Path("countryId") countryId: Int): ApiResponse<List<Place>>

    @GET("api/cities/{stateId}")
    suspend fun cities(@Path("stateId") stateId: Int): ApiResponse<List<Place>>

    @GET("api/city-detail/{cityId}")
    suspend fun cityDetail(@Path("cityId") cityId: Int): ApiResponse<CityDetail>

    @GET("api/daily-content")
    suspend fun dailyContent(): ApiResponse<DailyContent>
}

object ApiClient {
    private const val BASE_URL = "https://namaz-vakitleri-api.ilkersenel1957.workers.dev/"

    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .addInterceptor(HttpLoggingInterceptor().apply {
            level = if (BuildConfig.DEBUG) HttpLoggingInterceptor.Level.BASIC else HttpLoggingInterceptor.Level.NONE
        })
        .cache(Cache(File(context.cacheDir, "http"), 10L * 1024 * 1024)) // 10 MB
        .build()

    val api: NamazApi by lazy {
        Retrofit.Builder()
            .baseUrl(BASE_URL)
            .client(okHttp)
            .addConverterFactory(MoshiConverterFactory.create())
            .build()
            .create(NamazApi::class.java)
    }
}
```

### Kullanım (ViewModel)
```kotlin
class PrayerViewModel(private val cityId: Int) : ViewModel() {
    val state = MutableStateFlow<UiState>(UiState.Loading)

    fun load() = viewModelScope.launch {
        runCatching { ApiClient.api.monthly(cityId) }
            .onSuccess { resp ->
                if (resp.success && resp.data != null) {
                    state.value = UiState.Success(resp.data, fromCache = resp._source != "diyanet")
                } else {
                    state.value = UiState.Error(resp.message ?: "Bilinmeyen hata")
                }
            }
            .onFailure { state.value = UiState.Error(it.message ?: "Ağ hatası") }
    }
}
```

### Retry + offline
```kotlin
suspend fun <T> retry(
    times: Int = 3,
    initialDelayMs: Long = 1000,
    block: suspend () -> T
): T {
    var delay = initialDelayMs
    repeat(times - 1) {
        try { return block() }
        catch (e: IOException) {
            delay(delay)
            delay *= 2
        }
    }
    return block()
}
```

---

## iOS (Swift) — async/await + URLSession

### Modeller
```swift
struct ApiResponse<T: Decodable>: Decodable {
    let data: T?
    let success: Bool
    let message: String?
    let source: String?

    enum CodingKeys: String, CodingKey {
        case data, success, message
        case source = "_source"
    }
}

struct PrayerTime: Decodable {
    let fajr, sunrise, dhuhr, asr, maghrib, isha: String
    let gregorianDateShort: String?
    let hijriDateShort: String?
}
```

### API client
```swift
final class NamazAPI {
    static let shared = NamazAPI(baseURL: URL(string: "https://namaz-vakitleri-api.ilkersenel1957.workers.dev")!)

    private let baseURL: URL
    private let session: URLSession

    init(baseURL: URL) {
        self.baseURL = baseURL
        let config = URLSessionConfiguration.default
        config.timeoutIntervalForRequest = 15
        config.requestCachePolicy = .returnCacheDataElseLoad
        config.urlCache = URLCache(memoryCapacity: 4 * 1024 * 1024,
                                    diskCapacity: 20 * 1024 * 1024)
        self.session = URLSession(configuration: config)
    }

    func monthly(cityId: Int) async throws -> [PrayerTime] {
        try await get("/api/prayer-times/monthly/\(cityId)")
    }

    func cityDetail(cityId: Int) async throws -> CityDetail {
        try await get("/api/city-detail/\(cityId)")
    }

    private func get<T: Decodable>(_ path: String) async throws -> T {
        let url = baseURL.appendingPathComponent(path)
        let (data, response) = try await session.data(from: url)

        guard let http = response as? HTTPURLResponse else {
            throw URLError(.badServerResponse)
        }
        if http.statusCode == 429 {
            throw NSError(domain: "NamazAPI", code: 429,
                          userInfo: [NSLocalizedDescriptionKey: "Çok fazla istek, bekleyin"])
        }
        guard (200...299).contains(http.statusCode) else {
            throw URLError(.init(rawValue: http.statusCode))
        }

        let wrapper = try JSONDecoder().decode(ApiResponse<T>.self, from: data)
        guard wrapper.success, let payload = wrapper.data else {
            throw NSError(domain: "NamazAPI", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: wrapper.message ?? "Veri yok"])
        }
        return payload
    }
}
```

### View kullanımı
```swift
struct PrayerView: View {
    @State private var times: [PrayerTime] = []
    let cityId: Int

    var body: some View {
        List(times, id: \.gregorianDateShort) { Text($0.fajr) }
            .task { await load() }
    }

    func load() async {
        do { times = try await NamazAPI.shared.monthly(cityId: cityId) }
        catch { print("Hata:", error) }
    }
}
```

---

## Flutter (Dart) — `dio` + `hive` offline cache

### `pubspec.yaml`
```yaml
dependencies:
  dio: ^5.7.0
  dio_cache_interceptor: ^3.5.0
  dio_cache_interceptor_hive_store: ^4.0.0
  hive_flutter: ^1.1.0
```

### API client
```dart
class NamazApi {
  static const baseUrl = 'https://namaz-vakitleri-api.ilkersenel1957.workers.dev';

  late final Dio _dio;

  Future<void> init() async {
    final cacheDir = (await getTemporaryDirectory()).path;
    final cacheStore = HiveCacheStore('$cacheDir/namaz-cache');

    final cacheOptions = CacheOptions(
      store: cacheStore,
      policy: CachePolicy.refreshForceCache,
      maxStale: const Duration(days: 7),
    );

    _dio = Dio(BaseOptions(
      baseUrl: baseUrl,
      connectTimeout: const Duration(seconds: 10),
      receiveTimeout: const Duration(seconds: 20),
    ));

    _dio.interceptors.add(DioCacheInterceptor(options: cacheOptions));
  }

  Future<List<PrayerTime>> monthly(int cityId) async {
    final resp = await _dio.get('/api/prayer-times/monthly/$cityId');
    final list = (resp.data['data'] as List)
        .map((e) => PrayerTime.fromJson(e))
        .toList();
    return list;
  }

  Future<CityDetail> cityDetail(int cityId) async {
    final resp = await _dio.get('/api/city-detail/$cityId');
    return CityDetail.fromJson(resp.data['data']);
  }
}

class PrayerTime {
  final String fajr, sunrise, dhuhr, asr, maghrib, isha;
  final String? gregorianDateShort;
  PrayerTime.fromJson(Map<String, dynamic> j)
      : fajr = j['fajr'],
        sunrise = j['sunrise'],
        dhuhr = j['dhuhr'],
        asr = j['asr'],
        maghrib = j['maghrib'],
        isha = j['isha'],
        gregorianDateShort = j['gregorianDateShort'];
}
```

---

## Best Practices

1. **Aylık endpoint'i kullan**, daily/weekly yerine. Tek istekle 30 günlük veriyi al, lokalden oku. Bu hem Worker'a yükü azaltır hem mobil pil tüketimini düşürür.

2. **Şehir indeksini cache'le.** `/api/states/2` ve `/api/cities/{stateId}` çıktısı 7 gün cache'lidir, ama mobil tarafta SQLite/Hive ile *kalıcı* tut. Sadece kullanıcı manuel "şehir listesini yenile" derse tekrar çek.

3. **Background widget'lar için günlük endpoint yerine** aylık veriden bugünün tarihini filtrele:
   ```kotlin
   val today = prayerTimes.find { it.gregorianDateShort == LocalDate.now().toString() }
   ```

4. **Time zone:** `GreenwichMeanTimeZone` alanı `+3` (Türkiye) dönüyor. Cihazın TZ'iyle çakışırsa kullanıcının cihaz TZ'ini esas al.

5. **Pull-to-refresh:** Aylık veriyi yeniden çekmek yerine sadece `daily-content`'ı (ayet/hadis) tazele.

6. **Hata UX:**
   - 429: "Çok fazla istek attınız, bir dakika bekleyin."
   - 502 / network error: Lokal cache'i göster + ufak banner "Çevrimdışı".
   - Boş `data`: "Bu il/ilçe için veri bulunamadı, lokasyon değiştirin."

7. **Rate limit kotası:** Worker 60 istek/dk/IP veriyor. Tek mobil app için fazlasıyla yeterli; mobil tarafta zaten aylık çekiyorsanız ayda 30 istek bile etmez. Endişelenme.

8. **CDN edge cache'i kullanın:** Tarayıcıdan çağırıyorsanız (PWA), `Cache-Control: s-maxage=...` zaten dönüyor → tarayıcı otomatik cache'ler. Native app'te HTTP client'a cache eklemek gerekir (yukarıda Retrofit/URLSession/dio örnekleri var).

9. **Boş şehir vs offline:** `data: []` (boş array) → veri yok demektir. `null` veya 502 → offline veya hata. UI'da ayır.

10. **Kıble pusulası:** `/api/city-detail/{cityId}` yanıtındaki `QiblaAngle` (derece) ile cihazın magnetometer açısını birleştirerek pusula oku oluştur.

---

## Test endpoint'leri (sandbox)

Gerçek deploy URL'in olmadan da test edebilirsin:
```bash
# Sağlık
curl https://namaz-vakitleri-api.ilkersenel1957.workers.dev/health

# Kadıköy (cityId=9541) örnek
curl https://namaz-vakitleri-api.ilkersenel1957.workers.dev/api/prayer-times/monthly/9541
```

---

## Performans hedefleri

| Metrik | Hedef |
|---|---|
| İlk açılış (cold start, GH Pages) | < 500 ms |
| Cache hit (CF edge) | < 100 ms |
| Aylık veri boyutu | ~25 KB / ay (gzipped ~6 KB) |
| Mobil app pil tüketimi | Ayda 1 network req → ihmal edilebilir |

---

## Tipik şehir ID'leri (referans)

| Şehir/İlçe | ID |
|---|---|
| Kadıköy/İstanbul | 9541 |
| Ankara/Çankaya | 9206 |
| İzmir/Konak | 9560 |
| Bursa/Osmangazi | 9335 |

Tam liste: `/api/cities/{stateId}.json` veya `/data/city-index.json` (GH Pages üzerinden).
