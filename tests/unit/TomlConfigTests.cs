using Coder.Config;
using Coder.Types;
using Coder.Utils;

namespace Coder.Tests;

public class TomlConfigTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "coder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // TOML 深转:嵌套表/数组/long/bool/字符串。
    [Test]
    public void TomlToDataDeepConvertsNestedStructures()
    {
        var dir = TempDir();
        try
        {
            var file = Path.Combine(dir, "settings.toml");
            File.WriteAllText(file, """
                lang = "zh-CN"
                contextWindow = 200000
                streamOutput = true
                [env]
                CODER_CLI_PROVIDER = "openai"
                [[hooks.PreToolUse]]
                matcher = "bash"
                command = "echo hi"
                timeout = 30
                blocked = true
                """);
            var layer = ConfigLoader.LoadConfig(new CliOverrides(), dir, userConfigDir: dir).layers
                .FirstOrDefault(l => l.Source.EndsWith("settings.toml"));
            Assert.True(layer != null, "user layer must be loaded");
            Assert.Equal("zh-CN", layer!.Data["lang"] as string);
            Assert.True(layer.Data["contextWindow"] is long l && l == 200000, "TOML int must be long");
            Assert.True(layer.Data["streamOutput"] is bool b && b, "TOML bool must be real bool");
            var env = layer.Data["env"] as Dictionary<string, object?>;
            Assert.Equal("openai", env?["CODER_CLI_PROVIDER"] as string);
            var hooks = layer.Data["hooks"] as Dictionary<string, object?>;
            var pre = hooks?["PreToolUse"] as List<object?>;
            Assert.Equal(1, pre?.Count ?? -1);
            var spec = pre![0] as Dictionary<string, object?>;
            Assert.Equal("bash", spec?["matcher"] as string);
            Assert.True(spec?["blocked"] is bool bb && bb, "blocked must be bool");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 优先级:CLI flags > 项目 .coder/settings.toml > 用户 settings.toml。
    [Test]
    public void PriorityIsCliOverProjectOverUser()
    {
        var dir = TempDir();
        try
        {
            var userDir = Path.Combine(dir, "user"); Directory.CreateDirectory(userDir);
            var projDir = Path.Combine(dir, "proj"); Directory.CreateDirectory(projDir);
            Directory.CreateDirectory(Path.Combine(projDir, ".coder"));
            File.WriteAllText(Path.Combine(userDir, "settings.toml"), "model = \"user-model\"\nprovider = \"openai\"\ntheme = \"dark\"\n");
            File.WriteAllText(Path.Combine(projDir, ".coder", "settings.toml"), "model = \"project-model\"\n");

            var (cfg, _) = ConfigLoader.LoadConfig(new CliOverrides(), projDir, userConfigDir: userDir);
            Assert.Equal("project-model", cfg.Model, "project must override user");
            Assert.Equal("dark", cfg.Theme.ToString().ToLowerInvariant(), "user value must survive when project lacks the key");

            var cli = new CliOverrides { Model = "cli-model" };
            var (cfg2, _) = ConfigLoader.LoadConfig(cli, projDir, userConfigDir: userDir);
            Assert.Equal("cli-model", cfg2.Model, "CLI flag must override project");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // contextWindow 为 TOML long 时正确解析。
    [Test]
    public void ContextWindowLongParsesCorrectly()
    {
        var dir = TempDir();
        try
        {
            var userDir = Path.Combine(dir, "user"); Directory.CreateDirectory(userDir);
            File.WriteAllText(Path.Combine(userDir, "settings.toml"), "provider = \"openai\"\ncontextWindow = 200000\n");
            var (cfg, _) = ConfigLoader.LoadConfig(new CliOverrides(), dir, userConfigDir: userDir);
            Assert.Equal(200000, cfg.ContextWindow);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 用户层 [env] 块注入环境变量。
    [Test]
    public void UserEnvBlockInjectsEnvironment()
    {
        var dir = TempDir();
        try
        {
            var userDir = Path.Combine(dir, "user"); Directory.CreateDirectory(userDir);
            File.WriteAllText(Path.Combine(userDir, "settings.toml"),
                "provider = \"openai\"\n[env]\nCODER_TEST_INJECTED = \"hello\"\n");
            Environment.SetEnvironmentVariable("CODER_TEST_INJECTED", null);
            ConfigLoader.LoadConfig(new CliOverrides(), dir, userConfigDir: userDir);
            Assert.Equal("hello", Environment.GetEnvironmentVariable("CODER_TEST_INJECTED"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODER_TEST_INJECTED", null);
            Directory.Delete(dir, recursive: true);
        }
    }

    // 损坏 TOML → ConfigError。
    [Test]
    public void BrokenTomlThrowsConfigError()
    {
        var dir = TempDir();
        try
        {
            var userDir = Path.Combine(dir, "user"); Directory.CreateDirectory(userDir);
            File.WriteAllText(Path.Combine(userDir, "settings.toml"), "model = \"unclosed\n[[broken]]\n");
            var threw = false;
            try { ConfigLoader.LoadConfig(new CliOverrides(), dir, userConfigDir: userDir); }
            catch (ConfigError) { threw = true; }
            Assert.True(threw, "broken TOML must throw ConfigError");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // 旧 JSON 配置不再被读取。
    [Test]
    public void LegacyJsonIsNotRead()
    {
        var dir = TempDir();
        try
        {
            var userDir = Path.Combine(dir, "user"); Directory.CreateDirectory(userDir);
            File.WriteAllText(Path.Combine(userDir, "config.json"),
                "{\"provider\":\"legacy-provider\",\"model\":\"legacy-model\"}");
            var (cfg, _) = ConfigLoader.LoadConfig(new CliOverrides(), dir, userConfigDir: userDir);
            Assert.NotEqual("legacy-provider", cfg.Provider, "legacy config.json must be ignored");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
