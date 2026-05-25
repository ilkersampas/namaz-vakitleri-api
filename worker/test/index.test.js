import { describe, it, expect, beforeEach, vi } from "vitest";
import { SELF, env } from "cloudflare:test";

// Diyanet & GitHub Pages mock'larini ayarla
function mockFetch(handlers) {
  return vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
    const url = typeof input === "string" ? input : input.url;
    for (const [pattern, response] of Object.entries(handlers)) {
      if (url.includes(pattern)) {
        return typeof response === "function" ? response(input) : response;
      }
    }
    return new Response("not mocked: " + url, { status: 599 });
  });
}

function jsonResponse(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

beforeEach(async () => {
  // KV'leri her testte temizle
  for (const ns of ["TOKEN_KV", "RATE_LIMIT_KV"]) {
    const list = await env[ns].list();
    await Promise.all(list.keys.map((k) => env[ns].delete(k.name)));
  }
  vi.restoreAllMocks();
});

describe("Routing", () => {
  it("ana sayfada endpoint listesi doner", async () => {
    const resp = await SELF.fetch("https://worker/");
    expect(resp.status).toBe(200);
    const body = await resp.json();
    expect(body.name).toContain("Namaz Vakitleri");
    expect(body.endpoints).toBeDefined();
  });

  it("bilinmeyen endpoint 404 doner", async () => {
    const resp = await SELF.fetch("https://worker/api/unknown");
    expect(resp.status).toBe(404);
  });

  it("OPTIONS isteklerine CORS header donen", async () => {
    const resp = await SELF.fetch("https://worker/api/countries", { method: "OPTIONS" });
    expect(resp.headers.get("Access-Control-Allow-Origin")).toBe("*");
  });

  it("POST gibi metodlar 405 doner", async () => {
    const resp = await SELF.fetch("https://worker/api/countries", { method: "POST" });
    expect(resp.status).toBe(405);
  });

  it("/health endpoint'i bind'leri raporlar", async () => {
    const resp = await SELF.fetch("https://worker/health");
    const body = await resp.json();
    expect(body.checks).toBeDefined();
    expect(body.checks.worker).toBe("ok");
    expect(body.checks.kv_token).toBe("bound");
  });
});

describe("3 katmanli cache akisi", () => {
  it("GH Pages'te varsa Diyanet'e gitmez", async () => {
    let diyanetCalled = false;
    mockFetch({
      "test.github.io": jsonResponse({ success: true, data: [{ Id: 2, Name: "TURKIYE" }] }),
      "awqatsalah.diyanet.gov.tr": () => {
        diyanetCalled = true;
        return jsonResponse({ success: true, data: [] });
      },
    });

    const resp = await SELF.fetch("https://worker/api/countries");
    expect(resp.status).toBe(200);
    const body = await resp.json();
    expect(body._source).toBe("github");
    expect(diyanetCalled).toBe(false);
  });

  it("GH'te yoksa Diyanet'e duser (login + fetch)", async () => {
    mockFetch({
      "test.github.io": new Response("not found", { status: 404 }),
      "Auth/Login": jsonResponse({
        success: true,
        data: { accessToken: "tok", refreshToken: "ref" },
      }),
      "/api/Place/Countries": jsonResponse({
        success: true,
        data: [{ Id: 1, Name: "X" }],
      }),
    });

    const resp = await SELF.fetch("https://worker/api/countries");
    expect(resp.status).toBe(200);
    const body = await resp.json();
    expect(body._source).toBe("diyanet");
  });

  it("Diyanet 401 dondurunce refresh dener", async () => {
    let attempt = 0;
    mockFetch({
      "test.github.io": new Response("404", { status: 404 }),
      "Auth/Login": jsonResponse({ success: true, data: { accessToken: "t1", refreshToken: "r1" } }),
      "Auth/RefreshToken": jsonResponse({ success: true, data: { accessToken: "t2", refreshToken: "r2" } }),
      "/api/Place/Countries": () => {
        attempt++;
        if (attempt === 1) return new Response("unauth", { status: 401 });
        return jsonResponse({ success: true, data: [{ Id: 1, Name: "OK" }] });
      },
    });

    const resp = await SELF.fetch("https://worker/api/countries");
    expect(resp.status).toBe(200);
    const body = await resp.json();
    expect(body.data).toHaveLength(1);
    expect(attempt).toBe(2);
  });
});

describe("Token KV'de saklaniyor", () => {
  it("login sonrasi token KV'ye yaziliyor", async () => {
    mockFetch({
      "test.github.io": new Response("404", { status: 404 }),
      "Auth/Login": jsonResponse({ success: true, data: { accessToken: "tok-123", refreshToken: "r" } }),
      "/api/Place/Countries": jsonResponse({ success: true, data: [] }),
    });

    await SELF.fetch("https://worker/api/countries");

    const stored = await env.TOKEN_KV.get("diyanet:token");
    expect(stored).toBeTruthy();
    const parsed = JSON.parse(stored);
    expect(parsed.accessToken).toBe("tok-123");
  });

  it("ikinci istek KV'deki token'i kullanir, tekrar login olmaz", async () => {
    await env.TOKEN_KV.put(
      "diyanet:token",
      JSON.stringify({
        accessToken: "cached-tok",
        refreshToken: "cached-ref",
        expiresAt: Date.now() + 10 * 60 * 1000,
      })
    );

    let loginCalls = 0;
    mockFetch({
      "test.github.io": new Response("404", { status: 404 }),
      "Auth/Login": () => {
        loginCalls++;
        return jsonResponse({ success: true, data: { accessToken: "new", refreshToken: "new" } });
      },
      "/api/Place/Countries": jsonResponse({ success: true, data: [] }),
    });

    await SELF.fetch("https://worker/api/countries");
    expect(loginCalls).toBe(0);
  });
});

describe("Rate limiting", () => {
  it("limit asilinca 429 doner", async () => {
    mockFetch({
      "test.github.io": new Response("404", { status: 404 }),
      "Auth/Login": jsonResponse({ success: true, data: { accessToken: "t", refreshToken: "r" } }),
      "/api/Place/Countries": jsonResponse({ success: true, data: [] }),
    });

    // KV'ye yapay olarak limit asilmis say degeri koy
    const windowKey = `rl:1.2.3.4:${Math.floor(Date.now() / 1000 / 60)}`;
    await env.RATE_LIMIT_KV.put(windowKey, "1000");

    const resp = await SELF.fetch("https://worker/api/countries", {
      headers: { "CF-Connecting-IP": "1.2.3.4" },
    });
    expect(resp.status).toBe(429);
  });
});

describe("Hata senaryolari", () => {
  it("credential yoksa hata yutar, null akar, 502 doner", async () => {
    // env override KV'ye yazilamayacak token uretir
    mockFetch({
      "test.github.io": new Response("404", { status: 404 }),
      "Auth/Login": new Response("nope", { status: 500 }),
    });

    const resp = await SELF.fetch("https://worker/api/countries");
    // Login basarisiz -> token null -> data null -> 502
    expect([502, 500]).toContain(resp.status);
  });

  it("GH Pages 500 verirse Diyanet'e fallback yapar", async () => {
    let diyanetCalled = false;
    mockFetch({
      "test.github.io": new Response("server error", { status: 500 }),
      "Auth/Login": jsonResponse({ success: true, data: { accessToken: "t", refreshToken: "r" } }),
      "/api/Place/Countries": () => {
        diyanetCalled = true;
        return jsonResponse({ success: true, data: [{ Id: 1, Name: "OK" }] });
      },
    });

    const resp = await SELF.fetch("https://worker/api/countries");
    expect(resp.status).toBe(200);
    expect(diyanetCalled).toBe(true);
  });
});

describe("CORS", () => {
  it("normal yanitlarda da CORS basliklari var", async () => {
    mockFetch({
      "test.github.io": jsonResponse({ success: true, data: [] }),
    });
    const resp = await SELF.fetch("https://worker/api/countries");
    expect(resp.headers.get("Access-Control-Allow-Origin")).toBe("*");
  });
});
