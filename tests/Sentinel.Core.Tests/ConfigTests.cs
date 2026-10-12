using Sentinel.Core.Config;
using Sentinel.Core.Ipc;
using Sentinel.Core.Models;
using Xunit;

namespace Sentinel.Core.Tests;

public class ConfigStoreTests
{
    private static string TempPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"sentinel-{tag}-{Guid.NewGuid():N}.json");

    [Fact]
    public void A_config_written_to_disk_can_travel_over_the_pipe()
    {
        var path = TempPath("transport");
        try
        {
            var config = new AppConfig();
            config.Policies.Add(new Policy { Id = "p", Actions = { ActionType.Notify, ActionType.Close } });
            config.Targets.Add(new Target
            {
                Id = "t",
                ProcessNames = { "msedge" },
                Rules =
                {
                    new Rule
                    {
                        Id = "r",
                        PolicyId = "p",
                        Match = new MatchSpec { Type = MatchKind.Regex, Fields = { MatchField.Url } },
                        Pause = new PauseOptions { Enabled = true, EveryDays = 2, Minutes = 15 },
                    },
                },
            });

            ConfigStore.Save(config, path);

            // The file writes enums as names — it is meant to be read and edited by hand — while the
            // pipe used to write them as numbers, so the same config had two shapes depending on
            // which way it travelled. Taking one off disk and sending it over the pipe failed with
            // "配置内容无法解析" and nothing to say why. Both shapes are accepted now.
            // Fully qualified: `Ipc` is both the namespace and the type, and inside Sentinel.Core.Tests
            // the bare name resolves to the namespace.
            var sent = Sentinel.Core.Ipc.Ipc.Deserialize<AppConfig>(File.ReadAllText(path));

            Assert.NotNull(sent);
            Assert.Equal(MatchKind.Regex, sent!.Targets[0].Rules[0].Match.Type);
            Assert.Equal(MatchField.Url, sent.Targets[0].Rules[0].Match.Fields[0]);
            Assert.Equal([ActionType.Notify, ActionType.Close], sent.Policies[0].Actions);

            Assert.True(sent.Targets[0].Rules[0].Pause.Enabled);
            Assert.Equal(2, sent.Targets[0].Rules[0].Pause.EveryDays);
            Assert.Equal(15, sent.Targets[0].Rules[0].Pause.Minutes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Round_trips()
    {
        var path = TempPath("roundtrip");
        try
        {
            var config = ConfigStore.Default();
            config.Targets.Add(new Target { Id = "t1", DisplayName = "Test" });

            ConfigStore.Save(config, path);
            var loaded = ConfigStore.LoadOrDefault(path);

            Assert.Equal("t1", Assert.Single(loaded.Targets).Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_config_falls_back_to_default()
    {
        var path = TempPath("corrupt");
        try
        {
            File.WriteAllText(path, "{ not valid json ");
            var loaded = ConfigStore.LoadOrDefault(path);

            Assert.NotNull(loaded);
            Assert.Empty(loaded.Targets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_falls_back_to_default()
    {
        var loaded = ConfigStore.LoadOrDefault(TempPath("missing"));
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Targets);
    }

    [Fact]
    public void Enums_serialize_as_strings()
    {
        var path = TempPath("enum");
        try
        {
            var config = new AppConfig();
            config.Targets.Add(new Target
            {
                Id = "t1",
                Rules =
                {
                    new Rule
                    {
                        Id = "r1",
                        Match = new MatchSpec { Type = MatchKind.Regex, Fields = { MatchField.Body } },
                    },
                },
            });

            ConfigStore.Save(config, path);
            var json = File.ReadAllText(path);

            Assert.Contains("\"Regex\"", json);
            Assert.Contains("\"Body\"", json);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
