# Emissary — Development Roadmap

> **Emissary** — production-grade Claude agents for .NET. AOT-compiled, zero-trust, observable.
>
> Claude-native by design (not provider-agnostic). Consumes Claude exclusively; is consumable
> universally via `IChatClient` and MCP. Thesis: **determinism, provability, reversibility** —
> agents you can put in front of an auditor.

## Ground rules (apply to every phase)

- **Target framework:** net10.0 only (current LTS; .NET 9 is EOL, .NET 8 EOL Nov 2026 — before v1 ships).
  Modern C# first: C# 14 (`LangVersion latest`) — extension members, `field` properties, `params
  ReadOnlySpan<T>`, `allows ref struct`, `System.Threading.Lock`. Add net11.0 later only if a feature
  earns it. `IsAotCompatible=true`, nullable enabled, warnings as errors.
- **Testing:** TUnit on Microsoft.Testing.Platform. `Verify` snapshot tests for source-generator output.
- **Coverage:** 100% line + branch on shipped libraries, enforced in CI (ReportGenerator threshold gate).
  Carve-outs: generated code, and `[ExcludeFromCodeCoverage]` with a mandatory justification comment.
- **Mutation testing:** intended quality guard on top of coverage — coverage proves code ran;
  mutation score proves tests would notice a break. Security-policy branches (Phase 5) require
  near-100% kill rate. *Not in the repo yet: Stryker.NET cannot run Microsoft.Testing.Platform/TUnit
  tests (stryker-mutator/stryker-net#3094). Re-adopt when that lands; see ADR 0003.*
- **Dependencies:** official `Anthropic` NuGet SDK behind an internal stable seam (SDK is beta — its
  majors must not become our majors). Official `ModelContextProtocol` SDK for MCP.
- **Never build:** vector DBs / RAG pipelines, generic multi-agent orchestration (that is Microsoft
  Agent Framework's turf — interop instead), visual designers, own eval-judge models, own rate
  limiter (`System.Threading.RateLimiting` exists).
- **Samples are documentation that cannot rot:** every sample in `samples/` builds in CI on every
  commit (AOT-published where meaningful); a breaking API change that breaks a sample fails the
  build. Samples are excluded from coverage/mutation gates. Each sample ships its own README.
  Each phase's exit criteria include its samples landing (see Samples plan below).

---

## Phase 0 — Foundation

Repo skeleton, solution layout, `Directory.Build.props`, MIT license, NuGet metadata + `Emissary.*`
prefix reservation. GitHub Actions CI: build → TUnit → coverage collect → ReportGenerator → fail
under 100%. ADR folder; first entries: "Claude-native, not agnostic",
"TUnit + MTP", "coverage policy".

**Exit:** empty-but-green pipeline where a single uncovered line fails the build.

## Phase 1 — Source generator (crown jewel first)

- `[ClaudeTool]` → compile-time JSON schema + AOT-safe typed dispatcher. Zero reflection.
- Typed structured outputs: `RunAsync<T>` emitting strict schemas at compile time.
- Roslyn analyzer diagnostics: `EMS001` empty tool description, `EMS002` non-schema-representable
  parameter, `EMS003` unbounded return without truncation strategy (more over time).

**Testing:** Roslyn SDK harness + Verify snapshots for every emission path; each diagnostic has
dedicated tests; a Native AOT sample app compiled **and executed** in CI as the trim regression test.

**Exit:** console app defines 3 attributed tools, publishes Native AOT, emits correct wire schemas.

## Phase 2 — Core runtime

Agent loop over the Anthropic SDK: immutable conversation state, `IAsyncEnumerable` streaming
(thinking blocks included), adaptive thinking / effort config, retry + rate-limit handling,
refusal-fallback wiring, DI-first (`services.AddEmissary(...)`). SDK behind an internal transport
seam — the hook that makes everything testable offline and that Phase 3 formalizes.

**Testing:** every loop path (tool call, parallel tools, refusal, max-tokens, budget stop) via fake
transport with TUnit `[Arguments]` cases; small opt-in live smoke suite (outside coverage gates).

**Exit:** multi-turn streaming agent with tools, 100% green with zero network access.

## Phase 3 — Record/replay + Emissary.Testing (dogfood milestone)

- `.trajectory` file format, recorder, replayer.
- **`Emissary.Testing`**: TUnit-native trajectory assertions, e.g.
  `AssertToolCalledOnce("refund_payment")`, `AssertNever("close_ticket", before: "verify_identity")`.
- Dogfood: Emissary's own integration tests convert to recorded trajectories.

**Exit:** CI replays a golden trajectory suite deterministically; model-upgrade canary report runs.

## Phase 4 — Context, cache, and cost

Cache-aware prompt assembly (automatic `cache_control` placement, stable prefix ordering,
invalidation detection with runtime warnings), compaction / context-editing lifecycle, token budgets
(`task_budget`) + local cost accounting, full OTel GenAI semantic conventions, Aspire dashboard sample.

*Status: automatic cache breakpoints (tools/system/latest message), cache-usage accounting, local
token budgets, and OTel GenAI spans + metrics landed. Deferred to a follow-up: server-side
compaction lifecycle (needs the beta client path), server `task_budget`, cache-invalidation
analyzer warnings, and sample 05.*

**Exit:** long-running demo agent holds a high cache-read ratio and survives past the context window;
all spans/metrics visible in Aspire.

## Phase 5 — Contracts and safety (the thesis features)

- Tool-call state machines: fluent graph → source-generated runtime guard + replay assertions.
- `[AuthorizeTool]` RBAC: dual identity (OBO / service), pre-prompt schema filtering, audit log.
- Taint tracking: untrusted tool output taints the turn; tainted turns cannot invoke privileged
  tools without a human gate (prompt-injection defense as information-flow control).
- Shadow mode (plan-of-effects for approval); compensation sagas may trail into 5.5.

*Status: COMPLETE except source-generated contract declarations (runtime ToolRules cover the
semantics; a compile-time fluent declaration may come later). Landed across 5 and 5.5: tool-call
contracts (Require/Terminal/Limit), [AuthorizeTool] pre-prompt schema filtering, taint tracking,
shadow mode (ExecutionMode.Shadow → PlannedEffects), compensation sagas
(CompensatedBy → ClaudeAgent.CompensateAsync, reverse order, shadow-aware), and sample 06 —
the auditor demo replays offline and its audit is a test assertion.*

**Exit:** the auditor demo — injected instruction in tainted web content provably fails to trigger a
privileged tool, captured in a replayable trajectory.

## Phase 6 — Hosting and composition

`app.MapEmissaryAgent(...)` SSE endpoint; human-in-the-loop gates including durable multi-day pause
(state-store abstraction + SQLite provider, resume via webhook); agent-as-MCP-server.

*Status: COMPLETE. 6a — Emissary.AspNetCore (MapEmissaryAgent SSE + MapEmissaryApprovals webhook),
durable suspend/resume in core (AgentOptions.ApprovalRequired → SuspendedRun with serialized guard
state; ClaudeAgent.ResumeAsync), IAgentStateStore + in-memory provider, sample 07. 6b —
Emissary.Mcp agent-as-MCP-server + sample 08, Emissary.Sqlite durable state store, and the
Native AOT Dockerfile for sample 07. Note: the MCP server is a minimal hand-rolled stdio
implementation (initialize / tools/list / tools/call over newline-delimited JSON-RPC) rather than
the official ModelContextProtocol SDK — the SDK is preview and reflection-based, which conflicts
with the AOT guarantee; revisit when it is AOT-ready.*

**Exit:** one sample deploys as an AOT container: HTTP in, SSE out, approval webhook resumes a run.

## Phase 7 — v1.0 launch

Docs site, three polished samples (console, ASP.NET Core, Aspire), published BenchmarkDotNet numbers
(binary size, cold start, memory per conversation — vs. MAF equivalent), NuGet release, announcement.

## Samples plan (`samples/`)

Numbered in learning order — a reader should be able to walk 01 → 09 and end up an expert.
Each lands with the phase that makes it buildable and becomes part of that phase's exit criteria.

| Sample | Shows | Lands in |
|---|---|---|
| `01-HelloTool` | Minimal console agent: one `[ClaudeTool]`, Native AOT publish, zero reflection | Phase 2 |
| `02-StreamingChat` | Multi-turn streaming incl. thinking blocks; adaptive thinking + effort config | Phase 2 |
| `03-TypedOutputs` | `RunAsync<T>` structured outputs with compile-time strict schemas | Phase 2 |
| `04-RecordReplay` | Record a `.trajectory`, replay it in a TUnit test with trajectory assertions | Phase 3 |
| `05-SupportAgent` | Flagship end-to-end: cache-aware assembly, budgets/cost accounting, compaction, OTel | Phase 4 |
| `06-ZeroTrustAgent` | The auditor demo: `[AuthorizeTool]` RBAC, taint tracking, tool state machine, shadow mode | Phase 5 |
| `07-WebApi` | ASP.NET Core SSE hosting, HITL gate with durable pause + webhook resume, AOT container | Phase 6 |
| `08-McpServer` | Agent-as-MCP-server, callable from Claude Code / Claude Desktop | Phase 6 |
| `09-AspireDashboard` | Aspire app model: live traces, token flow, cache hit rate on the dashboard | Phase 7 |

Conventions: every sample runs against `ANTHROPIC_API_KEY` from the environment (never hardcoded);
each defaults to the current recommended Claude model via one shared constant; 04–06 must run fully
offline via replay so readers without an API key still get a working experience.

## Post-1.0 backlog (in order)

1. Model-upgrade canarying as a product (golden suites vs. new models, Batch API overnight runs).
2. Orleans distributed hosting (`Emissary.Orleans`).
3. NuGet-as-skill-marketplace conventions (`Emissary.Skills.*`).
4. `Emissary.RedTeam` adversarial simulation personas.
5. Managed Agents bridge (write once, self-host or Anthropic-hosted).
6. Edge agents (offline queue + sync).
7. Conversation branching; speculative tool warm-up.

---

*Sequencing logic: the testing story (Phase 3) lands before the features that need guarding
(Phases 4–5) — replay is both a headline feature and our own test infrastructure. Dogfooding flywheel.*
