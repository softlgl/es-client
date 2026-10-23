using System.Text.Json;
using System.Text.Json.Serialization;

namespace EsClient.Api.Services;

/// <summary>单个 AI 模型配置（OpenAI 兼容接口）。</summary>
public class AiModelConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "custom";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>思考模式使用的模型名（可选，留空则与 Model 相同）。</summary>
    public string? ThinkingModel { get; set; }
    /// <summary>
    /// JSON 模式（response_format=json_object），只在「要求模型输出 JSON」的调用上生效：
    /// null = 自动（端点报错时自动关闭并在进程内记住，推荐）、true = 强制开启、false = 关闭。
    /// </summary>
    public bool? JsonMode { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>配置文件结构：多模型配置 + 当前激活项。</summary>
public class AiSettingsFile
{
    public string ActiveId { get; set; } = "";
    public List<AiModelConfig> Models { get; set; } = [];
}

/// <summary>
/// AI 配置持久化到 App_Data/ai-settings.json（本地单人工具，明文存储与 connections.json 同级）。
/// 支持多个模型配置切换；旧版单配置格式读取时自动迁移。
/// </summary>
public class AiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>国内常用 OpenAI 兼容服务商预设（baseUrl/model 可在表单中覆盖）。</summary>
    public static readonly Dictionary<string, (string Label, string BaseUrl, string Model, string? ThinkingModel)> Presets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek"] = ("DeepSeek（深度求索）", "https://api.deepseek.com/v1", "deepseek-chat", "deepseek-reasoner"),
            ["qwen"] = ("通义千问（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", null),
            ["kimi"] = ("Kimi（月之暗面）", "https://api.moonshot.cn/v1", "moonshot-v1-8k", null),
            ["glm"] = ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", null),
            ["doubao"] = ("豆包（火山方舟）", "https://ark.cn-beijing.volces.com/api/v3", "doubao-pro-32k", null),
            ["openai"] = ("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", null),
            ["custom"] = ("自定义（任意 OpenAI 兼容接口）", "", "", null)
        };

    private readonly string _filePath;
    private readonly object _lock = new();
    private AiSettingsFile _file = new();

    public AiSettingsStore(IHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "ai-settings.json");
        Load();
    }

    public bool IsConfigured
    {
        get
        {
            lock (_lock) return ActiveNoLock()?.IsValid == true;
        }
    }

    /// <summary>当前激活的配置；active 缺失/无效时自动回落到第一条有效配置。</summary>
    public AiModelConfig Get()
    {
        lock (_lock) return ActiveNoLock() ?? new AiModelConfig();
    }

    public List<AiModelConfig> List()
    {
        lock (_lock) return _file.Models.ToList();
    }

    public string ActiveId
    {
        get
        {
            lock (_lock)
            {
                if (ActiveNoLock() == null)
                    _file.ActiveId = _file.Models.FirstOrDefault(m => m.IsValid)?.Id ?? "";
                return _file.ActiveId;
            }
        }
    }

    /// <summary>新增或更新配置（id 为空则新增），并持久化。</summary>
    public AiModelConfig Save(AiModelConfig c)
    {
        lock (_lock)
        {
            var existing = _file.Models.FirstOrDefault(m => m.Id == c.Id);
            if (existing != null)
            {
                existing.Provider = c.Provider;
                existing.BaseUrl = c.BaseUrl;
                existing.ApiKey = c.ApiKey;
                existing.Model = c.Model;
                existing.ThinkingModel = c.ThinkingModel;
                existing.JsonMode = c.JsonMode;
                c = existing;
            }
            else
            {
                _file.Models.Add(c);
            }
            if (_file.ActiveId == "" && c.IsValid) _file.ActiveId = c.Id;
            PersistNoLock();
            return c;
        }
    }

    /// <summary>切换激活配置。返回是否成功（配置需存在且有效）。</summary>
    public bool Switch(string id)
    {
        lock (_lock)
        {
            var m = _file.Models.FirstOrDefault(x => x.Id == id);
            if (m == null || !m.IsValid) return false;
            _file.ActiveId = id;
            PersistNoLock();
            return true;
        }
    }

    /// <summary>删除配置；删除激活项时回落到剩余第一条有效配置。</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            _file.Models.RemoveAll(m => m.Id == id);
            if (_file.ActiveId == id)
                _file.ActiveId = _file.Models.FirstOrDefault(m => m.IsValid)?.Id ?? "";
            PersistNoLock();
        }
    }

    private AiModelConfig? ActiveNoLock()
    {
        var active = _file.Models.FirstOrDefault(m => m.Id == _file.ActiveId);
        if (active is { IsValid: true }) return active;
        active = _file.Models.FirstOrDefault(m => m.IsValid);
        if (active != null) _file.ActiveId = active.Id;
        return active;
    }

    /// <summary>
    /// 写盘走共享的 <see cref="AtomicFile"/>「临时文件 + 原子替换」：直接覆盖目标文件时若进程中途被杀，
    /// 文件会被截断，下次启动时 Load 的兜底 catch 会把整份配置当作未配置处理（表现为「密钥莫名消失」）。
    /// 本方法的写入失败不吞——保存 AI 配置这个操作的目的就是落盘，写不进去必须报错让用户重试。
    /// </summary>
    private void PersistNoLock() =>
        AtomicFile.Write(_filePath, JsonSerializer.Serialize(_file, JsonOpts));

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Models", out _))
            {
                _file = doc.RootElement.Deserialize<AiSettingsFile>(JsonOpts) ?? new AiSettingsFile();
            }
            else
            {
                // 旧版单配置格式 → 迁移为列表；DeepSeek 自动补思考模型
                var old = doc.RootElement.Deserialize<AiModelConfig>(JsonOpts);
                if (old != null && old.IsValid)
                {
                    if (old.Provider == "deepseek" && string.IsNullOrWhiteSpace(old.ThinkingModel))
                        old.ThinkingModel = "deepseek-reasoner";
                    _file = new AiSettingsFile { ActiveId = old.Id, Models = [old] };
                    PersistNoLock();
                }
            }
        }
        catch
        {
            _file = new AiSettingsFile(); // 配置损坏时当作未配置处理
        }
    }
}
