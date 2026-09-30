# Test Implementation Plan

## Overview
Implement the requested tests for exactly three production files in the canonical xUnit test project, without production edits or package changes. Work from the dependency graph: first lock down the pure byte/path mapper, then test the Visual Studio provider's mapper contract, and finally exercise `ChatSyncService` with a mapper-capable fake and a real-provider two-PC round trip. The mapper is untested; the provider and service are partially tested, but their requested mapped behaviors are not covered. All cases below are in scope; none are deferred.

**Canonical project**: `src\CodeChatSync.Tests\CodeChatSync.Tests.csproj`  
**Solution entry point**: `src\CodeChatSync.slnx`  
**Framework**: xUnit 2.9.3 on .NET 10 (`net10.0`); implicit test-source inclusion. No new packages, project, or production edits.

## Commands
- **Build**: `dotnet build src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore`
- **Test**: `dotnet test src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkspaceDescriptorPathMapperTests|FullyQualifiedName~VisualStudioChatProviderTests|FullyQualifiedName~ChatSyncServiceTests"`
- **Discovery check**: `dotnet test src\CodeChatSync.slnx --no-restore --list-tests`
- **Lint**: No repository-specific lint command/configuration. Optional formatter convention: `dotnet format src\CodeChatSync.Tests\CodeChatSync.Tests.csproj --include src\CodeChatSync.Tests\WorkspaceDescriptorPathMapperTests.cs src\CodeChatSync.Tests\VisualStudioChatProviderTests.cs src\CodeChatSync.Tests\ChatSyncServiceTests.cs`

## Phase Summary
| Phase | Focus | Files | Est. Tests |
|-------|-------|-------|------------|
| 1 | Leaf byte/path mapper contract | 1 | 15-20 |
| 2 | Visual Studio provider mapper selection | 1 | 4-6 |
| 3 | Service mapping orchestration and real-provider E2E | 1 | 9-12 |

---

## Phase 1: Portable Descriptor Mapper

### Overview
Start with the dependency-free leaf. Direct byte-oriented assertions establish the portable/local transformation contract independently of provider and service orchestration. Use explicit byte arrays for BOM, CRLF, and invalid UTF-8 cases; compare complete output bytes, not decoded text alone. Keep the target's complete scope in the new mapper test file.

### Files to Test

#### 1. `WorkspaceDescriptorPathMapper.cs`
- **Source**: `src\CodeChatSync.Providers.VisualStudio\WorkspaceDescriptorPathMapper.cs`
- **Test File**: `src\CodeChatSync.Tests\WorkspaceDescriptorPathMapperTests.cs` (new, in canonical project)
- **Test Class**: `WorkspaceDescriptorPathMapperTests`

**Methods and members to test**:

1. `ProjectRootToken` - canonical portable project-root token.
   - Assert the exact token value `${project}` used by mapped descriptors.

2. `ToPortable(byte[], string)` - replace only paths rooted under the supplied project root.
   - Root `cwd`: map to `${project}` while retaining the trailing separator present in the original `cwd`.
   - Root `git_root`: map to `${project}` without adding a trailing separator.
   - Subfolder: map an under-root path to the token plus its relative subpath.
   - Root comparison: recognize the project root case-insensitively.
   - Outside-root and sibling-prefix paths: retain their original content and return the identical input byte-array instance when no mapping occurs.
   - Preservation: leave unrelated lines, `created_at`, and multiline escaped descriptor values unchanged while mapping the intended path field(s).
   - Encoding/line endings: preserve CRLF and a UTF-8 BOM in transformed output; compare entire byte sequences including the BOM and line endings.

3. `ToLocal(byte[], string, Func<string, bool>)` - map portable or foreign descriptor paths into a local project root.
   - Expand `${project}` against the local root and preserve the separator represented in the portable mapped path.
   - Round-trip an under-root path through `ToPortable` then `ToLocal` and compare the expected local bytes.
   - Foreign absolute path without `git_root`: map `cwd` to the local root both when the foreign path has a trailing separator and when it does not; preserve the respective separator behavior.
   - Foreign `cwd` below a foreign `git_root`: retain the subfolder below the root when relocating to the local project.
   - Existing absolute path: when injected `directoryExists` reports the path exists, preserve that absolute path rather than relocating it.
   - Relative and non-absolute values: leave them unchanged.
   - Under-root value with different casing: emit local-root casing in the mapped result.
   - Double-quoted escaped backslashes: retain quoting and escaped-backslash representation after mapping.
   - Invalid UTF-8: return the identical original byte-array instance unchanged.

### Success Criteria
- [ ] New mapper test source is in the canonical xUnit project; no separate project is created.
- [ ] Tests assert complete bytes and required input-array identity cases.
- [ ] All mapper scenarios above are implemented and pass.

---

## Phase 2: Visual Studio Provider Mapper Contract

### Overview
After the leaf mapping contract, extend the existing mid-layer provider tests. Preserve current test conventions and temporary-directory setup; no new test project or production seams are needed. This phase verifies mapper capability and sharply distinguishes top-level workspace descriptors from unrelated and nested JSONL/YAML data.

### Files to Test

