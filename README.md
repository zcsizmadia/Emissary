# Emissary

**Production-grade Claude agents for .NET — AOT-compiled, zero-trust, observable.**

Emissary is a Claude-native agent framework for modern C#. It shifts agent correctness left:
tool schemas and dispatchers are generated at compile time (zero reflection, Native AOT),
agent behavior is constrained by declarative contracts, and every run is recordable and
replayable — agents you can put in front of an auditor.

> Status: pre-release, under active development. See [ROADMAP.md](ROADMAP.md).

## Principles

- **Claude-native, not provider-agnostic** — depth over breadth ([ADR 0001](docs/adr/0001-claude-native.md)).
- **The compiler is the safety net** — source generators and analyzers over runtime reflection.
- **Determinism, provability, reversibility** — record/replay, behavioral contracts, compensation.
- **100% test coverage, honestly** — enforced in CI with documented carve-outs ([ADR 0003](docs/adr/0003-coverage-policy.md)).

## Repository layout

| Path | Contents |
|---|---|
| `src/` | Shipped libraries (net10.0, Native AOT-compatible) |
| `tests/` | TUnit test projects (Microsoft.Testing.Platform) |
| `samples/` | Runnable samples, built in CI on every commit |
| `docs/adr/` | Architecture decision records |

## Building

Requires the .NET 10 SDK.

```bash
dotnet build Emissary.slnx --configuration Release
dotnet test --solution Emissary.slnx --configuration Release -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
```

## License

[MIT](LICENSE)
