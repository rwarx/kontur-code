import type { CanvasEdge, CanvasNode, DemoFile, Provider, Session } from "./types";

/* ============================================================
   Workspace demo project — AuthFlow (bug: refresh token never
   renewed; users are signed out after 1 hour)
   ============================================================ */

export const WORKSPACE_NAME = "AuthFlow";

const f = (path: string, language: DemoFile["language"], content: string): DemoFile => ({
  path,
  language,
  content: content.trimStart(),
});

export const DEMO_FILES: DemoFile[] = [
  f(
    "README.md",
    "markdown",
    `# AuthFlow

A compact authentication sample: sign-in, access tokens, refresh tokens.

- **AuthFlow.Domain** — entities and contracts
- **AuthFlow.Application** — token issuing and session services
- **AuthFlow.Infrastructure** — clock, token store, JWT issuer
- **AuthFlow.Tests** — xUnit tests for the token flow

\`\`\`
dotnet build && dotnet test
\`\`\`
`,
  ),
  f(
    "src/AuthFlow.Domain/Entities/Account.cs",
    "csharp",
    `namespace AuthFlow.Domain.Entities;

public sealed record Account(
    Guid Id,
    string Email,
    string PasswordHash,
    IReadOnlyList<string> Roles);
`,
  ),
  f(
    "src/AuthFlow.Domain/Entities/RefreshToken.cs",
    "csharp",
    `namespace AuthFlow.Domain.Entities;

public sealed record RefreshToken(
    string Value,
    Guid AccountId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Claims);
`,
  ),
  f(
    "src/AuthFlow.Domain/Entities/TokenPair.cs",
    "csharp",
    `namespace AuthFlow.Domain.Entities;

public sealed record TokenPair(string AccessToken, string RefreshToken);
`,
  ),
  f(
    "src/AuthFlow.Domain/Interfaces/ITokenService.cs",
    "csharp",
    `using AuthFlow.Domain.Entities;

namespace AuthFlow.Domain.Interfaces;

public interface ITokenService
{
    TokenPair IssueTokens(Account account);
    TokenPair? Renew(string refreshTokenValue);
}
`,
  ),
  f(
    "src/AuthFlow.Application/Services/TokenService.cs",
    "csharp",
    `using AuthFlow.Domain.Entities;
using AuthFlow.Domain.Interfaces;

namespace AuthFlow.Application.Services;

public sealed class TokenService : ITokenService
{
    private readonly IClock _clock;
    private readonly ITokenStore _store;
    private readonly TimeSpan _accessLifetime = TimeSpan.FromHours(1);

    public TokenService(IClock clock, ITokenStore store)
    {
        _clock = clock;
        _store = store;
    }

    public TokenPair IssueTokens(Account account)
    {
        var now = _clock.UtcNow;
        var access = Jwt.Issue(account, now, _accessLifetime);

        var refresh = new RefreshToken(
            value: Secure.Random(256),
            accountId: account.Id,
            issuedAt: now,
            expiresAt: now.Add(_accessLifetime));   // ← refresh lives 1 hour, same as access

        _store.Save(refresh);
        return new TokenPair(access, refresh.Value);
    }

    public TokenPair? Renew(string refreshTokenValue)
    {
        var existing = _store.Find(refreshTokenValue);
        if (existing is null || existing.ExpiresAt <= _clock.UtcNow)
            return null;

        var account = _store.AccountFor(existing);
        var next = Secure.Random(256);

        // ← old token is never invalidated, claims are dropped
        return new TokenPair(Jwt.Issue(account, _clock.UtcNow, _accessLifetime), next);
    }
}
`,
  ),
  f(
    "src/AuthFlow.Application/Services/AuthService.cs",
    "csharp",
    `using AuthFlow.Domain.Entities;
using AuthFlow.Domain.Interfaces;

namespace AuthFlow.Application.Services;

public sealed class AuthService
{
    private readonly ITokenService _tokens;
    private readonly IAccountRepository _accounts;
    private readonly ISessionEventSink _events;

    public AuthService(
        ITokenService tokens,
        IAccountRepository accounts,
        ISessionEventSink events)
    {
        _tokens = tokens;
        _accounts = accounts;
        _events = events;
    }

    public AuthResult SignIn(string email, string password)
    {
        var account = _accounts.FindByEmail(email);
        if (account is null || !Passwords.Verify(password, account.PasswordHash))
            return AuthResult.InvalidCredentials;

        var pair = _tokens.IssueTokens(account);
        return AuthResult.Ok(pair);
    }

    public AuthResult EnsureSession(TokenPair pair)
    {
        if (Jwt.IsValid(pair.AccessToken))
            return AuthResult.Ok(pair);

        return AuthResult.SessionExpired;   // ← no renewal is ever attempted
    }
}
`,
  ),
  f(
    "src/AuthFlow.Infrastructure/Clock/SystemClock.cs",
    "csharp",
    `namespace AuthFlow.Infrastructure.Clock;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
`,
  ),
  f(
    "tests/AuthFlow.Tests/TokenServiceTests.cs",
    "csharp",
    `using AuthFlow.Application.Services;

namespace AuthFlow.Tests;

public sealed class TokenServiceTests
{
    [Fact]
    public void IssueTokens_ShouldGrantRefreshForThirtyDays()
    {
        var service = NewService();
        var pair = service.IssueTokens(TestData.Account);

        var refresh = service.Store.Find(pair.RefreshToken);
        Assert.NotNull(refresh);
        Assert.True(refresh.ExpiresAt > service.Clock.UtcNow.AddDays(29));
    }

    [Fact]
    public void Renew_ShouldRotateAndPreserveClaims()
    {
        var service = NewService();
        var pair = service.IssueTokens(TestData.Account);

        var renewed = service.Renew(pair.RefreshToken);

        Assert.NotNull(renewed);
        Assert.NotEqual(pair.RefreshToken, renewed.RefreshToken);
        Assert.Null(service.Store.Find(pair.RefreshToken));      // rotated
        Assert.Equal("afi", renewed.Claims["afi"]);              // preserved
    }

    [Fact]
    public void Renew_Expired_ShouldReturnNull()
    {
        var service = NewService(expired: true);
        var pair = service.IssueTokens(TestData.Account);

        Assert.Null(service.Renew(pair.RefreshToken));
    }
}
`,
  ),
];