#### 1. `VisualStudioChatProvider.cs`
- **Source**: `src\CodeChatSync.Providers.VisualStudio\VisualStudioChatProvider.cs`
- **Test File**: `src\CodeChatSync.Tests\VisualStudioChatProviderTests.cs` (extend existing)
- **Test Class**: `VisualStudioChatProviderTests`

**Methods and members to test**:

1. `IChatContentMapper` contract / `IsMapped`.
   - Assert a constructed `VisualStudioChatProvider` implements `IChatContentMapper`.
   - Assert a top-level `workspace.yaml` path is mapped.
   - Assert filename matching is case-insensitive, including alternate casing of `workspace.yaml`.
   - Assert `events.jsonl` is not mapped.
   - Assert a nested `checkpoints/.../workspace.yaml` is not mapped; only a top-level workspace descriptor qualifies.

### Success Criteria
- [ ] Existing provider tests remain intact and the mapper contract tests follow their xUnit style.
- [ ] All positive and negative `IsMapped` classifications above are asserted.

---

## Phase 3: Service Mapping, Guard/Backup Behavior, and Two-PC E2E

### Overview
Complete the top-layer orchestration after mapper and provider behavior are established. Extend the existing service test module with a test-local fake implementing both `IChatProvider` and `IChatContentMapper`, reusing `TempDirectory` and existing `FakeProcessGuard` / `MutableProcessGuard`. Add the real-provider cross-PC scenario in the same existing service test module, wiring two isolated temporary project/session roots and a fake process guard so no Visual Studio process is required.

### Files to Test

#### 1. `ChatSyncService.cs`
- **Source**: `src\CodeChatSync.Core\ChatSyncService.cs`
- **Test File**: `src\CodeChatSync.Tests\ChatSyncServiceTests.cs` (extend existing)
- **Test Class**: `ChatSyncServiceTests`

**Methods and behaviors to test**:

1. `Sync` with a mapper-capable fake provider - portable hashing and push.
   - For mapped local content, verify the service calculates the local comparison/baseline hash from the portable representation, rather than machine-specific local bytes.
   - Push: assert exact portable bytes are written to the provider/sync side and the result reports the expected action and baseline/state effect.
   - Verify the span overload `SyncState.ComputeHash(ReadOnlySpan<byte>)` equals the path overload `SyncState.ComputeHash(path)` for the same bytes.

2. `Sync` pull, backup, restore, and process guard.
   - Pull: assert exact local/remapped bytes are written and an existing local file is backed up; verify returned action, backup location/result, and state/baseline effect.
   - Portable-equal but differently remapped local content: when pull is allowed, restore the correct local mapping and back up the preexisting local bytes.
   - Running provider: skip pull and assert `IsBlockedByProvider` and the corresponding action/state behavior.
   - Already-local correct remapping: report `Unchanged` and prove the service does not rewrite the file (compare pre/post bytes and an observable file timestamp or equivalent no-write evidence).
   - Use explicit fake process states to cover allowed and blocked paths deterministically, without consulting actual process state.

3. `GetBackupDirectory` / backup selection as exercised by mapped pull.
   - Verify the mapped pull's backup is placed in the expected backup directory and contains the exact original local bytes; retain coverage of the existing backup policy without duplicating ordinary non-mapped scenarios.

4. Real-provider two-PC push/pull/second-sync end-to-end.
   - PC A: use a real `VisualStudioChatProvider`, a temporary source session-state root, and a source descriptor containing PC A's project path; run the service push and verify the sync copy contains `${project}`.
   - PC B: use a distinct `ProjectInfo.LocalPath`, a separate empty temporary session-state root, a real `VisualStudioChatProvider`, and the existing fake process guard; pull the portable descriptor.
   - Assert PC B's resulting session descriptor stores PC B's local path, while the synchronized copy remains portable (`${project}`), and verify the expected on-disk bytes in all relevant locations.
   - Run a second PC B sync: assert `Unchanged` and verify it does not rewrite or ping-pong the portable/local representations.
   - Keep the providers real for mapping/discovery, but use the fake guard; do not depend on a running Visual Studio process or session store.

### Success Criteria
- [ ] Service-specific mapped tests use a mapper-capable test fake and existing guard fakes; no mocking package is added.
- [ ] Push/pull bytes, portable hash behavior, backup bytes, result flags/actions, blocked pull, baseline/state, and no-rewrite behavior are asserted.
- [ ] Real-provider E2E uses distinct PC A/PC B roots, checks local and portable representations, and completes an unchanged second sync without ping-pong.
- [ ] All three phases build and pass in the canonical project; the solution discovery command lists the tests.

---

## Scope and Implementation Constraints
- The only production targets are `WorkspaceDescriptorPathMapper.cs`, `VisualStudioChatProvider.cs`, and `ChatSyncService.cs`; each is assigned to exactly one phase above.
- Keep test changes within `src\CodeChatSync.Tests`; add only the mapper test file and extend the existing provider/service test files.
- Do not add packages, create another test project, or edit production code.
- No scenarios are deferred: mapper byte/path cases, provider `IsMapped`, fake-mapper service behavior, span/path hash equivalence, and real-provider cross-PC end-to-end behavior are all included above.
