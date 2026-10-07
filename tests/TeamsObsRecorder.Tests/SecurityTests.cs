using TeamsObsRecorder;
using TeamsObsRecorder.Platform;
using Xunit;

namespace TeamsObsRecorder.Tests;

public class SecurityTests
{
    [Fact]
    public void Config_IsReadableByOwnerOnly_OnLinux()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Directory.CreateTempSubdirectory().FullName, "app");
        var path = Path.Combine(dir, "config.json");

        AppConfig.Load(path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
    }

    [Fact]
    public void Password_RoundTripsThroughConfigFile()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "config.json");
        var config = new AppConfig();
        config.Obs.Password = "s3cret";
        config.Save(path);

        Assert.Equal("s3cret", AppConfig.Load(path).Obs.Password);
        var json = File.ReadAllText(path);
        if (Secrets.CanProtect)
        {
            Assert.DoesNotContain("s3cret", json);
            Assert.Contains("\"password_protected\"", json);
        }
        else
        {
            Assert.Contains("\"password\": \"s3cret\"", json);
            Assert.DoesNotContain("password_protected", json);
        }
    }

    [Fact]
    public void EmptyPassword_IsNotWritten()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "config.json");
        new AppConfig().Save(path);
        Assert.Equal("", AppConfig.Load(path).Obs.Password);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("", true)]
    [InlineData("192.168.1.20", false)]
    [InlineData("obs-pc", false)]
    public void IsLocalHost(string host, bool local) => Assert.Equal(local, Secrets.IsLocalHost(host));

    [Fact]
    public void ZenityText_ShowsTitlesLiterally()
    {
        Assert.Equal("Q&amp;A &lt;b&gt;all&lt;/b&gt;", LinuxPrompt.Markup("Q&A <b>all</b>"));
    }
}