/* The fixed contents the agent produces during the demo (applied by edit_file) */

export const FIX_TOKENSERVICE_FIND = `        var refresh = new RefreshToken(
            value: Secure.Random(256),
            accountId: account.Id,
            issuedAt: now,
            expiresAt: now.Add(_accessLifetime));   // ← refresh lives 1 hour, same as access`;

export const FIX_TOKENSERVICE_REPLACE = `        var refresh = new RefreshToken(
            value: Secure.Random(256),
            accountId: account.Id,
            issuedAt: now,
            expiresAt: now.Add(_refreshLifetime));`

export const FIX_TOKENSERVICE_FIND2 = `        var account = _store.AccountFor(existing);
        var next = Secure.Random(256);

        // ← old token is never invalidated, claims are dropped
        return new TokenPair(Jwt.Issue(account, _clock.UtcNow, _accessLifetime), next);`

export const FIX_TOKENSERVICE_REPLACE2 = `        var account = _store.AccountFor(existing);
        var next = new RefreshToken(
            value: Secure.Random(256),
            accountId: account.Id,
            issuedAt: _clock.UtcNow,
            expiresAt: _clock.UtcNow.Add(_refreshLifetime),
            claims: existing.Claims);               // afi survives the rotation

        _store.Invalidate(existing);                // rotation: old value dies
        return new TokenPair(Jwt.Issue(account, _clock.UtcNow, _accessLifetime), next.Value);`

export const FIX_TOKENSERVICE_LIFETIME_FIND = `    private readonly TimeSpan _accessLifetime = TimeSpan.FromHours(1);`
export const FIX_TOKENSERVICE_LIFETIME_REPLACE = `    private readonly TimeSpan _accessLifetime = TimeSpan.FromHours(1);
    private readonly TimeSpan _refreshLifetime = TimeSpan.FromDays(30);`

export const FIX_AUTH_FIND = `    public AuthResult EnsureSession(TokenPair pair)
    {
        if (Jwt.IsValid(pair.AccessToken))
            return AuthResult.Ok(pair);

        return AuthResult.SessionExpired;   // ← no renewal is ever attempted
    }`

export const FIX_AUTH_REPLACE = `    public AuthResult EnsureSession(TokenPair pair)
    {
        if (Jwt.IsValid(pair.AccessToken))
            return AuthResult.Ok(pair);

        var renewed = _tokens.Renew(pair.RefreshToken);
        if (renewed is not null)
        {
            _events.Raise(SessionEvent.Renewed);
            return AuthResult.Ok(renewed);
        }

        return AuthResult.SessionExpired;
    }`

/* ============================================================
   Initial canvas graph — architecture map of AuthFlow
   ============================================================ */

const n = (
  id: string,
  kind: CanvasNode["kind"],
  title: string,
  x: number,
  y: number,
  meta?: string,
  path?: string,
): CanvasNode => ({ id, kind, title, x, y, w: 208, h: 62, meta, path });

