using CodeChatSync.Core;
using CodeChatSync.Providers.VisualStudio;

namespace CodeChatSync.Tests;

public sealed class VisualStudioChatProviderMapperTests
{
    [Fact]
    public void Provider_ImplementsContentMapperAndRecognizesTopLevelWorkspaceDescriptor()
    {
        var provider = new VisualStudioChatProvider();
        var mapper = Assert.IsAssignableFrom<IChatContentMapper>(provider);

        Assert.True(mapper.IsMapped("session-42/workspace.yaml"));
    }

    [Theory]
    [InlineData("session-42/WORKSPACE.YAML", true)]
    [InlineData("session-42/events.jsonl", false)]
    [InlineData("session-42/checkpoints/workspace.yaml", false)]
    public void IsMapped_ClassifiesOnlyTopLevelWorkspaceDescriptor(string relativePath, bool expected)
    {
        var provider = new VisualStudioChatProvider();

        var actual = provider.IsMapped(relativePath);

        Assert.Equal(expected, actual);
    }
}