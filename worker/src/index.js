// ============================================================
// NAMAZ VAKITLERI API - Cloudflare Worker
// ============================================================
// Mimari:
//   1) Cloudflare Cache (edge, hizli, anlik)
//   2) GitHub Pages (statik JSON, aylik guncellenir)
//   3) Diyanet API (canli, son care)
//
// Guvenlik:
//   - Token KV namespace'te saklanir (caches.default DEGIL)
//   - IP basina rate-limit (KV sayac)
//   - Tek seferde tek token refresh (in-flight lock)
// ============================================================

const DIYANET_BASE = "https://awqatsalah.diyanet.gov.tr";
const TOKEN_KV_KEY = "diyanet:token";
const TOKEN_LOCK_KV_KEY = "diyanet:token-lock";
const TOKEN_REFRESH_BEFORE_SECONDS = 5 * 60; // token suresi dolmadan 5 dk once yenile
const TOKEN_LIFETIME_SECONDS = 30 * 60; // Diyanet token suresi ~30 dk
const RATE_LIMIT_WINDOW_SECONDS = 60;
const RATE_LIMIT_MAX_REQUESTS = 60; // IP basina dakikada 60 istek

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
  "Access-Control-Max-Age": "86400",
};

// ============================================================
// ROUTER
// ============================================================

const routes = [
  { pattern: /^\/$/, handler: handleIndex, cacheable: false },
  { pattern: /^\/health$/, handler: handleHealth, cacheable: false },
  { pattern: /^\/api\/countries$/, key: () => "countries", diyanet: () => "/api/Place/Countries", gh: () => "countries.json" },
  { pattern: /^\/api\/states\/(\d+)$/, key: (m) => `states-${m[1]}`, diyanet: (m) => `/api/Place/States/${m[1]}`, gh: (m) => `states/${m[1]}.json` },
  { pattern: /^\/api\/cities\/(\d+)$/, key: (m) => `cities-${m[1]}`, diyanet: (m) => `/api/Place/Cities/${m[1]}`, gh: (m) => `cities/${m[1]}.json` },
  { pattern: /^\/api\/city-detail\/(\d+)$/, key: (m) => `city-detail-${m[1]}`, diyanet: (m) => `/api/Place/CityDetail/${m[1]}`, gh: (m) => `city-detail/${m[1]}.json` },
  { pattern: /^\/api\/prayer-times\/daily\/(\d+)$/, key: (m) => `prayer-daily-${m[1]}-${todayKey()}`, diyanet: (m) => `/api/PrayerTime/Daily/${m[1]}`, gh: (m) => `prayer-times/${m[1]}/${monthKey()}.json`, cacheTtl: 6 * 3600 },
  { pattern: /^\/api\/prayer-times\/weekly\/(\d+)$/, key: (m) => `prayer-weekly-${m[1]}-${isoWeekKey()}`, diyanet: (m) => `/api/PrayerTime/Weekly/${m[1]}`, gh: (m) => `prayer-times/${m[1]}/${monthKey()}.json`, cacheTtl: 24 * 3600 },
  { pattern: /^\/api\/prayer-times\/monthly\/(\d+)$/, key: (m) => `prayer-monthly-${m[1]}-${monthKey()}`, diyanet: (m) => `/api/PrayerTime/Monthly/${m[1]}`, gh: (m) => `prayer-times/${m[1]}/${monthKey()}.json`, cacheTtl: 7 * 24 * 3600 },
  { pattern: /^\/api\/prayer-times\/eid\/(\d+)$/, key: (m) => `prayer-eid-${m[1]}-${yearKey()}`, diyanet: (m) => `/api/PrayerTime/Eid/${m[1]}`, gh: (m) => `eid/${m[1]}/${yearKey()}.json`, cacheTtl: 30 * 24 * 3600 },
  { pattern: /^\/api\/prayer-times\/ramadan\/(\d+)$/, key: (m) => `prayer-ramadan-${m[1]}-${yearKey()}`, diyanet: (m) => `/api/PrayerTime/Ramadan/${m[1]}`, gh: (m) => `ramadan/${m[1]}/${yearKey()}.json`, cacheTtl: 7 * 24 * 3600 },
  { pattern: /^\/api\/daily-content$/, key: () => `daily-content-${todayKey()}`, diyanet: () => "/api/DailyContent", gh: () => `daily-content/${todayKey()}.json`, cacheTtl: 12 * 3600 },
];

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    const path = url.pathname;
    const requestId = crypto.randomUUID();

    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }

    if (request.method !== "GET") {
      return jsonResponse({ success: false, message: "Sadece GET destekleniyor" }, 405);
    }

    try {
      // Rate limit
      const clientIp = request.headers.get("CF-Connecting-IP") || "unknown";
      const limited = await checkRateLimit(env, clientIp);
      if (limited) {
        log(env, "warn", { requestId, event: "rate_limited", ip: clientIp, path });
        return jsonResponse(
          { success: false, message: "Cok fazla istek. Lutfen biraz bekleyin." },
          429,
          { "Retry-After": String(RATE_LIMIT_WINDOW_SECONDS) }
        );
      }

      // Route esle
      for (const route of routes) {
        const match = path.match(route.pattern);
        if (!match) continue;

        if (route.handler) {
          const response = await route.handler(env, requestId);
          return withCors(response);
        }

        const cacheKey = route.key(match);
        const ttl = route.cacheTtl ?? (env.CACHE_TTL_SECONDS ? parseInt(env.CACHE_TTL_SECONDS) : 604800);
        const ghPath = route.gh ? route.gh(match) : null;
        const diyanetPath = route.diyanet(match);

        const response = await handleCached(env, ctx, { cacheKey, ttl, ghPath, diyanetPath, requestId });
        return withCors(response);
      }

      return withCors(jsonResponse({ success: false, message: "Endpoint bulunamadi" }, 404));
    } catch (err) {
      log(env, "error", { requestId, event: "uncaught", error: err.message, stack: err.stack, path });
      return withCors(jsonResponse({ success: false, message: `Sunucu hatasi: ${err.message}` }, 500));
    }
  },
};