export const INITIAL_NODES: CanvasNode[] = [
  { ...n("sol", "module", "AuthFlow.sln", 0, 0, "solution · 4 projects"), w: 232 },
  { ...n("dom", "module", "AuthFlow.Domain", -470, 170, "entities + contracts"), w: 232 },
  { ...n("app", "module", "AuthFlow.Application", 30, 170, "services"), w: 244 },
  { ...n("infra", "module", "AuthFlow.Infrastructure", 530, 170, "clock · store · jwt"), w: 262 },
  n("account", "data", "Account", -740, 350, "record", "src/AuthFlow.Domain/Entities/Account.cs"),
  n("refresh", "data", "RefreshToken", -500, 350, "record", "src/AuthFlow.Domain/Entities/RefreshToken.cs"),
  n("tokenpair", "data", "TokenPair", -260, 350, "record", "src/AuthFlow.Domain/Entities/TokenPair.cs"),
  n("its", "interface", "ITokenService", -500, 520, "contract", "src/AuthFlow.Domain/Interfaces/ITokenService.cs"),
  n("tokensvc", "service", "TokenService", -30, 350, "issues + renews", "src/AuthFlow.Application/Services/TokenService.cs"),
  n("authsvc", "service", "AuthService", 220, 350, "sign-in + session", "src/AuthFlow.Application/Services/AuthService.cs"),
  n("clock", "service", "SystemClock", 530, 350, "IClock", "src/AuthFlow.Infrastructure/Clock/SystemClock.cs"),
  n("tests", "test", "TokenServiceTests", 530, 520, "3 facts", "tests/AuthFlow.Tests/TokenServiceTests.cs"),
];

const e = (
  id: string,
  from: string,
  to: string,
  kind: CanvasEdge["kind"],
  note?: string,
): CanvasEdge => ({ id, from, to, kind, note });

export const INITIAL_EDGES: CanvasEdge[] = [
  e("e1", "sol", "dom", "contains"),
  e("e2", "sol", "app", "contains"),
  e("e3", "sol", "infra", "contains"),
  e("e4", "dom", "account", "contains"),
  e("e5", "dom", "refresh", "contains"),
  e("e6", "dom", "tokenpair", "contains"),
  e("e7", "dom", "its", "contains"),
  e("e8", "app", "dom", "depends", "Compile-time only — no runtime reach-in"),
  e("e9", "infra", "dom", "depends"),
  e("e10", "tokensvc", "its", "implements"),
  e("e11", "authsvc", "tokensvc", "calls", "Login() → CreateTokenAsync"),
  e("e12", "tests", "tokensvc", "depends"),
  e("e13", "tokensvc", "refresh", "relates"),
];

/* ============================================================
   Providers & model catalogue (served from the local cache)
   ============================================================ */

const m = (
  id: string,
  name: string,
  providerId: string,
  contextK: number,
  extra: Partial<Provider["models"][number]> = {},
): Provider["models"][number] => ({
  id,
  name,
  providerId,
  contextK,
  tools: true,
  ...extra,
});

