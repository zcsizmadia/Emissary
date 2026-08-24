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

*Status: COMPLETE. Automatic cache breakpoints (tools/system/latest message), cache-usage
accounting, local token budgets, OTel GenAI spans + metrics, sample 05, and **client-side context
compaction** (ADR 0006 — chosen over the opaque server-side beta so compacted runs stay
replayable). Still open: server `task_budget` and cache-invalidation analyzer warnings.*

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

*Status: release engineering + flagship README landed — tag-driven MinVer versioning (ADR 0005),
lockstep packages with the generator embedded in the core package, SourceLink + snupkg + package
validation, release.yml (tag → gauntlet → NuGet + GitHub release), CI pack dry-run. Remaining:
NuGet API key + prefix reservation (owner action), first preview tag, BenchmarkDotNet numbers,
sample 09-AspireDashboard, docs site, announcement.*

## Phase 8 — Earn 1.0

Not features: the verification that makes a semver commitment honest. Phases 0–7 built the thing;
this phase proves it works where it has never been run.

- **Ship `0.1.0-preview.3` first.** What is published today has stop reasons collapsing to
  `end_turn` and cancellation that stops nothing. Those fixes are on `main` and should reach anyone
  on preview.2 before 1.0 is even discussed.
- **`live-smoke` green against the release candidate.** The transport was rewritten — stop-reason
  normalization, cancellation lifetime, transient classification, retry bounds — and none of it has
  executed against the real API. [ADR 0008](docs/adr/0008-sdk-boundary-testing.md) exists because
  that exact gap hid two bugs.
- **Re-record `04` and `06` trajectories** on the small model, so every sample takes the cost cap.
- **Settle the API before semver freezes it:** `EmissaryDefaults.Model` from `const` to
  `static readonly` (a `const` is inlined into consumers, which is wrong for a value that exists
  because model ids change), and a last read of the unavoidably-public `ToolArguments` surface.
- **Soak.** A real workload, a few weeks, no new defect of the "silently wrong in production" class.

**Testing:** nothing new — [the release checklist](docs/release-checklist.md) *is* the test.

**Exit:** `v1.0.0` tagged, `PackageValidationBaselineVersion` set, live smoke green on the tag.

## Phase 9 — Inherit the MCP ecosystem (client side)

Emissary is an MCP *server*. The inverse is worth more: consume any MCP server as Emissary tools,
and give the whole MCP ecosystem the thing it has no notion of — enforcement.

```csharp
options.Tools.AddMcpServer("github");                     // untrusted by default
options.Rules.Require("create_pr", prerequisite: "run_tests");
```

- Discovery via `tools/list`, dispatch via `tools/call`, over stdio first.
- **A tool you did not write is untrusted by default** — it is someone else's code reading the
  world, so its output taints the run unless the caller says otherwise. Worth an ADR: this is the
  central tension of the phase, between inheriting tools and guaranteeing behavior.
- Remote tools get the full apparatus: contracts by wire name, RBAC, shadow mode, approval gates,
  per-call timeouts, result caps.
- Defensive binding: we do not own the remote schema, so arguments and results are validated at the
  boundary the way generated binders validate ours.

**Testing:** a fake MCP server over pipes (the existing server tests supply the harness) — malformed
responses, protocol errors, a server that dies mid-call, a tool whose schema disagrees with what it
returns.

**Exit:** a sample using a public MCP server where a taint rule provably blocks a privileged action,
replayable offline.

## Phase 10 — Hermetic replay, and cost as a test

Replay re-executes real tools today, so a tool that touches a database still touches it. And nothing
stops a run from getting more expensive release over release.

- **Tool cassettes:** record tool inputs and outputs alongside the trajectory; on replay, serve the
  recorded result. Modes for record / replay / passthrough, and a mismatch is a divergence like any
  other.
- **Cost regression gate:** cost is computable from recorded usage, so it needs no network —
  `EmissaryAssert.That(result).CostUnder(...)`, and CI failing on drift past a threshold.
- Sequenced here deliberately: it makes every later integration (SQL, browser, MCP) testable without
  side effects, and it is the durable answer to a development loop that spends money.

**Testing:** a cassette for a tool with an observable side effect, asserting the side effect does
**not** happen on replay.

**Exit:** a sample whose tools require a database replays with the database absent.