// ============================================================
// CACHE KATMANI - 3 SEVIYELI
// ============================================================

async function handleCached(env, ctx, { cacheKey, ttl, ghPath, diyanetPath, requestId }) {
  const cache = caches.default;
  const cacheRequest = new Request(`https://cache.internal/${cacheKey}`);

  // 1) Edge cache
  const cached = await cache.match(cacheRequest);
  if (cached) {
    log(env, "info", { requestId, event: "cache_hit", key: cacheKey });
    return cached;
  }

  // 2) GitHub Pages
  if (ghPath) {
    const ghData = await tryGitHubPages(env, ghPath, requestId);
    if (ghData) {
      const body = wrapResponse(ghData, "github");
      const resp = jsonResponse(body, 200, { "Cache-Control": `public, s-maxage=${ttl}` });
      ctx.waitUntil(cache.put(cacheRequest, resp.clone()));
      return resp;
    }
  }

  // 3) Diyanet API
  const fresh = await fetchFromDiyanet(env, diyanetPath, requestId);
  if (fresh !== null) {
    const body = wrapResponse(fresh, "diyanet");
    const resp = jsonResponse(body, 200, { "Cache-Control": `public, s-maxage=${ttl}` });
    ctx.waitUntil(cache.put(cacheRequest, resp.clone()));
    return resp;
  }

  log(env, "error", { requestId, event: "all_sources_failed", key: cacheKey });
  return jsonResponse({ success: false, message: "Veri alinamadi" }, 502);
}

function wrapResponse(data, source) {
  // Eger zaten ApiResponse formatinda ise wrap'leme
  if (data && typeof data === "object" && "success" in data && "data" in data) {
    return { ...data, _source: source };
  }
  return {
    data,
    success: true,
    message: null,
    _source: source,
    _cachedAt: new Date().toISOString(),
  };
}

async function tryGitHubPages(env, relativePath, requestId) {
  const base = env.GITHUB_PAGES_BASE;
  if (!base) return null;

  try {
    const url = `${base.replace(/\/$/, "")}/${relativePath}`;
    const resp = await fetch(url, { cf: { cacheTtl: 300, cacheEverything: true } });
    if (resp.ok) {
      log(env, "info", { requestId, event: "gh_hit", path: relativePath });
      return await resp.json();
    }
    log(env, "info", { requestId, event: "gh_miss", path: relativePath, status: resp.status });
  } catch (err) {
    log(env, "warn", { requestId, event: "gh_error", path: relativePath, error: err.message });
  }
  return null;
}

// ============================================================
// DIYANET API + TOKEN YONETIMI
// ============================================================

async function fetchFromDiyanet(env, endpoint, requestId) {
  let token = await getValidToken(env, requestId);
  if (!token) return null;

  let resp = await fetch(`${DIYANET_BASE}${endpoint}`, {
    headers: { Authorization: `Bearer ${token.accessToken}` },
  });

  if (resp.status === 401) {
    log(env, "info", { requestId, event: "token_expired", endpoint });
    token = await refreshOrLogin(env, token, requestId);
    if (!token) return null;
    resp = await fetch(`${DIYANET_BASE}${endpoint}`, {
      headers: { Authorization: `Bearer ${token.accessToken}` },
    });
  }

  if (!resp.ok) {
    log(env, "warn", { requestId, event: "diyanet_error", endpoint, status: resp.status });
    return null;
  }

  try {
    const result = await resp.json();
    if (result.success && result.data !== undefined) {
      return result.data;
    }
    log(env, "warn", { requestId, event: "diyanet_unsuccessful", endpoint, message: result.message });
  } catch (err) {
    log(env, "error", { requestId, event: "diyanet_parse_error", endpoint, error: err.message });
  }
  return null;
}