export const PROVIDERS: Provider[] = [
  {
    id: "openrouter",
    name: "OpenRouter",
    endpoint: "https://openrouter.ai/api/v1",
    state: "connected",
    statusMessage: "Connected · 12 models cached",
    builtin: true,
    models: [
      m("anthropic/claude-sonnet-4.5", "Claude Sonnet 4.5", "openrouter", 200, { vision: true, pricePrompt: 3 }),
      m("openai/gpt-5", "GPT-5", "openrouter", 400, { pricePrompt: 1.25 }),
      m("openai/gpt-4.1", "GPT-4.1", "openrouter", 1000, { pricePrompt: 2 }),
      m("google/gemini-2.5-pro", "Gemini 2.5 Pro", "openrouter", 1000, { vision: true, pricePrompt: 1.25 }),
      m("qwen/qwen3-max", "Qwen3 Max", "openrouter", 262, { pricePrompt: 0.6 }),
      m("deepseek/deepseek-v3.2", "DeepSeek V3.2", "openrouter", 164, { pricePrompt: 0.28 }),
      m("mistral/mistral-large-2", "Mistral Large 2", "openrouter", 128, { pricePrompt: 2 }),
    ],
  },
  {
    id: "nvidia",
    name: "NVIDIA NIM",
    endpoint: "https://integrate.api.nvidia.com/v1",
    state: "connected",
    statusMessage: "Connected · 3 models cached",
    builtin: true,
    models: [
      m("nvidia/llama-3.3-nemotron-super-49b", "Llama 3.3 Nemotron Super 49B", "nvidia", 128, { pricePrompt: 0.4 }),
      m("nvidia/nemotron-nano-9b", "Nemotron Nano 9B", "nvidia", 128, { pricePrompt: 0.04 }),
      m("nvidia/gpt-oss-120b", "GPT-OSS 120B", "nvidia", 131, { pricePrompt: 0.15 }),
    ],
  },
  {
    id: "openai",
    name: "OpenAI",
    endpoint: "https://api.openai.com/v1",
    state: "missing-key",
    builtin: true,
    models: [
      m("gpt-5", "GPT-5", "openai", 400, { pricePrompt: 1.25 }),
      m("gpt-4.1", "GPT-4.1", "openai", 1000, { pricePrompt: 2 }),
      m("o3", "o3", "openai", 200, { kind: "reasoner", pricePrompt: 2 }),
    ],
  },
  {
    id: "anthropic",
    name: "Anthropic",
    endpoint: "https://api.anthropic.com/v1",
    state: "missing-key",
    builtin: true,
    models: [
      m("claude-sonnet-4-5", "Claude Sonnet 4.5", "anthropic", 200, { vision: true, pricePrompt: 3 }),
      m("claude-opus-4-1", "Claude Opus 4.1", "anthropic", 200, { vision: true, pricePrompt: 15 }),
    ],
  },
  {
    id: "groq",
    name: "Groq",
    endpoint: "https://api.groq.com/openai/v1",
    state: "missing-key",
    builtin: true,
    models: [m("llama-3.3-70b-versatile", "Llama 3.3 70B Versatile", "groq", 128, { pricePrompt: 0.59 })],
  },
  {
    id: "xai",
    name: "xAI",
    endpoint: "https://api.x.ai/v1",
    state: "missing-key",
    builtin: true,
    models: [m("grok-4", "Grok 4", "xai", 256, { pricePrompt: 3 })],
  },
  {
    id: "mistral",
    name: "Mistral",
    endpoint: "https://api.mistral.ai/v1",
    state: "missing-key",
    builtin: true,
    models: [m("mistral-large-latest", "Mistral Large", "mistral", 128, { pricePrompt: 2 })],
  },
  {
    id: "deepseek",
    name: "DeepSeek",
    endpoint: "https://api.deepseek.com/v1",
    state: "missing-key",
    builtin: true,
    models: [
      m("deepseek-chat", "DeepSeek Chat", "deepseek", 128, { pricePrompt: 0.27 }),
      m("deepseek-reasoner", "DeepSeek Reasoner", "deepseek", 128, { kind: "reasoner", pricePrompt: 0.55 }),
    ],
  },
];

export const DEFAULT_MODEL_ID = "anthropic/claude-sonnet-4.5";

export function findModel(modelId: string): Provider["models"][number] | undefined {
  for (const p of PROVIDERS) {
    const found = p.models.find((x) => x.id === modelId);
    if (found) return found;
  }
  return undefined;
}

/* ============================================================
   Connect-provider presets (opencode-breadth catalogue).
   `builtinId` set  → a first-class server provider: connect by key.
   `builtinId` unset → OpenAI-compatible endpoint added via
   `providers.addCustom(name, baseUrl)`, then keyed like any other.
   ============================================================ */

export interface ConnectPreset {
  builtinId?: string;
  name: string;
  baseUrl: string;
  apiKeyUrl?: string;
  /** localhost endpoint — usually needs no key */
  local?: boolean;
}

