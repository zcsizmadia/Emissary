# Testing agents

Agents are nondeterministic; test suites must not be. Emissary's answer is to record real runs
and replay them exactly, then assert on *behavior* rather than wording.

## Record and replay

```csharp
// Record a live run
var recorder = new TrajectoryRecorder();
var agent = new ClaudeAgent(options, recorder);
await agent.RunAsync("Refund order A-1001");
recorder.ToTrajectory().Save("refund.trajectory");

// Replay it — zero network, real tools still execute
var replayed = new ClaudeAgent(options, Trajectory.Load("refund.trajectory"));
var result = await replayed.RunAsync("Refund order A-1001");
```

A `.trajectory` is readable JSON containing every request and response. Replay verifies as it
goes: if the agent's requests drift from the recording — different model, tool set, or
conversation shape — it throws `TrajectoryDivergenceException` rather than silently pretending.

Check golden trajectories into your repo and replay them in CI; they cost microseconds and no
API budget.

## Behavioral assertions

`Emissary.Testing` is framework-agnostic (works with TUnit, xUnit, NUnit, MSTest):

```csharp
EmissaryAssert.That(result)
    .ToolCalled("refund_payment", times: 1)
    .ToolNotCalledBefore("refund_payment", requiredPredecessor: "verify_identity")
    .ToolNotCalled("delete_account")
    .NotTainted()
    .NoPlannedEffects()
    .Stopped(AgentStopReason.Completed)
    .FinalTextContains("refunded");
```

These assert what the agent *did*, which is stable, rather than what it *said*, which is not.

Two of them are worth putting on every golden run, because what they catch is otherwise invisible:

```csharp
EmissaryAssert.That(result)
    .NoToolFailures()   // a tool that starts throwing — the model narrates around it
    .Complete();        // a truncated, refused, or paused answer — FinalText looks fine either way
```

A failing tool is reported to the model, which then explains itself politely, so a run whose
database is down can still look successful in the transcript. `NoToolFailures()` names the
exceptions; `ToolFailed("charge_card")` and `ToolTimedOut("slow_report")` assert the opposite when a
failure is the thing under test. `Complete()` fails on every stop reason that leaves the answer cut
short and says which one it was.

## Cost as a unit test

A replayed run carries the usage that was recorded, so what a run *costs* is a deterministic number
available with no network — which makes it assertable like any other behaviour:

```csharp
EmissaryAssert.That(result)
    .TokensUnder(12_000)              // input + output, the same total TokenBudget caps
    .CostUnder(0.05m, estimator);     // priced with your own rates
```

This catches a class of regression that otherwise reaches you as an invoice: a system prompt that
quietly grows, a tool that starts returning ten times what it used to, a contract change that adds
a turn to every run. None of those break a test that only checks the final answer.

`CostUnder` prices `result.Usage` through a [`CostEstimator`](production.md#prompt-caching-and-cost)
you register rates on — Emissary ships no prices, since they change. Cache reads and writes are
billed at their own rates, so a cache-heavy run costs far less than its raw token count suggests;
`TokensUnder` deliberately counts only input plus output, matching `TokenBudget`, so the two agree.

## Model-upgrade canarying

The question every team dreads — *what changes when we upgrade the model?* — becomes a diff:

```csharp
var baseline = Trajectory.Load("golden/refund.trajectory");
var candidate = new ClaudeAgent(new AgentOptions { Model = "claude-opus-5-1", /* … */ });

var report = await TrajectoryCanary.RunAsync(baseline, candidate);
if (!report.Passed) Console.WriteLine(report.ToText());
```

`Passed` tolerates final-text wording drift (expected across versions) but fails on a changed
tool sequence, turn count, or stop reason. Run it over a suite of goldens and a model upgrade
becomes a managed rollout.

## LLM-as-judge evaluation

Canarying answers "did it change?"; evaluation answers "is it good?"

```csharp
var rubric = new EvaluationRubric()
    .Criterion("resolved", "Did the agent resolve the customer's request?")
    .Criterion("safe", "Did it avoid acting on untrusted content?");

var eval = await EmissaryEval.EvaluateAsync(rubric, result, judgeAgent);
Console.WriteLine(eval.ToText());   // per-criterion PASS/FAIL with reasons
```

The judge is an ordinary `ClaudeAgent`, configured with
`OutputSchemaJson = EmissaryEval.JudgeSchema`. Point it at a live model to grade for real, or at
a recorded trajectory to make evaluation itself deterministic in CI. Missing verdicts fail closed.

To grade a whole suite at once, see [`BatchEvaluator`](#bulk-evaluation).

## Bulk evaluation

```csharp
var suite = new[] { (rubric, runA), (rubric, runB), (rubric, runC) };
var report = await BatchEvaluator.EvaluateAllAsync(suite, judgeAgent, maxConcurrency: 4);

Console.WriteLine(report.ToText());   // pass rate + the failures, worst first
```

`BatchEvaluator` grades many runs with bounded concurrency and aggregates the results into a
single pass/fail report — the shape you want for a nightly quality gate over your golden suite.

## Testing your own tools

Generated tools are ordinary objects, so unit-testing one needs no agent at all:

```csharp
using var input = JsonDocument.Parse("""{"city":"Oslo"}""");
string result = await MyTools.GetWeatherTool.InvokeAsync(input.RootElement);
```
