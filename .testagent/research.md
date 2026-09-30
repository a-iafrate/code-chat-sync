# Test Generation Research

## Project Overview
- **Path**: `C:\progetti\code-chat-sync`
- **Language**: C# / .NET 10; repository `global.json` pins SDK `10.0.401` with `rollForward: latestPatch` and prerelease disabled.
- **Framework**: `net10.0`; nullable and implicit usings enabled.
- **Test Framework**: xUnit `2.9.3`; `Microsoft.NET.Test.Sdk` `17.14.1`; `xunit.runner.visualstudio` `3.1.4`; `coverlet.collector` `6.0.4`. This is xUnit v2 with the Visual Studio/VSTest adapter; the project does not select Microsoft.Testing.Platform (no `test.runner` setting in `global.json`).
- **Project system**: SDK-style (`Microsoft.NET.Sdk`).
- **Dependency format and versions**: `PackageReference`; no mocking library is referenced. Respect the requested no-new-NuGet-packages constraint.
- **New-file registration**: implicit SDK `Compile` glob; new `.cs` test files beneath `src\CodeChatSync.Tests` require no explicit `<Compile Include>` or project-file edits.

## Dependency Graph
- **Leaf types**: `WorkspaceDescriptorPathMapper` (static; BCL-only byte/text/path transformation).
- **Mid-layer types**: `VisualStudioChatProvider` (implements Core `IChatSessionProvider` and `IChatContentMapper`; uses `CopilotChatDiscovery` and Core path/identity contracts).
- **Top-layer types**: `ChatSyncService` (uses injected `IProcessGuard`, `IChatProvider`, optional runtime `IChatContentMapper`, and filesystem/state APIs; coordinates portable hash, push, pull, backup and restore).
- **Test seams**: Core interfaces are outside the production scope but are already referenced by the canonical test project. Existing `FakeChatProvider` implements only `IChatProvider`, so mapper-specific service tests need a test-local provider/fake that also implements `IChatContentMapper`; use existing `FakeProcessGuard` or `MutableProcessGuard` for process states. The requested E2E instead uses the real Visual Studio provider with an explicit temp session-state root and the existing fake process guard.

## Build & Test Commands
- **Build**: `dotnet build src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore`
- **Test (scoped — fix cycles)**: `dotnet test src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceDescriptorPathMapperTests|FullyQualifiedName~VisualStudioChatProviderTests|FullyQualifiedName~ChatSyncServiceTests"`
- **Test (harness-equivalent — discovery check)**: from repository root, `dotnet test src\CodeChatSync.slnx --no-restore --list-tests`. `src\CodeChatSync.slnx` is the checked-in solution entry point and explicitly includes the canonical test project. The pinned SDK is .NET 10; the test project uses the standard VSTest path through `Microsoft.NET.Test.Sdk` and xUnit VS runner.
- **Lint**: no repository-specific lint command/configuration found. Optional formatter convention: `dotnet format src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --include src\CodeChatSync.Tests\WorkspaceDescriptorPathMapperTests.cs src\CodeChatSync.Tests\VisualStudioChatProviderTests.cs src\CodeChatSync.Tests\ChatSyncServiceTests.cs` (not required by this research).
- **Execution note**: this is research only; no build/test/discovery command was run. No production changes, test-source edits, or package additions are authorized.

## Scope
- **Boundary**: exactly the three requested production source files: `src\CodeChatSync.Providers.VisualStudio\WorkspaceDescriptorPathMapper.cs`, `src\CodeChatSync.Providers.VisualStudio\VisualStudioChatProvider.cs`, and `src\CodeChatSync.Core\ChatSyncService.cs`. Existing tests/support and directly relevant project/solution configuration were consulted only for pairing, conventions, and test seam discovery; no sibling production files are targets.
- **Targets**: the same three non-trivial files, all recorded in `.testagent/scope-ledger.md`.
- **Scope ledger**: `.testagent/scope-ledger.md`
- **Canonical .NET test project and entry point**: `C:\progetti\code-chat-sync\src\CodeChatSync.Tests\CodeChatSync.Tests.csproj`; repo test/discovery solution entry point `C:\progetti\code-chat-sync\src\CodeChatSync.slnx` (contains `CodeChatSync.Tests`).
- **Representative existing tests**: `src\CodeChatSync.Tests\ChatSyncServiceTests.cs`; `src\CodeChatSync.Tests\VisualStudioChatProviderTests.cs`.
- **Static pairing**: executed the Roslyn `find-untested-sources` analyzer once at the repository root (parse-only, no production compilation or tests). It classified `WorkspaceDescriptorPathMapper.cs` as unpaired and paired `VisualStudioChatProvider.cs` with `VisualStudioChatProviderTests.cs`, and `ChatSyncService.cs` with `ChatSyncServiceTests.cs` among other references.

