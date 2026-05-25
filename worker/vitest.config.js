import { defineWorkersConfig } from "@cloudflare/vitest-pool-workers/config";

export default defineWorkersConfig({
  test: {
    poolOptions: {
      workers: {
        wrangler: { configPath: "./wrangler.toml" },
        miniflare: {
          kvNamespaces: ["TOKEN_KV", "RATE_LIMIT_KV"],
          bindings: {
            DIYANET_EMAIL: "test@example.com",
            DIYANET_PASSWORD: "test-pass",
            GITHUB_PAGES_BASE: "https://test.github.io/repo/data",
            CACHE_TTL_SECONDS: "60",
            LOG_LEVEL: "silent",
          },
        },
      },
    },
  },
});