## Phase 11 — Tools from specifications

The generator's best trick, pointed at the largest available input.

- `[ClaudeToolsFromOpenApi("stripe.json", Prefix = "stripe_")]` emitting tool definitions and
  binders at compile time.
- Safety defaults read from the spec: `GET` becomes an untrusted read, mutating verbs become
  privileged, per-operation overrides available.
- Selection by tag or operation id, because dumping four hundred tools into a prompt is its own
  failure — with a diagnostic when the generated tool count crosses a threshold.

**Testing:** generator snapshot tests over real specs; the schema shapes that OpenAPI allows and
Claude's tool schema does not.

**Exit:** an agent driving a real public API with no hand-written tool code.

## Phase 12 — Make it visible

The engineering is better than the demo, which is a marketing bug.

- `Emissary.Aspire`: the agent as a resource in the app model, with dashboard panels for tokens,
  cost, cache hit rate, and tool latency.
- `Emissary.Blazor`: a streaming chat component over `IAsyncEnumerable<AgentEvent>`, a tool-call
  timeline, and an approval widget wired to the human-in-the-loop gate.
- A Grafana dashboard JSON and an OTel conventions note, so observability is drop-in.

**Exit:** sample `09-AspireDashboard` (already reserved) as the showcase, plus a Blazor sample.

## Phase 13 — Provenance and inference

Turning the audit story from a boolean into an explanation, and observation into enforcement.

- **Taint provenance:** not `Tainted: true` but the path — which untrusted bytes reached which
  decision — as a report an auditor can read.