## Files to Test

### High Priority
| File | Classes/Functions | Testability | Estimated Coverage | Notes |
|------|-------------------|-------------|-------------------|-------|
| `src/CodeChatSync.Providers.VisualStudio/WorkspaceDescriptorPathMapper.cs` | `WorkspaceDescriptorPathMapper.ToPortable`, `ToLocal`, `ProjectRootToken` | High | Untested | Pure transformations. Test byte-exact no-change cases, descriptor quoting/line endings/BOM and root-vs-subfolder boundaries. Suggested new path from Roslyn: `src/CodeChatSync.Tests/WorkspaceDescriptorPathMapperTests.cs`. |
| `src/CodeChatSync.Providers.VisualStudio/VisualStudioChatProvider.cs` | `VisualStudioChatProvider` discovery, identity/path mapping and `IChatContentMapper` methods | High | Partial overall; requested mapping selection and cross-PC round trip untested | Extend the existing provider test file; add a real-provider two-PC temp-directory sync E2E. `IsMapped` must identify only a top-level `workspace.yaml`, case-insensitively. |
| `src/CodeChatSync.Core/ChatSyncService.cs` | `ChatSyncService.Sync`, mapped hash/Push/Pull/backup/process-guard flow, `GetBackupDirectory` | High | Partial generally; mapper flow untested | Extend existing service tests with a mapper-capable fake provider. Assert bytes on both sides, returned action/backup/block flags, baseline and no-rewrite behavior. |

### Medium Priority
None. The request explicitly requires every listed file, including the unpaired leaf mapper.

### Low Priority / Skip
None. No target is generated, trivial, or deferred; no testability blocker requires deferral.

## Existing Tests & Coverage Classification
- `WorkspaceDescriptorPathMapper.cs` -> **No matching test file found (untested)**. Roslyn marks unpaired with the suggested `WorkspaceDescriptorPathMapperTests.cs`; test-source search found no direct `ToPortable`, `ToLocal`, or `IsMapped` calls. All requested transformation semantics therefore need direct byte-level tests.
- `VisualStudioChatProvider.cs` -> `VisualStudioChatProviderTests.cs` (**partial**). Existing suite has 11 facts and one four-row theory covering session discovery, runtime DB/lock exclusion, nested arbitrary files, local-path resolution, and lock state. It does not assert `IChatContentMapper` membership/classification or perform a push/pull across two distinct project paths. `IsMapped` and conversion integration remain requested gaps.
- `ChatSyncService.cs` -> `ChatSyncServiceTests.cs` (**partial**). Existing direct suite has 18 facts covering ordinary push/pull, backup, unchanged/conflict, process guard, dry run, in-use and restore selection. There are additional static references from integration tests. No existing test-source call to `IChatContentMapper.ToPortable`, `ToLocal`, or `IsMapped` was found; existing service cases do not establish portable hashes/bytes, local remapping rewrite, mapped pull backup, or no ping-pong.
- `SyncState.ComputeHash(ReadOnlySpan<byte>)` is outside this target scope but must be asserted as a dependency behavior required by the request: current tests use the path overload; no direct span-overload test was found.
- Coverage classifications are static pairing and bounded test-source inspection, not instrumented coverage. Do not infer line/branch percentages.

## Existing Test Projects
- **Project file**: `src\CodeChatSync.Tests\CodeChatSync.Tests.csproj` (only relevant test project discovered/selected for this scope).
- **Target source project references**: direct references to `CodeChatSync.Core`, `CodeChatSync.Providers.VisualStudio`, `CodeChatSync.Providers.Claude`, and `CodeChatSync.Git`; required Core and Visual Studio provider references already exist.
- **Test files relevant to this scope**: `src\CodeChatSync.Tests\ChatSyncServiceTests.cs`; `src\CodeChatSync.Tests\VisualStudioChatProviderTests.cs`; support `src\CodeChatSync.Tests\TestSupport\FakeChatProvider.cs`, `FakeProcessGuard.cs`, `MutableProcessGuard.cs`, and `TempDirectory.cs`.
- **Runner / entry-point check**: package references identify Microsoft.NET.Test.Sdk + xUnit VS adapter (VSTest); `global.json` pins SDK 10.0.401 and has no runner override. `src\CodeChatSync.slnx` includes `CodeChatSync.Tests`; no repository workflow directory or README test command was found.

