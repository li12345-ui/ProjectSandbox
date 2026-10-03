using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace ProjectSandbox.Core;

/// <summary>
/// 输入服务：键鼠 + 手柄的统一 action 映射、重映射、配置读写。
/// 设计要点：
/// (1) 静态单例 Instance——Godot Node 无法构造注入的务实让步（与 ServiceLocator 同模式）；
/// (2) 默认绑定来自 data/InputMap.json（IDataLoader 读取，data/ 只读）；
/// (3) 用户重映射存档写 user://input_map_save.json（可写区），启动时覆盖默认；
/// (4) 所有 action 在 Initialize 里一次性注册到 Godot.InputMap，之后消费者用 IsActionPressed 等查询，
///     避免运行时 action 不存在触发引擎报错（经验 #301911 教训）；
/// (5) 不依赖 Godot 编辑器 Input Map 配置——程序化 AddAction + AddEvent，零 project.godot 改动；
/// (6) Initialize 只应调用一次（IsInitialized 短路），重映射不走重建而走 Godot.InputMap 运行时修改。
/// </summary>
public sealed class InputService
{
    public static InputService Instance { get; } = new();

    /// <summary>是否已完成初始化（action 注册完毕）。未初始化时查询任何 action 都会抛异常。</summary>
    public bool IsInitialized { get; private set; }

    private static readonly string SavePath = "user://input_map_save.json";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>当前生效的 action 配置（重映射 UI 接线后需同步更新）。</summary>
    private InputMapCatalog? _catalog;

    private InputService() { }

    // -------- 初始化 --------

    /// <summary>
    /// 程序化构建 Godot.InputMap：默认绑定（data/InputMap.json）→ 覆盖（user:// 存档）→ 注册。
    /// 仅执行一次（IsInitialized 短路）。
    /// </summary>
    public void Initialize(IDataLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (IsInitialized) return;

        var catalog = loader.Load<InputMapCatalog>("InputMap.json");
        var merged = catalog;

        if (Godot.FileAccess.FileExists(SavePath))
        {
            try
            {
                merged = LoadSavedMapping(catalog);
            }
            catch (Exception ex)
            {
                GD.PushWarning($"[InputService] 用户存档解析失败，回退默认：{ex.Message}");
            }
        }

        ApplyToGodotInputMap(merged);
        _catalog = merged;
        IsInitialized = true;
        GD.Print($"[InputService] 已注册 {merged.Actions.Count} 个 action");
    }

    // -------- 重映射读写 --------

    /// <summary>
    /// 把当前 Godot.InputMap 全部 action 的 bindings 序列化到 user://。
    /// 供重映射 UI 调用（暂未接线，本任务提供底座）。
    /// </summary>
    public void SaveMapping()
    {
        if (!IsInitialized || _catalog == null)
            throw new InvalidOperationException("InputService 未初始化，无法保存映射");

        using var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Write);
        if (file == null)
        {
            GD.PushError($"[InputService] 写入 {SavePath} 失败");
            return;
        }
        file.StoreString(JsonSerializer.Serialize(_catalog, JsonOpts));
    }

    // -------- 私有：JSON → Godot.InputMap --------

    private static void ApplyToGodotInputMap(InputMapCatalog catalog)
    {
        foreach (var action in catalog.Actions)
        {
            if (!Godot.InputMap.HasAction(action.Name))
                Godot.InputMap.AddAction(action.Name);

            foreach (var binding in action.Bindings)
            {
                var evt = BuildInputEvent(binding);
                if (evt != null)
                    Godot.InputMap.ActionAddEvent(action.Name, evt);
                else
                    GD.PushWarning($"[InputService] 跳过无法解析的 binding：{binding}");
            }
        }
    }

    private static InputEvent? BuildInputEvent(InputBinding binding)
    {
        // 用字符串匹配而非 enum——JsonDataLoader 走默认 Deserialize，
        // enum 按整数解析，"keyboard" 等字符串无法匹配；string 类型天生兼容
        switch (binding.Type)
        {
            case "keyboard":
                if (binding.Key == null) return null;
                if (Enum.TryParse<Key>(StripPrefix(binding.Key, "Key."), out var key))
                    return new InputEventKey { Keycode = key };
                break;

            case "mouse":
                if (binding.Button == null) return null;
                if (Enum.TryParse<MouseButton>(StripPrefix(binding.Button, "MouseButton."), out var mb))
                    return new InputEventMouseButton { ButtonIndex = mb };
                break;

            case "gamepad_button":
                if (binding.Button == null) return null;
                if (Enum.TryParse<JoyButton>(StripPrefix(binding.Button, "JoyButton."), out var jb))
                    return new InputEventJoypadButton { ButtonIndex = jb };
                break;

            case "gamepad_axis":
                if (binding.Axis == null) return null;
                if (Enum.TryParse<JoyAxis>(StripPrefix(binding.Axis, "JoyAxis."), out var ja))
                    return new InputEventJoypadMotion { Axis = ja, AxisValue = (float?)binding.AxisValue ?? 1f };
                break;
        }
        return null;
    }

    private static string StripPrefix(string s, string prefix)
        => s.StartsWith(prefix, StringComparison.Ordinal) ? s.Substring(prefix.Length) : s;

    // -------- 私有：user:// 存档 → 合并覆盖默认 --------

    private static InputMapCatalog LoadSavedMapping(InputMapCatalog defaults)
    {
        using var file = Godot.FileAccess.Open(SavePath, Godot.FileAccess.ModeFlags.Read);
        if (file == null) return defaults;

        var json = file.GetAsText();
        var saved = JsonSerializer.Deserialize<InputMapCatalog>(json, JsonOpts);
        if (saved == null || saved.Actions == null) return defaults;

        // 以存档为准——存档里有的 action 全用存档 binding；存档缺的保留默认
        var savedDict = new Dictionary<string, InputAction>(StringComparer.Ordinal);
        foreach (var a in saved.Actions) savedDict[a.Name] = a;

        var merged = new InputMapCatalog { Version = defaults.Version, Actions = new List<InputAction>() };
        foreach (var def in defaults.Actions)
        {
            merged.Actions.Add(savedDict.TryGetValue(def.Name, out var s) ? s : def);
        }
        return merged;
    }
}

// -------- 数据模型（internal：仅供 InputService 内部 JSON 反序列化） --------

internal sealed class InputMapCatalog
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("actions")]
    public List<InputAction> Actions { get; set; } = new();
}

internal sealed class InputAction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("bindings")]
    public List<InputBinding> Bindings { get; set; } = new();
}

internal sealed class InputBinding
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("button")]
    public string? Button { get; set; }

    [JsonPropertyName("axis")]
    public string? Axis { get; set; }

    [JsonPropertyName("axis_value")]
    public double? AxisValue { get; set; }

    public override string ToString()
        => Type switch
        {
            "keyboard" => $"key={Key}",
            "mouse" => $"mouse={Button}",
            "gamepad_button" => $"gpbtn={Button}",
            "gamepad_axis" => $"gpaxis={Axis}({AxisValue})",
            _ => $"type={Type}"
        };
}