- **Contract inference:** mine a golden suite and propose rules (*"refund always followed verify
  across 200 runs — add `Require`?"*), emitted as code to paste.
- **Trajectory bisect and a step debugger:** canary says behavior changed; these say which turn.

**Exit:** the auditor demo answers *why* a call was blocked with a path, not a flag.

## Phase 14 — Ecosystem bridges

Each of these is a safety story someone else's ecosystem cannot tell.

- **Semantic Kernel / Microsoft Agent Framework:** bidirectional — their plugins as Emissary tools,
  Emissary tools as their plugins. `IChatClient` is already the beachhead.
- **Playwright:** browser tools where every page read is untrusted automatically. Web-browsing agents
  that provably cannot be injected into spending money.
- **Guarded SQL** (Dapper/EF Core): parameterized-only, statement allow-list, row caps, results
  marked untrusted.
- **Identity-backed RBAC:** `ClaimsPrincipal` to `IToolAuthorizer`, so authorization is real auth
  rather than a policy string.
- **Durable resumption via MassTransit or Hangfire** — their sagas and our compensation are the same
  idea with a model in the loop.

**Exit:** each bridge ships with a sample and its safety defaults on by default.

## Phase 15 — Distribution

- **`emissary-canary` GitHub Action:** run golden suites against a candidate model on a pull request
  and comment the behavioral diff. Every user of it advertises the idea.
- **`Emissary.RedTeam`** (promoted from the backlog): a curated prompt-injection corpus run in CI,
  asserting no privileged tool fires.

**Exit:** this repository's own pull requests gated by the action.

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
| `10-InjectedTools` | Instance tools resolved from DI: scoped dependencies, per-tenant state, direct invocation | post-preview.2 |

Conventions: every sample runs against `ANTHROPIC_API_KEY` from the environment (never hardcoded);
each defaults to the current recommended Claude model via one shared constant; 04–06 must run fully
offline via replay so readers without an API key still get a working experience.

## Shipped since 0.1.0-preview.2

Phases 0–7 are complete; work now lands as one focused feature per PR, each held to the same
100% coverage, AOT, and docs bar.

1. **Tool result truncation** — `[ClaudeTool(MaxResultLength = N)]` keeps an oversized tool result
   from blowing the context window, telling the model what was cut.
2. **Streaming structured outputs** — `StreamAsync<T>` deserializes the partial JSON of an
   in-flight response, so a UI can render a typed object as it is written.
3. **Multi-agent handoff** — `AgentOptions.Handoffs` transfers a whole conversation to another
   agent, carrying accumulated taint across the boundary.
4. **Argument validation in generated binders** — a wrong-typed or unknown-enum argument becomes a
   model-visible error result naming what was expected, instead of an unhandled exception.
5. **Tool failure containment** — `AgentOptions.ToolFailures`: a throwing or hanging tool is
   reported to the model without ending the run, with the exception surfaced to the caller and the
   message withheld from the prompt by default.

6. **Instance tools** — `[ClaudeTool]` on an instance method emits an instance `{Method}Tool`
   property bound to that object, so tools can hold injected dependencies. `EMS005` is retired;
   `EMS012` replaces it, catching a tool and compensator that differ in static-ness.
7. **Contract validation at construction** — an agent whose `Rules` name a tool it does not have
   throws instead of running with a silently unenforceable safety contract.
8. **`MaxParallelTools`** — bounds how many of a turn's tool calls execute at once, so one turn
   cannot drain a connection pool.
9. **Sample 10** — instance tools resolved from DI, runnable with no API key.
10. **SDK-boundary correctness sweep** — an audit of the code that interprets SDK values found
    defects no offline test could see, because the tests asserted against invented values rather
    than SDK-produced ones. Fixed: stop reasons (every one collapsed to `end_turn`, making
    `MaxTokens` and `Refusal` unreachable), `pause_turn` reported as a completion, a tool call
    truncated mid-argument throwing out of the transport, an empty turn poisoning the next request,
    cancellation severed for the body of every stream, connection failures never retried, SDK
    retries multiplying ours, and an MCP server that a JSON-RPC batch could kill. The practice that
    prevents the class is [ADR 0008](docs/adr/0008-sdk-boundary-testing.md).
11. **Suspension and SSE hardening** — a client disconnect no longer loses a suspended run, an
    approval is at-most-once (`DeleteAsync` is an atomic claim), responses are not proxy-buffered,
    and a mid-stream failure emits an `error` event instead of a dead connection.
12. **Failure assertions** — `NoToolFailures()`, `ToolFailed`, `ToolTimedOut`, `Complete()`, because
    a failing tool gets narrated around and an incomplete answer reads like a good one.
13. **Sample cost caps** — a small model, a 50k token budget and six turns for every sample, since
    the defaults are Opus with no ceiling. `04` and `06` keep the recorded model: replay
    verification rejects a mismatch, which is the guard working.
14. **Brand and docs entry points** — the mark, the lockup, a NuGet icon on every package, the
    published site linked from the README at last, and a docs review that found the install command
    in the README did not work against the only published version.
15. **Dependabot auto-merge** — grouped minor and patch bumps merge on green; GitHub Actions bumps
    of any kind merge on green, because an action that misbehaves reddens CI rather than reaching a
    consumer. NuGet majors still wait for a human.

### Needs the live API before it can be built

Verified-not-inferred is now a rule ([ADR 0008](docs/adr/0008-sdk-boundary-testing.md)), so these
wait for API credits rather than being guessed at:

- **Server-side search round-trip** — `server_tool_use`, `web_search_tool_result` and text
  citations are not modeled, so a search does not survive into a later turn. Eight block shapes
  plus their request-side equivalents; a wrong shape fails every follow-up turn.
- **Re-running `live-smoke`** against the fixed transport, which is the only end-to-end proof that
  the stop-reason and cancellation fixes behave as measured.

## Unscheduled backlog

Not in a phase yet — parked deliberately, in rough order of appeal:

1. Orleans distributed hosting (`Emissary.Orleans`) — a grain per conversation.
2. NuGet-as-skill-marketplace conventions (`Emissary.Skills.*`).
3. Managed Agents bridge (write once, self-host or Anthropic-hosted).
4. Edge agents (offline queue + sync).
5. Conversation branching; speculative tool warm-up.
6. Budget-aware agents — tell the model its remaining token budget so it can adapt, rather than being
   guillotined at the cap.
7. Server-side search round-trip (blocked on live verification; see above).

Promoted out of this list into phases: model-upgrade canarying and `Emissary.RedTeam` are Phase 15,
because packaging them as a GitHub Action is what makes them spread.

---

*Sequencing logic: the testing story (Phase 3) lands before the features that need guarding
(Phases 4–5) — replay is both a headline feature and our own test infrastructure. Dogfooding
flywheel. The same logic orders the new phases: Phase 8 proves what exists before semver freezes it,
and Phase 10 makes tools replayable before Phases 11–14 add tools with side effects worth replaying.*
