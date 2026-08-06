// Coder CLI (.NET 11 port) — OpenAI-compatible provider catalog.
// Central definition of endpoints, credential env vars, and URL inference rules.
// Ported from src/providers/compat_catalog.ts.

using Coder.Types;

namespace Coder.Providers;

/// <summary>OpenAI-compatible provider definition.</summary>
public sealed record CompatProviderDef(
    string Id,
    ProviderCapabilities Caps,
    string[] EnvKeys,
    string DefaultBaseURL,
    string[] UrlPatterns,
    string? CnBaseURL = null,
    string? ApiKeyFallback = null);

/// <summary>
/// All OpenAI-compatible providers (excluding the MiniMax / DeepSeek / Kimi
/// dedicated modules, which live in <see cref="ProviderRegistry"/>).
/// </summary>
public static class CompatCatalog
{
    private static ProviderCapabilities Caps(
        string display, string envVar, string model, int ctx, int maxCtx,
        bool images, bool thinking, bool requiresKey = true) =>
        new(Tools: true, Streaming: true, SystemChannel: false, Images: images,
            PromptCaching: false, Thinking: thinking, RequiresApiKey: requiresKey,
            DisplayName: display, ApiKeyEnvVar: envVar, DefaultModel: model,
            DefaultContextWindow: ctx, MaxContextWindow: maxCtx);

    public static readonly IReadOnlyList<CompatProviderDef> All = new List<CompatProviderDef>
    {
        // 通义千问（DashScope OpenAI 兼容）
        new("qwen",
            Caps("Qwen", "DASHSCOPE_API_KEY", "qwen-plus", 128_000, 1_000_000, images: true, thinking: true),
            new[] { "DASHSCOPE_API_KEY", "QWEN_API_KEY", "OPENAI_API_KEY" },
            "https://dashscope.aliyuncs.com/compatible-mode/v1",
            new[] { "dashscope", "aliyuncs", "qwen" }),

        // 智谱 GLM
        new("zhipu",
            Caps("Zhipu", "ZHIPU_API_KEY", "glm-4-plus", 128_000, 128_000, images: true, thinking: true),
            new[] { "ZHIPU_API_KEY", "GLM_API_KEY", "OPENAI_API_KEY" },
            "https://open.bigmodel.cn/api/paas/v4",
            new[] { "bigmodel", "zhipu", "glm" }),

        // 硅基流动
        new("siliconflow",
            Caps("SiliconFlow", "SILICONFLOW_API_KEY", "deepseek-ai/DeepSeek-V3", 64_000, 128_000, images: false, thinking: true),
            new[] { "SILICONFLOW_API_KEY", "OPENAI_API_KEY" },
            "https://api.siliconflow.cn/v1",
            new[] { "siliconflow" },
            CnBaseURL: "https://api.siliconflow.cn/v1"),

        // Ollama 本地推理
        new("ollama",
            Caps("Ollama", "OLLAMA_API_KEY", "llama3.2", 32_000, 128_000, images: true, thinking: false, requiresKey: false),
            new[] { "OLLAMA_API_KEY" },
            "http://127.0.0.1:11434/v1",
            new[] { "11434", "ollama" },
            ApiKeyFallback: "ollama"),

        // Groq 高速推理
        new("groq",
            Caps("Groq", "GROQ_API_KEY", "llama-3.3-70b-versatile", 128_000, 128_000, images: false, thinking: false),
            new[] { "GROQ_API_KEY", "OPENAI_API_KEY" },
            "https://api.groq.com/openai/v1",
            new[] { "groq" }),

        // Mistral AI
        new("mistral",
            Caps("Mistral", "MISTRAL_API_KEY", "mistral-large-latest", 128_000, 128_000, images: true, thinking: false),
            new[] { "MISTRAL_API_KEY", "OPENAI_API_KEY" },
            "https://api.mistral.ai/v1",
            new[] { "mistral" }),

        // OpenRouter 聚合路由
        new("openrouter",
            Caps("OpenRouter", "OPENROUTER_API_KEY", "anthropic/claude-sonnet-4", 200_000, 200_000, images: true, thinking: true),
            new[] { "OPENROUTER_API_KEY", "OPENAI_API_KEY" },
            "https://openrouter.ai/api/v1",
            new[] { "openrouter" }),

        // Google Gemini（OpenAI 兼容端点）
        new("gemini",
            Caps("Gemini", "GEMINI_API_KEY", "gemini-2.0-flash", 1_000_000, 1_000_000, images: true, thinking: true),
            new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "OPENAI_API_KEY" },
            "https://generativelanguage.googleapis.com/v1beta/openai/",
            new[] { "generativelanguage", "googleapis", "gemini" }),
    };

    public static CompatProviderDef? ById(string id)
    {
        var lower = id.ToLowerInvariant();
        foreach (var def in All) if (def.Id == lower) return def;
        return null;
    }

    public static string[] Ids => All.Select(d => d.Id).ToArray();

    /// <summary>Infer an OpenAI-compatible provider id from a URL fragment.</summary>
    public static string? InferFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var lower = url!.ToLowerInvariant();
        foreach (var def in All)
            if (def.UrlPatterns.Any(p => lower.Contains(p))) return def.Id;
        return null;
    }

    /// <summary>Infer a provider from configured API key env vars (catalog order).</summary>
    public static string? InferFromEnv()
    {
        foreach (var def in All)
        {
            foreach (var key in def.EnvKeys)
            {
                if (key == "OPENAI_API_KEY") continue;
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) return def.Id;
            }
        }
        return null;
    }

    /// <summary>Resolve the default base URL, honouring CN_MODE and OLLAMA_HOST.</summary>
    public static string ResolveBaseUrl(CompatProviderDef def, bool cnMode)
    {
        if (cnMode && !string.IsNullOrEmpty(def.CnBaseURL)) return def.CnBaseURL!;
        if (def.Id == "ollama")
        {
            var host = Environment.GetEnvironmentVariable("OLLAMA_HOST")
                       ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL");
            if (!string.IsNullOrEmpty(host))
            {
                var b = host!.TrimEnd('/');
                return b.EndsWith("/v1") ? b : $"{b}/v1";
            }
        }
        return def.DefaultBaseURL;
    }
}