export const CONNECT_PRESETS: ConnectPreset[] = [
  /* built-ins (map to the sidecar's first-class providers) */
  { builtinId: "openrouter", name: "OpenRouter", baseUrl: "https://openrouter.ai/api/v1", apiKeyUrl: "https://openrouter.ai/keys" },
  { builtinId: "anthropic", name: "Anthropic", baseUrl: "https://api.anthropic.com/v1", apiKeyUrl: "https://console.anthropic.com/settings/keys" },
  { builtinId: "openai", name: "OpenAI", baseUrl: "https://api.openai.com/v1", apiKeyUrl: "https://platform.openai.com/api-keys" },
  { builtinId: "nvidia", name: "NVIDIA NIM", baseUrl: "https://integrate.api.nvidia.com/v1", apiKeyUrl: "https://build.nvidia.com/" },
  { builtinId: "groq", name: "Groq", baseUrl: "https://api.groq.com/openai/v1", apiKeyUrl: "https://console.groq.com/keys" },
  { builtinId: "xai", name: "xAI", baseUrl: "https://api.x.ai/v1", apiKeyUrl: "https://console.x.ai/" },
  { builtinId: "mistral", name: "Mistral", baseUrl: "https://api.mistral.ai/v1", apiKeyUrl: "https://console.mistral.ai/api-keys/" },
  { builtinId: "deepseek", name: "DeepSeek", baseUrl: "https://api.deepseek.com/v1", apiKeyUrl: "https://platform.deepseek.com/api_keys" },
  /* OpenAI-compatible endpoints (added as custom providers) */
  { name: "Google Gemini", baseUrl: "https://generativelanguage.googleapis.com/v1beta/openai/", apiKeyUrl: "https://aistudio.google.com/apikey" },
  { name: "Together AI", baseUrl: "https://api.together.xyz/v1", apiKeyUrl: "https://api.together.xyz/settings/api-keys" },
  { name: "Fireworks AI", baseUrl: "https://api.fireworks.ai/inference/v1", apiKeyUrl: "https://fireworks.ai/account/api-keys" },
  { name: "Perplexity", baseUrl: "https://api.perplexity.ai", apiKeyUrl: "https://www.perplexity.ai/settings/api" },
  { name: "Cerebras", baseUrl: "https://api.cerebras.ai/v1", apiKeyUrl: "https://cloud.cerebras.ai/" },
  { name: "DeepInfra", baseUrl: "https://api.deepinfra.com/v1/openai", apiKeyUrl: "https://deepinfra.com/dash/api_keys" },
  { name: "Hugging Face", baseUrl: "https://router.huggingface.co/v1", apiKeyUrl: "https://huggingface.co/settings/tokens" },
  { name: "Ollama", baseUrl: "http://localhost:11434/v1", local: true },
  { name: "LM Studio", baseUrl: "http://localhost:1234/v1", local: true },
];

/* ============================================================
   Skills (Settings → Agent)
   ============================================================ */

export const SKILLS: { id: string; name: string; description: string; enabled: boolean }[] = [
  { id: "dotnet", name: "dotnet", description: "Solution layout, xUnit patterns, nullable annotations", enabled: true },
  { id: "testing", name: "testing", description: "Test-first fixes: write the failing fact, then repair", enabled: true },
  { id: "code-review", name: "code-review", description: "Diff hygiene, naming, seams for tests", enabled: true },
  { id: "architecture", name: "architecture", description: "Domain/Application/Infrastructure boundaries", enabled: false },
  { id: "security", name: "security", description: "Token handling, secrets, OWASP checkpoints", enabled: true },
  { id: "database", name: "database", description: "EF Core migrations, SQLite specifics", enabled: false },
];

/* ============================================================
   Seed sessions (sidebar history)
   ============================================================ */

const now = Date.now();

export const SEED_SESSIONS: Session[] = [
  {
    id: "seed-1",
    title: "Explain the graph indexer",
    createdAt: now - 3 * 86400000,
    updatedAt: now - 3 * 86400000 + 400000,
    preview: "How does WorkspaceGraphIndexer rebuild edges on refresh?",
    messages: [
      {
        id: "seed-1-m1",
        role: "user",
        content: "How does WorkspaceGraphIndexer rebuild edges on refresh?",
        createdAt: now - 3 * 86400000,
        toolCalls: [],
      },
      {
        id: "seed-1-m2",
        role: "assistant",
        content:
          "It walks the folder twice: first collecting nodes with stable ids (path hashes), then resolving references into `Depends` / `Contains` edges. Positions are preserved across refreshes by matching node ids, so a re-index never scrambles your layout.",
        createdAt: now - 3 * 86400000 + 400000,
        toolCalls: [],
      },
    ],
    events: [],
    goalIds: [],
    modelId: DEFAULT_MODEL_ID,
  },
  {
    id: "seed-2",
    title: "Draft README for AuthFlow",
    createdAt: now - 86400000,
    updatedAt: now - 86400000 + 300000,
    pinned: true,
    preview: "Draft a README with build and test instructions",
    messages: [
      {
        id: "seed-2-m1",
        role: "user",
        content: "Draft a README with build and test instructions",
        createdAt: now - 86400000,
        toolCalls: [],
      },
      {
        id: "seed-2-m2",
        role: "assistant",
        content:
          "Done — `README.md` now lists the four projects, the build/test commands and a short architecture note. Kept it under 30 lines so it stays maintainable.",
        createdAt: now - 86400000 + 300000,
        toolCalls: [],
      },
    ],
    events: [],
    goalIds: [],
    modelId: DEFAULT_MODEL_ID,
  },
];
