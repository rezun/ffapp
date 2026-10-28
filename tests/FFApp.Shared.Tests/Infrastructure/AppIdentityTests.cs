using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class AppIdentityTests
{
    [Fact]
    public void AppDataDirectory_EndsWithTheIdentitysFolder()
    {
        var path = AppPaths.AppDataDirectory(new AppIdentity("FFRux", "ffrux"));

        Path.GetFileName(path).Should().Be("ffrux");
        Path.IsPathRooted(path).Should().BeTrue();
    }

    [Fact]
    public void AppDataDirectory_SeparatesApps()
    {
        var ffapp = AppPaths.AppDataDirectory(new AppIdentity("FFApp", "ffapp"));
        var ffrux = AppPaths.AppDataDirectory(new AppIdentity("FFRux", "ffrux"));

        ffrux.Should().NotBe(ffapp);
        Path.GetDirectoryName(ffrux).Should().Be(Path.GetDirectoryName(ffapp));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_RejectsBlankName(string name)
    {
        var create = () => new AppIdentity(name, "ffrux");

        create.Should().Throw<ArgumentException>().WithParameterName("Name");
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("ff/rux")]
    [InlineData("ff\\rux")]
    public void Constructor_RejectsDataDirectoryThatIsNotASingleFolder(string directoryName)
    {
        var create = () => new AppIdentity("FFRux", directoryName);

        create.Should().Throw<ArgumentException>().WithParameterName("DataDirectoryName");
    }

    [Fact]
    public void Identities_WithSameValues_AreEqual()
    {
        new AppIdentity("FFRux", "ffrux").Should().Be(new AppIdentity("FFRux", "ffrux"));
    }
}
