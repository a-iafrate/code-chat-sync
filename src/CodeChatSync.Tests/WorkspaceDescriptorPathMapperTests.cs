using System.Text;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.Tests;

public sealed class WorkspaceDescriptorPathMapperTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mapper", "project"));
    private static readonly string LocalRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mapper", "local"));
    private static readonly char Separator = Path.DirectorySeparatorChar;

    [Fact]
    public void ProjectRootToken_IsCanonicalPortableToken()
    {
        Assert.Equal("${project}", WorkspaceDescriptorPathMapper.ProjectRootToken);
    }

    [Fact]
    public void ToPortable_MapsCwdProjectRootAndRetainsTrailingSeparator()
    {
        var input = Utf8($"cwd: {ProjectRoot}{Separator}\nname: session\n");

        var actual = WorkspaceDescriptorPathMapper.ToPortable(input, ProjectRoot);

        AssertBytes(Utf8($"cwd: ${{project}}{Separator}\nname: session\n"), actual);
    }

    [Fact]
    public void ToPortable_MapsGitRootWithoutAddingTrailingSeparator()
    {
        var input = Utf8($"git_root: {ProjectRoot}\n");

        var actual = WorkspaceDescriptorPathMapper.ToPortable(input, ProjectRoot);

        AssertBytes(Utf8("git_root: ${project}\n"), actual);
    }

    [Fact]
    public void ToPortable_MapsSubfolderToTokenAndRelativePath()
    {
        var input = Utf8($"cwd: {ProjectRoot}{Separator}src{Separator}App\n");

        var actual = WorkspaceDescriptorPathMapper.ToPortable(input, ProjectRoot);

        AssertBytes(Utf8($"cwd: ${{project}}{Separator}src{Separator}App\n"), actual);
    }

    [Fact]
    public void ToPortable_RecognizesRootCaseInsensitivelyAndUsesCanonicalToken()
    {
        var differentlyCasedRoot = ProjectRoot.ToUpperInvariant();
        var input = Utf8($"cwd: {differentlyCasedRoot}{Separator}src\n");

        var actual = WorkspaceDescriptorPathMapper.ToPortable(input, ProjectRoot);

        AssertBytes(Utf8($"cwd: ${{project}}{Separator}src\n"), actual);
    }

    [Theory]
    [InlineData("outside-folder")]
    [InlineData("sibling-prefix")]
    public void ToPortable_LeavesOutsideAndSiblingPrefixPathsUntouchedByIdentity(string scenario)
    {
        var path = scenario == "outside-folder"
            ? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "other-project", "src"))
            : ProjectRoot + "Sibling" + Separator + "src";
        var input = Utf8($"cwd: {path}\ncreated_at: 2025-03-04T05:06:07Z\n");

        var actual = WorkspaceDescriptorPathMapper.ToPortable(input, ProjectRoot);

        Assert.Same(input, actual);
        AssertBytes(Utf8($"cwd: {path}\ncreated_at: 2025-03-04T05:06:07Z\n"), actual);
    }

    [Fact]
    public void ToPortable_ChangesOnlyPathLinesAndPreservesEscapedYamlAndMetadata()
    {
        const string unrelatedLines = "name: \"first\\nsecond\\tline\"\ncreated_at: 2025-03-04T05:06:07Z\nother: keep-me\n";
        var input = Utf8($"cwd: {ProjectRoot}{Separator}chats\n{unrelatedLines}git_root: {ProjectRoot}\n");

        var actual = WorkspaceDescriptorPathMapper.ToPortable(input, ProjectRoot);

        AssertBytes(Utf8($"cwd: ${{project}}{Separator}chats\n{unrelatedLines}git_root: ${{project}}\n"), actual);
    }

    [Fact]
    public void ToPortable_PreservesUtf8BomAndCrLfInRewrittenBytes()
    {
        var body = Utf8($"cwd: {ProjectRoot}{Separator}chat\r\ncreated_at: 2025-03-04T05:06:07Z\r\n");
        var input = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();

        var actual = WorkspaceDescriptorPathMapper.ToPortable(input, ProjectRoot);
        var expected = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Utf8($"cwd: ${{project}}{Separator}chat\r\ncreated_at: 2025-03-04T05:06:07Z\r\n"))
            .ToArray();

        AssertBytes(expected, actual);
        Assert.Contains(new byte[] { 0x0D, 0x0A }, actual);
    }

    [Theory]
    [InlineData("${project}\\chats\\", "\\chats\\")]
    [InlineData("${project}/chats/", "/chats/")]
    [InlineData("${project}\\chats", "\\chats")]
    public void ToLocal_ExpandsTokenPreservingSeparatorAndTrailingStyle(string portablePath, string expectedSuffix)
    {
        var input = Utf8($"cwd: {portablePath}\n");

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, LocalRoot, _ => false);

        AssertBytes(Utf8($"cwd: {LocalRoot}{expectedSuffix}\n"), actual);
    }

    [Fact]
    public void ToLocal_RoundTripsDescriptorUnderProjectRoot()
    {
        var original = Utf8($"cwd: {ProjectRoot}{Separator}chats{Separator}one{Separator}\ngit_root: {ProjectRoot}\ncreated_at: 2025-03-04T05:06:07Z\n");

        var portable = WorkspaceDescriptorPathMapper.ToPortable(original, ProjectRoot);
        var localized = WorkspaceDescriptorPathMapper.ToLocal(portable, ProjectRoot, _ => false);

        AssertBytes(original, localized);
        AssertBytes(Utf8($"cwd: ${{project}}{Separator}chats{Separator}one{Separator}\ngit_root: ${{project}}\ncreated_at: 2025-03-04T05:06:07Z\n"), portable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToLocal_MapsForeignAbsoluteCwdWithoutGitRootToRoot(bool hasTrailingSeparator)
    {
        var foreignRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "foreign-project"));
        var foreignPath = foreignRoot + (hasTrailingSeparator ? Separator.ToString() : string.Empty);
        var input = Utf8($"cwd: {foreignPath}\nname: remote\n");
        var expectedPath = LocalRoot + (hasTrailingSeparator ? Separator.ToString() : string.Empty);

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, LocalRoot, _ => false);

        AssertBytes(Utf8($"cwd: {expectedPath}\nname: remote\n"), actual);
    }

    [Fact]
    public void ToLocal_MapsForeignWorkingDirectoryRelativeToForeignGitRoot()
    {
        var foreignRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "source", "repo"));
        var foreignWorkingDirectory = Path.Combine(foreignRoot, "src", "Feature");
        var input = Utf8($"cwd: {foreignWorkingDirectory}\ngit_root: {foreignRoot}\n");

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, LocalRoot, _ => false);

        AssertBytes(Utf8($"cwd: {Path.Combine(LocalRoot, "src", "Feature")}\ngit_root: {LocalRoot}\n"), actual);
    }

    [Fact]
    public void ToLocal_LeavesForeignAbsolutePathAloneWhenDirectoryExists()
    {
        var existingPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "worktree", "repo"));
        var input = Utf8($"cwd: {existingPath}\n");

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, LocalRoot, path => path == existingPath);

        Assert.Same(input, actual);
        AssertBytes(Utf8($"cwd: {existingPath}\n"), actual);
    }

    [Theory]
    [InlineData("relative\\folder")]
    [InlineData("C:drive-relative\\folder")]
    public void ToLocal_LeavesRelativeAndDriveRelativePathsUnchanged(string path)
    {
        var input = Utf8($"cwd: {path}\n");

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, LocalRoot, _ => false);

        Assert.Same(input, actual);
        AssertBytes(Utf8($"cwd: {path}\n"), actual);
    }

    [Fact]
    public void ToLocal_NormalizesUnderRootPathCasingToLocalRootCasing()
    {
        var differentlyCasedPath = ProjectRoot.ToUpperInvariant() + Separator + "Chats";
        var input = Utf8($"cwd: {differentlyCasedPath}\n");

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, ProjectRoot, _ => false);

        AssertBytes(Utf8($"cwd: {ProjectRoot}{Separator}Chats\n"), actual);
    }

    [Fact]
    public void ToLocal_PreservesDoubleQuotedEscapedBackslashes()
    {
        var input = Utf8("cwd: \"${project}\\\\chats\\\\one\"\n");

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, LocalRoot, _ => false);

        AssertBytes(Utf8($"cwd: \"{LocalRoot.Replace("\\", "\\\\", StringComparison.Ordinal)}\\\\chats\\\\one\"\n"), actual);
    }

    [Fact]
    public void ToLocal_ReturnsSameInputArrayForInvalidUtf8()
    {
        var input = new byte[] { (byte)'c', (byte)'w', (byte)'d', (byte)':', (byte)' ', 0xC3, 0x28, (byte)'\n' };

        var actual = WorkspaceDescriptorPathMapper.ToLocal(input, LocalRoot, _ => false);

        Assert.Same(input, actual);
        Assert.Equal(new byte[] { (byte)'c', (byte)'w', (byte)'d', (byte)':', (byte)' ', 0xC3, 0x28, (byte)'\n' }, actual);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static void AssertBytes(byte[] expected, byte[] actual) => Assert.Equal(expected, actual);
}
