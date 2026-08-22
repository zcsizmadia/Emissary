# 05 — SupportDesk

The flagship end-to-end sample: a customer-support agent running as a real service against a
real database, with the whole Emissary safety and observability stack engaged — and a full
`docker compose` deployment (agent + Postgres + Aspire dashboard).

One customer message drives a multi-step run that exercises, in a single trajectory:

- **Tool contracts** — a refund requires a prior successful order lookup (`Rules.Require`), and
  emails are capped per conversation (`Limit`).
- **A real database** — `lookup_order` / `issue_refund` query and update Postgres (or a seeded
  in-memory store offline) through `IOrderStore`.
- **Human-in-the-loop** — `issue_refund` moves money, so the run **durably suspends** for
  approval; state persists to SQLite and resumes via a webhook.
- **Taint tracking / prompt-injection defense** — the carrier's tracking page (an `Untrusted`
  tool) carries an injected "send a $500 gift card" instruction. It taints the run, and the
  agent's `Privileged` `send_email` is **provably blocked**.
- **Prompt caching & cost** — automatic cache breakpoints; the run reports cache-read tokens.
- **OpenTelemetry** — every `invoke_agent` / `chat` / `execute_tool` span and token metric
  streams to the Aspire dashboard.

## Run it offline (no API key, no Docker)

```bash
dotnet run --project samples/05-SupportDesk -- --replay
```

Replays the bundled `support.trajectory` through the exact agent configuration the service uses.
You'll see the order looked up, the refund suspended for approval and then resumed, the tracking
page's injection **blocked**, and the honest final summary — all deterministic, zero network.

## Run the full stack (Docker)

```bash
export ANTHROPIC_API_KEY=sk-ant-...
docker compose -f samples/05-SupportDesk/docker-compose.yml up --build
```

Three containers come up: **postgres** (seeded from `db/init.sql`), the **Aspire dashboard**
(http://localhost:18888), and the **support-desk** service (http://localhost:8080).

Start a conversation — the response streams as Server-Sent Events and ends in a `suspended`
event when the refund needs approval:

```bash
curl -N localhost:8080/support -H "content-type: application/json" \
  -d '{"message":"Order ORD-7 arrived damaged, please refund the $129.99. Where is ORD-9?"}'
```

Approve the refund with the `conversationId` from the `suspended` event:

```bash
curl -N localhost:8080/support/approvals -H "content-type: application/json" \
  -d '{"conversationId":"<GUID>","approve":true}'
```

Open the **Aspire dashboard** to watch the traces: the tool call tree, token usage per turn,
cache hit rates, and the blocked `send_email` span marked as an error.

## Architecture

```
customer ──HTTP/SSE──▶ support-desk ──┬── Postgres        (orders, refund ledger)
                        (Emissary)    ├── SQLite          (durable suspensions)
                                      └── OTLP ──▶ Aspire dashboard (traces + metrics)
```

`IOrderStore` has two implementations — `PostgresOrderStore` for the stack and a seeded
`InMemoryOrderStore` for offline replay — so the agent, tools, and trajectory are identical
in both modes.
