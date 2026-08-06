using System.Text.Json;
using Coder.Config;
using Coder.Types;
using Tomlyn;

namespace Coder.Tests;

public class SetupMigrationTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "coder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // 旧 JSON(含枚举数字、env 表)→ settings.toml,旧文件保留。
    [Test]
    public void MigratesLegacyJsonToToml()
    {
        var dir = TempDir();
        try
        {
            // 模拟 ~/.coder-cli/config.json(旧目录)与 ~/.coder(新目录),共享同一 home。
            var newDir = Path.Combine(dir, "new-home", ".coder");
            var legacyDir = Path.Combine(dir, "new-home", ".coder-cli"); Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(newDir);
            Environment.SetEnvironmentVariable("CODER_USER_CONFIG_DIR", newDir);

            var legacy = Path.Combine(legacyDir, "config.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["lang"] = "zh-CN",
                ["provider"] = "openai",
                ["model"] = "gpt-4o",
                ["theme"] = 1,                 // Dark
                ["effortLevel"] = 2,           // High
                ["contextWindow"] = 128000,
                ["cnMode"] = true,
                ["env"] = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" },
            }));

            var migrated = Setup.MigrateLegacyIfPresent();
            Assert.True(migrated, "legacy config must migrate");
            var target = Path.Combine(newDir, "settings.toml");
            Assert.True(File.Exists(target), "settings.toml must be written");

            var doc = Tomlyn.Toml.Parse(File.ReadAllText(target), target);
            Assert.False(doc.HasErrors, "migrated TOML must parse: " + string.Join(";", doc.Diagnostics.Select(d => d.Message)));
            var model = doc.ToModel();
            Assert.Equal("zh-CN", model["lang"] as string);
            Assert.Equal("openai", model["provider"] as string);
            Assert.Equal("dark", model["theme"] as string, "theme enum 1 must map to dark");
            Assert.Equal("high", model["effortLevel"] as string, "effortLevel enum 2 must map to high");
            Assert.True(model["contextWindow"] is long cw && cw == 128000);
            Assert.True(File.Exists(legacy), "legacy file must be kept");

            // 迁移后的配置可被 LoadConfig 消费。
            var (cfg, _) = ConfigLoader.LoadConfig(new CliOverrides(), dir, userConfigDir: newDir);
            Assert.Equal("openai", cfg.Provider);
            Assert.Equal("dark", cfg.Theme.ToString().ToLowerInvariant());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODER_USER_CONFIG_DIR", null);
            Directory.Delete(dir, recursive: true);
        }
    }

    // target 已存在 → 不迁移。
    [Test]
    public void DoesNotMigrateWhenTargetExists()
    {
        var dir = TempDir();
        try
        {
            var newDir = Path.Combine(dir, "new", ".coder"); Directory.CreateDirectory(newDir);
            Environment.SetEnvironmentVariable("CODER_USER_CONFIG_DIR", newDir);
            File.WriteAllText(Path.Combine(newDir, "settings.toml"), "model = \"keep\"\n");
            var legacyDir = Path.Combine(dir, "new", ".coder-cli"); Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "config.json"), "{\"model\":\"drop\"}");

            Assert.False(Setup.MigrateLegacyIfPresent(), "must not migrate when settings.toml exists");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODER_USER_CONFIG_DIR", null);
            Directory.Delete(dir, recursive: true);
        }
    }

    // WriteFirstRunConfig 输出可被 Toml.Parse 往返。
    [Test]
    public void WriteFirstRunConfigRoundTrips()
    {
        var dir = TempDir();
        try
        {
            Environment.SetEnvironmentVariable("CODER_USER_CONFIG_DIR", dir);
            var file = Setup.WriteFirstRunConfig("zh-CN", ThemeMode.Dark, "openai", "sk-test-key");
            Assert.True(file.EndsWith("settings.toml"));
            var doc = Tomlyn.Toml.Parse(File.ReadAllText(file), file);
            Assert.False(doc.HasErrors, "written TOML must parse: " + string.Join(";", doc.Diagnostics.Select(d => d.Message)));
            var model = doc.ToModel();
            Assert.Equal("zh-CN", model["lang"] as string);
            Assert.Equal("dark", model["theme"] as string);
            Assert.Equal("openai", model["provider"] as string);
            var env = model["env"] as Tomlyn.Model.TomlTable;
            Assert.Equal("sk-test-key", env?["OPENAI_API_KEY"] as string, "API key must land in [env]");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODER_USER_CONFIG_DIR", null);
            Directory.Delete(dir, recursive: true);
        }
    }
}
