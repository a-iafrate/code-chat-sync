# Test Generation Research

## Project Overview
- **Path**: `C:\progetti\code-chat-sync`
- **Language**: C# on .NET 10 (SDK pinned to `10.0.401`, `rollForward: latestPatch`, prerelease disabled)
- **Framework**: `net10.0`, nullable reference types and implicit usings enabled
- **Test Framework**: xUnit `2.9.3`; Visual Studio runner `xunit.runner.visualstudio` `3.1.4`; `Microsoft.NET.Test.Sdk` `17.14.1`; `coverlet.collector` `6.0.4`
- **Project system**: SDK-style (`Microsoft.NET.Sdk`)
- **Dependency format and versions**: `PackageReference`; no mocking library is installed or needed
- **New-file registration**: implicit SDK compile glob. Place `SkipRunningCheckTests.cs` directly in `src\CodeChatSync.Tests`; do not add a `<Compile Include>` item and do not change the project file.

## Dependency Graph
- **Leaf types** (no in-scope dependencies): `LocalConfig`
- **Mid-layer types** (depend on leaves/contracts): `ProcessGuard` (`ForSync` consumes `LocalConfig` and `IChatProvider`); `IProcessGuard` is declared in the same file
- **Top-layer types** (depend on mid-layer): `ClaudeCodeChatProvider` consumes `IProcessGuard`; the requested integration passes the guard returned by `ProcessGuard.ForSync`

## Build & Test Commands
- **Build**: `dotnet build src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore` (canonical scoped build command; do not run in this research-only task, and the user authorizes only the filtered test command below for implementation validation)
- **Test (scoped — fix cycles)**: `dotnet test src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SkipRunningCheckTests"`
- **Test (harness-equivalent — discovery check)**: `dotnet test src\CodeChatSync.slnx --no-restore --list-tests` from the repository root. In the current Visual Studio host, the equivalent native discovery gate is `run_tests` filtered to project `CodeChatSync.Tests`; do not execute either during this research-only task.
- **Lint**: no repository-specific lint command was found. The standard scoped formatter would be `dotnet format src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --include src\CodeChatSync.Tests\SkipRunningCheckTests.cs`, but it is not required or authorized here.

## Scope
- **Boundary**: production analysis is restricted to exactly `src\CodeChatSync.Core\LocalConfig.cs`, `src\CodeChatSync.Core\ProcessGuard.cs`, and `src\CodeChatSync.Providers.Claude\ClaudeCodeChatProvider.cs`. Test-convention/context reads are restricted to the existing `CodeChatSync.Tests` project and the named support/tests needed by the requested scenarios. No sibling production source is inventoried.
- **Targets**: the three production files above; all are non-trivial and all appear in `.testagent\scope-ledger.md`.
- **Requested output test file**: `src\CodeChatSync.Tests\SkipRunningCheckTests.cs` (one new file only; this research task does not create it).
- **Scope ledger**: `.testagent\scope-ledger.md`
- **Canonical .NET test project and entry point**: `C:\progetti\code-chat-sync\src\CodeChatSync.Tests\CodeChatSync.Tests.csproj`; solution entry point `C:\progetti\code-chat-sync\src\CodeChatSync.slnx`, which already includes the test project.
- **Representative existing tests**: `src\CodeChatSync.Tests\LocalConfigTests.cs`; `src\CodeChatSync.Tests\ClaudeCodeChatProviderTests.cs`
- **Supporting test doubles/helpers inspected**: `TestSupport\FakeProcessGuard.cs`, `TestSupport\FakeChatProvider.cs`, `TestSupport\TempDirectory.cs`, and `TestSupport\ClaudeProjectsFixture.cs`

