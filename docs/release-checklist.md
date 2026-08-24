# Release checklist — the first non-preview release

Cutting `v1.0.0` is the moment the public API stops being negotiable: from then on
[ADR 0005](adr/0005-versioning-and-releases.md) commits Emissary to strict semver, so anything
awkward in the surface area is awkward for the life of the major version. This is the list of things
that must be true, or must change, at that point.

Everything here was checked as part of a docs review; items already fixed are marked as such so the
list stays honest about what is left.

## 1. Docs that lie once a stable version exists

| Item | State |
|---|---|
| `docs/guides/getting-started.md` install command uses `--prerelease` | **Must be removed at release.** Correct today: no stable version exists to install. |
| `README.md` quick-start install command | **Fixed** — was missing `--prerelease`, so it did not work against the only published version. Must lose the flag at release, together with the line above. |
| Preview-era wording anywhere else | **Verified clean** — no "preview", "coming soon", "arrives in a later phase", or `0.1.0` references remain in `README.md`, `docs/`, or `samples/README.md`. |

Both install commands must change in the same commit as the release, and nowhere else references a
version number, so that commit is small and complete.

## 2. Package validation baseline

ADR 0005 says to set `PackageValidationBaselineVersion` after the first stable release. Until it is
set, `EnablePackageValidation` has no baseline to compare against, so an accidental breaking change
in `1.0.1` would not be caught. Set it to `1.0.0` immediately after the tag.

## 3. API surface to settle before it freezes

These are deliberate choices worth one last look, not defects:

- **`EmissaryDefaults.Model` is a `const`.** A `const` is inlined into every consumer at compile
  time, so a caller that references it keeps sending the old model id until it is recompiled — for a
  value that exists precisely because model ids change, `static readonly` is the better shape.
  Changing it is source-compatible but binary-breaking, so it is cheap now and expensive later.
- **`ToolArguments` is public.** It has to be: generated dispatchers live in the consumer's
  assembly. It is marked `[EditorBrowsable(Never)]` and documented as generated-code support, but it
  is public surface under semver from 1.0 on. Confirm the method set is what we want to keep.
- **`IAgentStateStore.DeleteAsync` returns `Task<bool>`** (the atomic claim that makes approval
  at-most-once). This changed shape late; it is the right shape, and 1.0 is the deadline for that
  kind of correction.
- **`AgentStopReason` has six values**, three of which mean the answer is incomplete
  (`MaxTokens`, `Refusal`, `Paused`). Adding an enum member later is not breaking, so this is fine;
  it is listed because `Paused` was only reachable after the transport was fixed, and callers that
  switch exhaustively should know it exists.

## 4. Verification that needs the live API

Neither of these can be done offline, and both are gates rather than nice-to-haves:

- **Run the live gate green against the release candidate:**
  `dotnet run --project tests/Emissary.LiveSmoke`. It asserts the things only the API can answer —
  a strict schema round-tripping, a tool call executing, a truncated answer reporting `MaxTokens`,
  and a cancelled stream stopping — each of which corresponds to a defect that shipped in a release.
  It prints `SKIPPED` and exits 0 without a key, and costs about a fifth of a cent to run.
  [ADR 0008](adr/0008-sdk-boundary-testing.md) exists because that gap is where two bugs hid.
- **Re-record the sample trajectories on a cheap model.** `04-RecordReplay` and
  `06-ZeroTrustAgent` replay trajectories recorded with `claude-opus-5`, so those two samples cannot
  take the samples' cost cap on the model (replay verification rejects a mismatch — correctly).
  Re-recording them makes every sample cheap to run.

## 5. Features still open, and whether they block

From the phase notes in
[ROADMAP.md](https://github.com/zcsizmadia/Emissary/blob/main/ROADMAP.md). None of these are
regressions; the question is only whether 1.0 claims them:

| Open item | Recommendation |
|---|---|
| Server-side search round-trip (`server_tool_use`, `web_search_tool_result`, citations) | **Document, do not block.** Already documented as a known limitation; needs live verification. |
| Server `task_budget` | Do not block — local `TokenBudget` covers the practical need. |
| Cache-invalidation analyzer warnings | Do not block. |
| Source-generated contract declarations | Do not block — runtime `ToolRules` cover the semantics, and construction-time validation closed the typo hole. |
| Mutation testing (Stryker cannot run TUnit) | Do not block; tracked in ADR 0003 with the upstream issue. |

## 6. Mechanics at the tag

1. Confirm `main` is green: build, 100% line and branch coverage, AOT proof executed, all samples
   compiled, docs built with `--warningsAsErrors`.
2. Remove `--prerelease` from both install snippets.
3. Create the release in the GitHub UI with tag `v1.0.0`; MinVer derives every package version from
   it, in lockstep.
4. Set `PackageValidationBaselineVersion` to `1.0.0`.
5. Check the published docs site reflects the tag.