## Testing Patterns
- Namespace `CodeChatSync.Tests`; test project globally imports `Xunit`, so existing tests import production/support namespaces as needed. Use `[Fact]` for focused cases and `[Theory]` only for genuine data-driven variants; method names follow `Member_Scenario_ExpectedResult`.
- Direct xUnit assertions include `Assert.Equal`, `Assert.Same`, `Assert.Single`, `Assert.Contains`, and `Assert.Throws`; tests use explicit expected values and filesystem outcomes, not a mocking framework.
- Temp filesystem setup uses `TempDirectory` (`IDisposable`) and injected temporary roots. Provider tests write descriptor files under a temp session-state root. Service tests use existing fake provider and guard; avoid real process detection by injecting `FakeProcessGuard` / `MutableProcessGuard`.
- For byte-sensitive mapper cases, compare full byte arrays and assert unchanged-input identity when that is the contract; construct BOM, CRLF and invalid UTF-8 byte sequences explicitly rather than round-tripping through `File.ReadAllText`.

## Required Scenario Matrix
Implement the user-specified checklist with no production edits or package changes:
1. **Mapper `ToPortable`**: map root `cwd` while preserving its trailing separator; map root `git_root` without adding one; map a subfolder; compare root case-insensitively; retain outside-root and sibling-prefix values and return the identical input array when no change occurs; preserve unrelated lines, multiline escaped values, `created_at`, CRLF and UTF-8 BOM.
2. **Mapper `ToLocal`**: expand `${project}` using the local root and preserve the separator represented in the mapped path; round-trip an under-root path; map a foreign absolute path with no `git_root` to local root with and without trailing separator; when a foreign `cwd` is below a foreign `git_root`, retain that subfolder; preserve an absolute path when injected `directoryExists` reports it exists; leave relative/non-absolute values unchanged; use local root casing for an under-root value with different casing; preserve double-quoted escaped backslashes; for invalid UTF-8 return the original byte array unchanged.
3. **Visual Studio mapper contract**: assert the concrete provider implements `IChatContentMapper`; `IsMapped` is true only for a top-level workspace descriptor, case-insensitively (including filename casing), and false for `events.jsonl` and nested `checkpoints/.../workspace.yaml` descriptors.
4. **Service with fake provider + mapper**: mapped local content is hashed in portable form; push writes portable bytes; pull writes local bytes and backs up an existing local file; portable-equal but differently remapped local bytes are restored by Pull with backup when allowed; provider-running pull is skipped with `IsBlockedByProvider`; already-local correct remapping yields `Unchanged` and does not rewrite the file; `SyncState.ComputeHash(ReadOnlySpan<byte>)` equals `ComputeHash(path)` for the same bytes.
5. **Real-provider two-PC E2E in temp directories**: PC A uses a real `VisualStudioChatProvider` and a source descriptor with PC A project path to push; PC B has a distinct `ProjectInfo.LocalPath` and empty session state and pulls; verify PC B session descriptor stores PC B path while the sync copy has `${project}`; a second PC B sync reports `Unchanged` and does not ping-pong. Inject existing fake process-guard support into service; do not depend on a running Visual Studio process.

## Recommendations
1. Implement direct mapper byte-oriented tests first: it is the unpaired leaf and fixes the portable/local transformation contract independently of filesystem orchestration.
2. Extend `VisualStudioChatProviderTests` for `IChatContentMapper` classification and conversions; keep provider filesystem input in `TempDirectory`.
3. Extend `ChatSyncServiceTests` with a test-local `IChatProvider, IChatContentMapper` fake and existing process-guard fakes for portable hash/push/pull, backup, blocked pull and no-rewrite cases.
4. Add the end-to-end case using the actual Visual Studio provider for both simulated PCs, with separate local project and session-state roots, an empty B-side state initially, and fake process guard. Check all three representations: local A, portable sync descriptor, and local B.
5. Run the scoped test filter during fix cycles and solution `.slnx` discovery command for harness visibility. Keep all changes confined to the research artifacts at this stage; implementation scope explicitly forbids production edits and new NuGet packages.