## Exact APIs and Constructors
- `new LocalConfig()` initializes `SkipRunningCheckProviderIds` to an empty `List<string>`.
- `bool LocalConfig.IsRunningCheckSkipped(string providerId)` uses `StringComparer.OrdinalIgnoreCase` through `List<string>.Contains`; it currently has no explicit null guard, so tests should cover the requested case-insensitive behavior, not invent a null contract for this helper.
- `LocalConfig.Load(string? path = null)` and `void LocalConfig.Save(string? path = null)` use the shared JSON options and the property name `skipRunningCheckProviderIds`.
- `new FakeProcessGuard(params string[] runningProcesses)` reports requested names that match its fixed running set, case-insensitively.
- `new FakeChatProvider(string localRoot, params string[] processNames)` implements `IChatProvider`; set its init-only provider identity with `new FakeChatProvider(root, "process") { Id = "provider-id" }`.
- `ProcessGuard.ForSync(IProcessGuard inner, LocalConfig config, IEnumerable<IChatProvider> providers)` returns `inner` unchanged when no process names are ignored; otherwise it returns an `IProcessGuard` that removes ignored process names before delegating. It throws `ArgumentNullException` for each null argument.
- `new ClaudeCodeChatProvider(IProcessGuard processGuard, string? projectsRoot = null)` rejects a null guard and accepts an absolute temporary projects root.
- Claude identity is `Id == "claudecode"`; its process list is exactly `["claude"]`.
- A minimal project value is `new ProjectInfo { Identity = ProjectIdentity.FromRemote("https://github.com/example/client-app.git"), LocalPath = absoluteLocalPath }`. `ClaudeProjectsFixture.Project(absoluteLocalPath)` is the established equivalent helper.
- `new TempDirectory()` creates and later removes an isolated absolute root. `Combine(params string[] segments)` creates paths but not directories; create the local project and Claude projects directories when the no-exception scenario needs them.

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|---|---|---|---|---|
| `src/CodeChatSync.Core/LocalConfig.cs` | `LocalConfig.SkipRunningCheckProviderIds`, `IsRunningCheckSkipped`, `Load`, `Save` | High | Partial for requested behavior | Verify safe defaults, exact JSON property round trip, legacy omission, and case-insensitive lookup. |
| `src/CodeChatSync.Core/ProcessGuard.cs` | `ProcessGuard.ForSync`; filtering `IProcessGuard` | High | Requested behavior untested | Verify identity optimization, selective process filtering, case-insensitive IDs/process names, and all null guards without touching OS process APIs. |
| `src/CodeChatSync.Providers.Claude/ClaudeCodeChatProvider.cs` | constructor and `Discover` process safety | High with fake guard/temp root | Substantial overall; integration gap | Demonstrate that the ordinary guard throws and the sync-filtered guard permits discovery for the same running `claude` process. |

### Medium Priority
None inside the requested boundary.

### Low Priority / Skip
None. All three requested non-trivial files must be represented in the new test file.

## Existing Tests & Coverage Classification
- `LocalConfig.cs` -> `LocalConfigTests.cs` plus other integration-oriented tests. **Partial for this request**: existing tests establish default/legacy/round-trip patterns but do not mention `skipRunningCheckProviderIds` or `IsRunningCheckSkipped`.
- `ProcessGuard.cs` -> statically paired to `TestSupport\FakeProcessGuard.cs` and `TestSupport\MutableProcessGuard.cs`. **Untested for the requested public factory**: no existing `ProcessGuard.ForSync` call or filtering assertion was found.
- `ClaudeCodeChatProvider.cs` -> `ClaudeCodeChatProviderTests.cs` and `ClaudeTranscriptRestoreCwdTests.cs`. **Substantial generally, partial for this request**: `Discover_ThrowsWhileClaudeIsRunning` proves the default exception path; no existing test supplies a guard filtered through `ForSync` to prove the opt-out path.
- The Roslyn analyzer reported all three files as paired. This is static name/reference evidence only and must not be interpreted as executed coverage.

## Existing Test Projects
- **Project file**: `src\CodeChatSync.Tests\CodeChatSync.Tests.csproj`
- **Target source projects**: direct project references to `CodeChatSync.Core`, `CodeChatSync.Providers.Claude`, `CodeChatSync.Providers.VisualStudio`, and `CodeChatSync.Git`
- **Relevant test files**: `LocalConfigTests.cs`, `ClaudeCodeChatProviderTests.cs`, `ClaudeTranscriptRestoreCwdTests.cs`, `TestSupport\FakeProcessGuard.cs`, `TestSupport\FakeChatProvider.cs`, `TestSupport\TempDirectory.cs`, `TestSupport\ClaudeProjectsFixture.cs`

