# 07 — WebApi

An Emissary agent as a web service: HTTP in, Server-Sent Events out, and a human-in-the-loop
approval webhook that resumes a durably suspended run — the run can wait minutes or days
between suspension and approval.

## Run

```bash
export ANTHROPIC_API_KEY=...   # PowerShell: $env:ANTHROPIC_API_KEY = "..."
dotnet run --project samples/07-WebApi
```

## Walkthrough

Ask for a refund — the agent looks up the order (contract: refunds require a lookup first),
then hits the approval gate and suspends:

```bash
curl -N localhost:5000/agent -H "content-type: application/json" \
  -d '{"message":"Please refund order A-1001, it arrived broken."}'
```

The stream ends with:

```
event: suspended
data: {"conversationId":"<GUID>","pendingTools":["refund_payment"]}

event: completed
data: {..., "stopReason":"AwaitingApproval", ...}
```

The suspension is persisted in the `IAgentStateStore`. Approve (or deny) it whenever the human
decides — the webhook resumes the run and streams the rest:

```bash
curl -N localhost:5000/agent/approvals -H "content-type: application/json" \
  -d '{"conversationId":"<GUID>","approve":true}'
```

```
event: tool_result
data: {"id":"...","name":"refund_payment","result":"refunded $39.99 for order A-1001","isError":false}

event: completed
data: {..., "stopReason":"Completed", ...}
```

## Native AOT

```bash
dotnet publish samples/07-WebApi -c Release
```

`CreateSlimBuilder` + Emissary's source-generated tools and serializers keep the whole pipeline
reflection-free.
