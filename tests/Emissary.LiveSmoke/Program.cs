using System.Diagnostics;
using Emissary;
using Emissary.LiveSmoke;

// Each check below corresponds to a defect that shipped in a release and that no offline test
// could have caught, because the offline suite talks to a fake transport which never validates a
// request nor produces a real stop reason. Adding a check here is the standing cost of learning
// that lesson twice (ADR 0008).
//
// Deliberately cheap: a small model, MaxTokens in the tens, and a token budget on every run. The
// whole gate is a couple of thousand tokens.
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    Console.WriteLine("SKIPPED: ANTHROPIC_API_KEY is not set, so the live gate cannot run.");
    return 0;
}

int failures = 0;

// 1. Structured outputs. A request carrying only an output schema used to carry an effort the
//    model had not been asked for ("effort":"low"), and models without effort support rejected it,
//    so structured outputs were impossible on them.
var triageOptions = Cheap();
triageOptions.SystemPrompt = "Extract structured data faithfully.";
triageOptions.OutputSchemaJson = Verdict.JsonSchema;
var triage = await new ClaudeAgent(triageOptions).RunAsync(
    "The checkout page returns a 500 when a discount code is applied. Approve a hotfix?");
Verdict? verdict = null;
try
{
    verdict = triage.FinalAs(LiveSmokeJson.Default.Verdict);
}
catch (InvalidOperationException)
{
    // Left null; reported as a failure below.
}

Report(
    "a strict output schema round-trips",
    triage.StopReason == AgentStopReason.Completed && verdict is { Summary.Length: > 0 },
    $"{triage.StopReason}, summary={(verdict?.Summary is { } s ? $"\"{Trim(s)}\"" : "<none>")}");

// 2. The tool loop. Every stop reason once collapsed to end_turn; tool calling survived only
//    through a fallback that infers tool_use from the assembled content.
var toolOptions = Cheap();
toolOptions.SystemPrompt = "Use the provided tool rather than answering from memory.";
toolOptions.Tools.Add(SmokeTools.AddNumbersTool);
var sum = await new ClaudeAgent(toolOptions).RunAsync("What is 21 plus 21? Use the tool.");
Report(
    "a tool call executes and the answer comes back",
    sum.StopReason == AgentStopReason.Completed
        && sum.Conversation.Messages.Any(m => m.Content.OfType<ToolUseBlock>().Any())
        && sum.FinalText.Contains("42", StringComparison.Ordinal),
    $"{sum.StopReason}, {sum.Usage.InputTokens} in / {sum.Usage.OutputTokens} out");

// 3. AgentStopReason.MaxTokens was unreachable in production: a truncated answer reported
//    Completed, so callers could not tell a cut-off answer from a finished one.
var truncatingOptions = Cheap();
truncatingOptions.MaxTokens = 16;
var truncated = await new ClaudeAgent(truncatingOptions).RunAsync(
    "Write a detailed 500-word essay about the sea.");
Report(
    "a truncated answer reports MaxTokens",
    truncated.StopReason == AgentStopReason.MaxTokens,
    $"{truncated.StopReason}, {truncated.Usage.OutputTokens} out");

// 4. Cancellation was severed after the first streamed event: nothing stopped, and output kept
//    being generated and billed until the SDK's own ten-minute timeout.
var streamOptions = Cheap();
streamOptions.MaxTokens = 64;
using var cancellation = new CancellationTokenSource();
var stopwatch = Stopwatch.StartNew();
bool cancelled = false;
try
{
    await foreach (var streamed in new ClaudeAgent(streamOptions)
        .StreamAsync("Count slowly from one to fifty.", cancellation.Token))
    {
        if (streamed is AgentTextEvent)
        {
            await cancellation.CancelAsync();
        }
    }
}
catch (OperationCanceledException)
{
    cancelled = true;
}

stopwatch.Stop();
Report(
    "cancelling mid-stream stops the run promptly",
    cancelled && stopwatch.Elapsed < TimeSpan.FromSeconds(30),
    $"cancelled={cancelled} after {stopwatch.Elapsed.TotalSeconds:0.0}s");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "LIVE GATE PASSED" : $"LIVE GATE FAILED: {failures} check(s)");
return failures == 0 ? 0 : 1;

// A small model, a low turn limit and a hard token budget, so a misbehaving check stops rather
// than spends. Adaptive thinking is not supported on small models, so none is requested.
static AgentOptions Cheap() => new()
{
    Model = "claude-haiku-4-5-20251001",
    Thinking = ThinkingMode.Disabled,
    MaxTokens = 256,
    MaxTurns = 3,
    TokenBudget = 20_000,
};

static string Trim(string text) => text.Length <= 60 ? text : text[..60] + "…";

void Report(string what, bool ok, string detail)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what} — {detail}");
    if (!ok)
    {
        failures++;
    }
}