## Testing Patterns
- Namespace is `CodeChatSync.Tests`; production/support usings are explicit, while xUnit is globally imported by the test project.
- Use public test classes, `[Fact]` for single cases, `[Theory]`/`[InlineData]` only for genuinely data-driven variants, and names in `Member_Scenario_ExpectedResult` form.
- Existing tests use direct xUnit assertions (`Assert.Equal`, `Assert.Empty`, `Assert.Single`, `Assert.Same`, `Assert.Throws<T>`) and hand-written fakes; no mocking framework.
- For temporary files, implement `IDisposable`, hold a `TempDirectory` or `ClaudeProjectsFixture` field, and dispose it in `Dispose()`.
- JSON assertions should inspect the serialized document/property and then assert the reloaded object, matching the round-trip patterns in `LocalConfigTests`.
- Claude tests inject `FakeProcessGuard` and an explicit temporary `projectsRoot`; they never inspect the real `~/.claude` tree or real processes.

## Requirement Checklist for `SkipRunningCheckTests.cs`
- [ ] **Defaults**: a new `LocalConfig` has an empty `SkipRunningCheckProviderIds` collection and reports a representative provider as not skipped.
- [ ] **JSON round trip**: save a config containing one or more skipped provider IDs to a `TempDirectory`; assert JSON contains the exact `skipRunningCheckProviderIds` property and values; reload and assert values/behavior are preserved.
- [ ] **Legacy JSON**: load valid JSON with the property omitted; assert an empty non-null collection and `IsRunningCheckSkipped(...) == false`.
- [ ] **Case-insensitive helper**: store a mixed-case provider ID and query with different casing; assert true, plus a distinct ID false if not already covered by defaults.
- [ ] **ForSync identity**: when no configured provider is skipped (including the empty-default case), assert `Assert.Same(inner, ProcessGuard.ForSync(...))`.
- [ ] **ForSync filtering**: configure one skipped provider and at least one unskipped provider using `FakeChatProvider`; ask for mixed process names and assert only the skipped provider's process names are removed while other running names remain. Exercise provider-ID and/or process-name case differences to prove ordinal-ignore-case behavior.
- [ ] **ForSync null checks**: separately pass `null!` for `inner`, `config`, and `providers`; assert `ArgumentNullException` for each. Keep other arguments valid so each guard is isolated.
- [ ] **Claude exception integration**: with `FakeProcessGuard("claude")`, an explicit temporary Claude projects root, and a valid temporary project path, assert `ClaudeCodeChatProvider.Discover(...)` throws `ClaudeCodeRunningException` and reports `["claude"]`.
- [ ] **Claude opt-out integration**: wrap the same running guard with `ProcessGuard.ForSync` using `LocalConfig.SkipRunningCheckProviderIds = ["claudecode"]` and a Claude provider descriptor/process list; construct `ClaudeCodeChatProvider` with the filtered guard and the same temporary root; assert discovery completes without exception (an empty result is expected when no transcripts exist).
- [ ] Keep all requested cases in the single new file `src\CodeChatSync.Tests\SkipRunningCheckTests.cs`.
- [ ] Make no production, project, package, solution, or existing-test changes.
- [ ] Validate only with `dotnet test src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SkipRunningCheckTests"`.

## Recommendations
1. Implement configuration serialization/default tests first because `LocalConfig` is the leaf and establishes the skip ID semantics.
2. Test `ProcessGuard.ForSync` next with the existing fakes; assert both the no-wrapper identity path and selective filtering.
3. Finish with the Claude `Discover` integration using only temporary paths, proving the safety exception remains the default and the explicit per-PC opt-out suppresses only the sync-time running check.
4. Do not alter production code even if a broader API improvement is noticed; this task is limited to characterization of the current implementation.
5. Preserve all unrelated dirty-worktree changes. The current worktree already contains modifications outside `.testagent`; none are part of this research artifact.
