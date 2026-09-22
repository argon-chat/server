---
description: "Use when modifying Ion contract definitions (.ion files), regenerating codegen, or updating interaction implementations after contract changes. Covers the build-time codegen (ionpath.compiler MSBuild SDK), the schema lock, breaking change handling, and the correct workflow for contract→codegen→impl updates."
applyTo: "src/Argon.Ion/**"
---
# Ion Codegen Workflow

## Critical Rule

**The generated C# is not in the repository.** `Argon.Ion.CodeGen.csproj` (and its `Account`/`Admin` siblings) uses the `ionpath.compiler` MSBuild SDK, which runs `ionc` before compilation and writes the sources to `obj/<config>/<tfm>/ion/`. Only edit `.ion` source files and `*InteractionImpl.cs` bridge files; to read the generated code, look under `obj/`.

## Workflow

1. Edit `.ion` files in `src/Argon.Ion/`
2. `dotnet build` — the C# is regenerated whenever an `.ion` file, `ion.config.json` or `ion.lock.json` changes. Ion errors show up as build errors positioned in the `.ion` source.
3. Update `*InteractionImpl.cs` in `src/Argon.Core/Services/Ion/` to match new generated interfaces
4. When the contract change is done, record it in the lock and commit `ion.lock.json` with it:
   `dotnet build src/Argon.CodeGen -p:IonLockMode=update`
5. If the change deliberately breaks the recorded contract → `ionc lock update` from `src/Argon.Ion/`

## The schema lock

A build validates the schema against `ion.lock.json` but never writes it (`IonLockMode=check`): until the lock is updated, anything not recorded yet can be added, renamed and removed freely, and only a real break of the recorded contract fails the build. CI should build with `-p:IonLockMode=frozen`, which fails (ION0071) when the committed lock does not record the committed schema.

| Command | Purpose |
|---------|---------|
| `dotnet build` | Generate the C# and validate against the lock |
| `dotnet build -p:IonLockMode=update` | ... and record the schema in `ion.lock.json` |
| `dotnet build -p:IonLockMode=frozen` | ... and fail unless the lock records the current schema (CI) |
| `ionc lock update` | Accept breaking changes, update `ion.lock.json` |
| `ionc compile --check` | Validate only, no code generation |
| `ionc compile -o Browser` | TypeScript client (`regenerate-for-web.ps1`); not part of the build |

## Project Structure

| Path | Role | Editable? |
|------|------|-----------|
| `src/Argon.Ion/*.ion` | Contract source of truth | YES |
| `src/Argon.Ion/ion.config.json` | Codegen config (targets, features; the `dotnet` generator has no `outputs`) | YES |
| `src/Argon.Ion/ion.lock.json` | Schema lock for breaking change detection | Only via the commands above |
| `src/Argon.CodeGen/Argon.Ion.CodeGen.csproj` | Compiles the contracts; `IonProjectDirectory` points at `../Argon.Ion` | YES |
| `src/Argon.CodeGen/obj/<config>/<tfm>/ion/` | Generated DTOs, formatters, server executors, client stubs | NO (build output) |
| `src/Argon.Core/Services/Ion/*InteractionImpl.cs` | Bridge: generated interface → grain calls | YES |

`Argon.IonAccount` → `Argon.CodeGenAccount` and `Argon.IonAdmin` → `Argon.CodeGenAdmin` work the same way. `Argon.IonAdmin` imports `Argon.Ion` as the `argon` module: the build type-checks against it but only generates the admin contracts, so `Argon.CodeGenAdmin` keeps its `ProjectReference` to `Argon.CodeGen`.

The SDK version is pinned by `<Sdk Name="ionpath.compiler" Version="..." />` in each codegen csproj; keep it in step with the `ionpath.runtime*` package versions.

## After Codegen

The `*InteractionImpl.cs` files implement the generated `I*Interaction` interfaces. After regenerating:
- Check for new/changed method signatures
- Update implementations to match
- These files map Ion RPC calls to Orleans grain method calls