async function getValidToken(env, requestId) {
  if (!env.TOKEN_KV) {
    log(env, "error", { requestId, event: "kv_not_bound" });
    return null;
  }

  const raw = await env.TOKEN_KV.get(TOKEN_KV_KEY);
  if (raw) {
    try {
      const token = JSON.parse(raw);
      if (token.expiresAt && Date.now() < token.expiresAt - TOKEN_REFRESH_BEFORE_SECONDS * 1000) {
        return token;
      }
      log(env, "info", { requestId, event: "token_proactive_refresh" });
      const refreshed = await refreshOrLogin(env, token, requestId);
      return refreshed ?? token;
    } catch {
      // bozuk token, login at
    }
  }

  return await acquireTokenWithLock(env, () => diyanetLogin(env, requestId), requestId);
}

async function refreshOrLogin(env, oldToken, requestId) {
  return await acquireTokenWithLock(env, async () => {
    const refreshed = await diyanetRefreshToken(env, oldToken, requestId);
    if (refreshed) return refreshed;
    log(env, "info", { requestId, event: "fallback_to_login" });
    return await diyanetLogin(env, requestId);
  }, requestId);
}

// In-flight lock: ayni anda birden fazla login/refresh denemesini engelle
async function acquireTokenWithLock(env, tokenFetcher, requestId) {
  const lockId = crypto.randomUUID();
  const lockTtl = 60; // Cloudflare KV minimum 60 saniye

  // KV'de lock yoksa kur, varsa diger isteklerin bitirmesini bekle
  const existingLock = await env.TOKEN_KV.get(TOKEN_LOCK_KV_KEY);
  if (existingLock) {
    // Baska bir istek token aliyor, bekle ve KV'den oku
    for (let i = 0; i < 10; i++) {
      await sleep(500);
      const fresh = await env.TOKEN_KV.get(TOKEN_KV_KEY);
      if (fresh) {
        try {
          return JSON.parse(fresh);
        } catch {
          break;
        }
      }
    }
    log(env, "warn", { requestId, event: "lock_wait_timeout" });
  }

  await env.TOKEN_KV.put(TOKEN_LOCK_KV_KEY, lockId, { expirationTtl: lockTtl });

  try {
    const token = await tokenFetcher();
    if (token) {
      await env.TOKEN_KV.put(TOKEN_KV_KEY, JSON.stringify(token), {
        expirationTtl: TOKEN_LIFETIME_SECONDS,
      });
    }
    return token;
  } finally {
    const currentLock = await env.TOKEN_KV.get(TOKEN_LOCK_KV_KEY);
    if (currentLock === lockId) {
      await env.TOKEN_KV.delete(TOKEN_LOCK_KV_KEY);
    }
  }
}

