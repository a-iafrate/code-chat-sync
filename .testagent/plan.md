# Test Implementation Plan

## Overview
Add one new xUnit test file, `src\CodeChatSync.Tests\SkipRunningCheckTests.cs`, covering every source row in the scope ledger: `LocalConfig`, `ProcessGuard`, and `ClaudeCodeChatProvider`. The tests follow the dependency order from leaf configuration to sync-time process filtering and then Claude integration. Existing fakes and temporary-directory helpers are reused; no production, project, package, solution, or existing-test files are changed.

The plan is intentionally limited to the requested skip-running-check behavior. It characterizes the current default safety behavior and verifies that the explicit per-provider sync opt-out filters only the configured provider's process names.

## Commands
- **Build**: Not run; no build is authorized for this task.
- **Test**: `dotnet test src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SkipRunningCheckTests"`
- **Lint**: Not run; the user authorized only the filtered test command.

## Phase Summary
| Phase | Focus | Files | Est. Tests |
|-------|-------|-------|------------|
| 1 | Skip-running-check configuration, filtering, and Claude integration | 3 source rows; 1 new test file | 9-11 |

---

## Phase 1: Skip-Running-Check End-to-End Behavior

### Overview
Implement all requested coverage in the existing canonical test project and in the single new file `src\CodeChatSync.Tests\SkipRunningCheckTests.cs`. Start with `LocalConfig` because it defines the skip-ID semantics, continue with `ProcessGuard.ForSync` because it consumes that configuration, and finish with `ClaudeCodeChatProvider.Discover` to prove the integration behavior. Use namespace `CodeChatSync.Tests`, explicit production/support usings, xUnit assertions, hand-written fakes, and temporary paths consistent with the existing tests.

### Files to Test

#### 1. `LocalConfig.cs`
- **Source**: `src\CodeChatSync.Core\LocalConfig.cs`
- **Test File**: `src\CodeChatSync.Tests\SkipRunningCheckTests.cs`
- **Test Class**: `SkipRunningCheckTests`

**Methods and scenarios to test**:
1. `LocalConfig` defaults - a new configuration exposes a non-null empty `SkipRunningCheckProviderIds` collection and reports a representative provider ID as not skipped.
2. `LocalConfig.Save` / `LocalConfig.Load` JSON round trip - save a configuration containing one or more skipped provider IDs to a `TempDirectory`; inspect the serialized JSON for the exact `skipRunningCheckProviderIds` property and expected values; reload it and assert the collection and skip behavior are preserved.
3. Legacy JSON compatibility - load valid JSON that omits `skipRunningCheckProviderIds`; assert the resulting collection is non-null and empty and `IsRunningCheckSkipped` returns `false`.
4. `IsRunningCheckSkipped` case handling - store a mixed-case provider ID, query it using different casing, and assert `true`; query a distinct ID and assert `false`.

#### 2. `ProcessGuard.cs`
- **Source**: `src\CodeChatSync.Core\ProcessGuard.cs`
- **Test File**: `src\CodeChatSync.Tests\SkipRunningCheckTests.cs`
- **Test Class**: `SkipRunningCheckTests`

**Methods and scenarios to test**:
1. `ProcessGuard.ForSync` identity optimization - with an empty/default configuration and with a configuration that skips no configured provider, assert `Assert.Same(inner, ProcessGuard.ForSync(...))`.
2. `ProcessGuard.ForSync` selective filtering - configure one skipped provider and at least one unskipped provider using `FakeChatProvider`; use a fake guard with mixed running process names and assert the returned guard removes only the skipped provider's process names while preserving unrelated and unskipped-provider process names. Exercise provider-ID and process-name casing as required by the existing case-insensitive contracts.
3. `ProcessGuard.ForSync` argument validation - in three independent cases, pass `null!` for `inner`, `config`, and `providers` while keeping all other arguments valid; assert `ArgumentNullException` for each case.

#### 3. `ClaudeCodeChatProvider.cs`
- **Source**: `src\CodeChatSync.Providers.Claude\ClaudeCodeChatProvider.cs`
- **Test File**: `src\CodeChatSync.Tests\SkipRunningCheckTests.cs`
- **Test Class**: `SkipRunningCheckTests`

**Methods and scenarios to test**:
1. Default process-safety exception - inject `FakeProcessGuard("claude")`, provide an explicit temporary Claude projects root and a valid temporary project path, then assert `ClaudeCodeChatProvider.Discover(...)` throws `ClaudeCodeRunningException` and reports the Claude process name `"claude"`.
2. Explicit sync opt-out integration - configure `LocalConfig.SkipRunningCheckProviderIds` with `"claudecode"`; pass a Claude provider descriptor with the `claude` process name to `ProcessGuard.ForSync`; construct `ClaudeCodeChatProvider` with the filtered guard and the same temporary projects root; assert discovery proceeds without the running-process exception and returns the expected discovery result for the valid project setup.

### Implementation Constraints
- Keep every requested case in exactly `src\CodeChatSync.Tests\SkipRunningCheckTests.cs`.
- Reuse `FakeProcessGuard`, `FakeChatProvider`, `TempDirectory`, and `ClaudeProjectsFixture` where appropriate; do not add a mocking package or new helper.
- Use `ClaudeCodeProvider` identity `"claudecode"`, the established `ClaudeCodeChatProvider` constructor, and temporary absolute paths only; never inspect real Claude or user directories/processes.
- Do not modify production code, `CodeChatSync.Tests.csproj`, packages, the solution, or any existing test file.
- Preserve the existing test naming and assertion conventions. Use separate tests for independent null guards so each failure identifies the isolated invalid argument.

### Success Criteria
- [ ] `SkipRunningCheckTests.cs` is the only implementation file added or changed.
- [ ] All three scope-ledger source rows are represented in the new test file.
- [ ] Defaults, JSON round trip, legacy JSON, and case-insensitive helper behavior are asserted.
- [ ] `ProcessGuard.ForSync` identity, selective filtering, and all three null guards are asserted.
- [ ] Both Claude default exception and explicit opt-out integration behaviors are asserted.
- [ ] No production, project, package, solution, or existing-test changes are made.
- [ ] Run only `dotnet test src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore --filter "FullyQualifiedName~SkipRunningCheckTests"`.
- [ ] Stop after the authorized filtered test command passes; do not run a build, broader test suite, solution discovery, or lint command.