async function diyanetLogin(env, requestId) {
  if (!env.DIYANET_EMAIL || !env.DIYANET_PASSWORD) {
    log(env, "error", { requestId, event: "credentials_missing" });
    return null;
  }

  try {
    const resp = await fetch(`${DIYANET_BASE}/Auth/Login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email: env.DIYANET_EMAIL, password: env.DIYANET_PASSWORD }),
    });

    if (!resp.ok) {
      log(env, "error", { requestId, event: "login_failed", status: resp.status });
      return null;
    }

    const result = await resp.json();
    if (result.success && result.data) {
      log(env, "info", { requestId, event: "login_ok" });
      return {
        accessToken: result.data.accessToken,
        refreshToken: result.data.refreshToken,
        expiresAt: Date.now() + TOKEN_LIFETIME_SECONDS * 1000,
      };
    }
    log(env, "error", { requestId, event: "login_unsuccessful", message: result.message });
  } catch (err) {
    log(env, "error", { requestId, event: "login_exception", error: err.message });
  }
  return null;
}

async function diyanetRefreshToken(env, oldToken, requestId) {
  if (!oldToken?.refreshToken) return null;

  try {
    const resp = await fetch(`${DIYANET_BASE}/Auth/RefreshToken/${oldToken.refreshToken}`);
    if (!resp.ok) {
      log(env, "info", { requestId, event: "refresh_failed", status: resp.status });
      return null;
    }
    const result = await resp.json();
    if (result.success && result.data) {
      log(env, "info", { requestId, event: "refresh_ok" });
      return {
        accessToken: result.data.accessToken,
        refreshToken: result.data.refreshToken,
        expiresAt: Date.now() + TOKEN_LIFETIME_SECONDS * 1000,
      };
    }
  } catch (err) {
    log(env, "warn", { requestId, event: "refresh_exception", error: err.message });
  }
  return null;
}

// ============================================================
// RATE LIMITING (KV tabanli, IP basina)
// ============================================================

async function checkRateLimit(env, ip) {
  if (!env.RATE_LIMIT_KV) return false; // KV yoksa rate-limit kapali
  if (ip === "unknown") return false;

  const key = `rl:${ip}:${Math.floor(Date.now() / 1000 / RATE_LIMIT_WINDOW_SECONDS)}`;
  const current = await env.RATE_LIMIT_KV.get(key);
  const count = current ? parseInt(current) : 0;

  if (count >= RATE_LIMIT_MAX_REQUESTS) return true;

  await env.RATE_LIMIT_KV.put(key, String(count + 1), {
    expirationTtl: RATE_LIMIT_WINDOW_SECONDS * 2,
  });
  return false;
}

// ============================================================
// HANDLER'LAR
// ============================================================

function handleIndex() {
  return jsonResponse({
    name: "Namaz Vakitleri API",
    version: "2.0.0",
    source: "Diyanet Isleri Baskanligi - Awqat Salah API",
    endpoints: {
      "Ulkeler": "GET /api/countries",
      "Eyaletler": "GET /api/states/{countryId}",
      "Ilceler": "GET /api/cities/{stateId}",
      "Sehir Detay": "GET /api/city-detail/{cityId}",
      "Gunluk Namaz Vakti": "GET /api/prayer-times/daily/{cityId}",
      "Haftalik Namaz Vakti": "GET /api/prayer-times/weekly/{cityId}",
      "Aylik Namaz Vakti": "GET /api/prayer-times/monthly/{cityId}",
      "Bayram Namazi": "GET /api/prayer-times/eid/{cityId}",
      "Ramazan Imsakiye": "GET /api/prayer-times/ramadan/{cityId}",
      "Gunluk Icerik": "GET /api/daily-content",
      "Saglik Kontrolu": "GET /health",
    },
    ornekler: {
      "Turkiye eyaletleri": "/api/states/2",
      "Istanbul ilceleri": "/api/cities/539",
      "Kadikoy aylik vakitler": "/api/prayer-times/monthly/9541",
    },
    rate_limit: `${RATE_LIMIT_MAX_REQUESTS} istek / ${RATE_LIMIT_WINDOW_SECONDS} saniye (IP basina)`,
  });
}

async function handleHealth(env) {
  const checks = {
    worker: "ok",
    kv_token: env.TOKEN_KV ? "bound" : "missing",
    kv_rate_limit: env.RATE_LIMIT_KV ? "bound" : "missing",
    github_pages: env.GITHUB_PAGES_BASE ? "configured" : "missing",
    credentials: env.DIYANET_EMAIL && env.DIYANET_PASSWORD ? "configured" : "missing",
  };
  const healthy = Object.values(checks).every((v) => v === "ok" || v === "bound" || v === "configured");
  return jsonResponse({ healthy, checks, timestamp: new Date().toISOString() }, healthy ? 200 : 503);
}

// ============================================================
// YARDIMCI FONKSIYONLAR
// ============================================================

function todayKey() {
  return new Date().toISOString().slice(0, 10);
}

function monthKey() {
  return new Date().toISOString().slice(0, 7);
}

function yearKey() {
  return String(new Date().getUTCFullYear());
}

// ISO 8601 hafta numarasi
function isoWeekKey() {
  const d = new Date();
  const target = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));
  const dayNum = (target.getUTCDay() + 6) % 7;
  target.setUTCDate(target.getUTCDate() - dayNum + 3);
  const firstThursday = new Date(Date.UTC(target.getUTCFullYear(), 0, 4));
  const week = 1 + Math.round(((target - firstThursday) / 86400000 - 3 + ((firstThursday.getUTCDay() + 6) % 7)) / 7);
  return `${target.getUTCFullYear()}-W${String(week).padStart(2, "0")}`;
}

function jsonResponse(data, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(data, null, 2), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      ...extraHeaders,
    },
  });
}

function withCors(response) {
  const headers = new Headers(response.headers);
  for (const [k, v] of Object.entries(corsHeaders)) headers.set(k, v);
  return new Response(response.body, { status: response.status, headers });
}

function log(env, level, data) {
  if (env.LOG_LEVEL === "silent") return;
  const allowed = { silent: -1, error: 0, warn: 1, info: 2, debug: 3 };
  const threshold = allowed[env.LOG_LEVEL] ?? allowed.info;
  if (allowed[level] > threshold) return;
  console.log(JSON.stringify({ level, ts: new Date().toISOString(), ...data }));
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
