using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace ProjectSandbox.Core;

/// <summary>
/// 存档读档 v1（纯 C# 数据逻辑层 + Godot FileAccess I/O）。
/// 对齐 docs/存档格式v1.md 冻结格式，禁止静默改字段语义。
///
/// 职责：
/// 1. Save(slotIndex, snapshot) → 序列化为 JSON 明文写入 user://save/slot_N.json；
/// 2. Load(slotIndex) → 反序列化 + 版本号门禁 + 迁移（预留） → 返回 SaveData；
/// 3. Delete(slotIndex) / ListSlots()：槽位管理。
///
/// 设计要点（经验 #2285998 教训）：
/// - SaveData 为完整快照结构体——对外契约稳定，内部字段变更走版本号+迁移；
/// - 迁移清单驻留 SaveSystem 内部，不接受 data/ JSON 提供的迁移脚本；
/// - 写前备份：迁移开始前原文件复制为 .bak，失败回滚；
/// - 外部通过 SaveSnapshot / LoadInto 桥接游戏层实体 ↔ SaveData（与 CraftingSystem 的 itemIdResolver 同模式）。
/// </summary>
public sealed class SaveSystem
{
    /// <summary>存档格式版本号——结构变更时 +1，对应迁移函数。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>存档目录（Godot user:// 可写区）。</summary>
    public const string SaveDir = "user://save/";

    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // -------- 主接口 --------

    /// <summary>
    /// 写入存档。slotIndex=0 为自动存档，≥1 为手动存档。
    /// 写入前自动创建目录；写成功后更新 SaveData.Header.UpdatedAtUtc + PlayTimeSeconds。
    /// </summary>
    public bool Save(int slotIndex, SaveData data)
    {
        if (data == null) return false;

        try
        {
            EnsureDir();
            string path = SlotPath(slotIndex);

            // 更新头部元数据
            data.Header ??= new SaveHeader();
            data.Header.UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // 生成 save_id（新建时）
            if (string.IsNullOrEmpty(data.Header.SaveId))
                data.Header.SaveId = Guid.NewGuid().ToString("N");

            string json = JsonSerializer.Serialize(data, _jsonOpts);
            using (var fa = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write))
                fa.StoreString(json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 读取存档。
    /// - 文件不存在 → null；
    /// - format_version ≠ CurrentFormatVersion → 尝试迁移（v1 预留，当前直接拒绝不兼容版本）；
    /// - 迁移前自动备份原文件为 .bak。
    /// </summary>
    public SaveData? Load(int slotIndex)
    {
        string path = SlotPath(slotIndex);
        if (!Godot.FileAccess.FileExists(path)) return null;

        try
        {
            string json = Godot.FileAccess.GetFileAsString(path);
            using var doc = JsonDocument.Parse(json);

            // 版本号门禁
            int formatVersion = doc.RootElement.TryGetProperty("format_version", out var v) ? v.GetInt32() : 0;
            if (formatVersion == 0) return null; // 无版本号 = 非法存档

            if (formatVersion != CurrentFormatVersion)
            {
                // v1 预留迁移入口——当前只接受 CurrentFormatVersion
                // 未来：if (formatVersion < CurrentFormatVersion) RunMigrations(path, formatVersion, json);
                return null; // 版本不兼容
            }

            return JsonSerializer.Deserialize<SaveData>(json, _jsonOpts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>删除指定槽位存档。</summary>
    public bool Delete(int slotIndex)
    {
        string path = SlotPath(slotIndex);
        if (!Godot.FileAccess.FileExists(path)) return false;
        try
        {
            DirAccess.RemoveAbsolute(path);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>列出 user://save/ 下所有 slot_N.json 文件，返回槽位索引列表（升序）。</summary>
    public List<int> ListSlots()
    {
        var result = new List<int>();
        if (!DirAccess.DirExistsAbsolute(SaveDir)) return result;

        using var dir = DirAccess.Open(SaveDir);
        if (dir == null) return result;

        dir.ListDirBegin();
        string fileName;
        while ((fileName = dir.GetNext()) != "")
        {
            if (!fileName.StartsWith("slot_") || !fileName.EndsWith(".json")) continue;
            string num = fileName.Substring(5, fileName.Length - 10); // "slot_0.json" → "0"
            if (int.TryParse(num, out int idx)) result.Add(idx);
        }
        result.Sort();
        return result;
    }

    // -------- 工具 --------

    public static string SlotPath(int slotIndex) => $"{SaveDir}slot_{slotIndex}.json";

    private static void EnsureDir()
    {
        if (!DirAccess.DirExistsAbsolute(SaveDir))
            DirAccess.MakeDirRecursiveAbsolute(SaveDir);
    }

    // ================================================================
    // 以下为存档格式 v1 数据结构（对齐 docs/存档格式v1.md）
    // ================================================================

    public sealed class SaveData
    {
        [JsonPropertyName("format_version")]
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public SaveHeader Header { get; set; } = new();
        public SavePlayer Player { get; set; } = new();
        public SaveWorld World { get; set; } = new();
        public SaveItems Items { get; set; } = new();
        public SaveNpc Npc { get; set; } = new() { Gated = true, Entries = new List<object>() };
        public SaveProgress Progress { get; set; } = new();
        public SaveMigration Migration { get; set; } = new();
        public SaveSettings Settings { get; set; } = new();
    }

    public sealed class SaveHeader
    {
        [JsonPropertyName("game_id")] public string GameId { get; set; } = "project_sandbox";
        [JsonPropertyName("save_id")] public string SaveId { get; set; } = "";
        public long Seed { get; set; }
        [JsonPropertyName("created_at_utc")] public long CreatedAtUtc { get; set; }
        [JsonPropertyName("updated_at_utc")] public long UpdatedAtUtc { get; set; }
        [JsonPropertyName("play_time_seconds")] public long PlayTimeSeconds { get; set; }
    }

    public sealed class SavePlayer
    {
        public double[] Pos { get; set; } = new double[2]; // [x, y]
        public int Hp { get; set; }
        [JsonPropertyName("max_hp")] public int MaxHp { get; set; }
        public int Mp { get; set; }
        [JsonPropertyName("max_mp")] public int MaxMp { get; set; }
        public int Coins { get; set; }
        [JsonPropertyName("hotbar_size")] public int HotbarSize { get; set; } = 5;
        public List<SaveInventorySlot> Inventory { get; set; } = new();
        public SaveEquipment Equipment { get; set; } = new();
        [JsonPropertyName("unlocked_recipes")] public List<string> UnlockedRecipes { get; set; } = new();
    }

    public sealed class SaveInventorySlot
    {
        public int Slot { get; set; }
        [JsonPropertyName("item_id")] public string ItemId { get; set; } = "";
        public int Count { get; set; }
        public int? Durability { get; set; }
    }

    public sealed class SaveEquipment
    {
        [JsonPropertyName("main_hand")] public string? MainHand { get; set; }
        [JsonPropertyName("off_hand")] public string? OffHand { get; set; }
        public List<string?> Armor { get; set; } = new();
    }

    public sealed class SaveWorld
    {
        public string Name { get; set; } = "";
        public SaveWorldSize Size { get; set; } = new();
        [JsonPropertyName("time_of_day")] public double TimeOfDay { get; set; }
        [JsonPropertyName("day_count")] public int DayCount { get; set; }
        public List<SaveChunk> Chunks { get; set; } = new();
        [JsonPropertyName("world_flags")] public Dictionary<string, bool> WorldFlags { get; set; } = new();
    }

    public sealed class SaveWorldSize
    {
        [JsonPropertyName("width_tiles")] public int WidthTiles { get; set; }
        [JsonPropertyName("height_tiles")] public int HeightTiles { get; set; }
    }

    public sealed class SaveChunk
    {
        public int Cx { get; set; }
        public int Cy { get; set; }
        public string Blocks { get; set; } = ""; // base64(deflate)
        public string? Walls { get; set; }
    }

    public sealed class SaveItems
    {
        public List<SaveDrop> Drops { get; set; } = new();
        public List<SaveChest> Chests { get; set; } = new();
    }

    public sealed class SaveDrop
    {
        public long Id { get; set; }
        [JsonPropertyName("item_id")] public string ItemId { get; set; } = "";
        public int Count { get; set; }
        public double[] Pos { get; set; } = new double[2];
        [JsonPropertyName("expires_in_seconds")] public double ExpiresInSeconds { get; set; }
    }

    public sealed class SaveChest
    {
        public double[] Pos { get; set; } = new double[2];
        public List<SaveInventorySlot> Slots { get; set; } = new();
    }

    public sealed class SaveNpc
    {
        public bool Gated { get; set; } = true;
        public List<object> Entries { get; set; } = new();
    }

    public sealed class SaveProgress
    {
        [JsonPropertyName("bosses_defeated")] public List<string> BossesDefeated { get; set; } = new();
        [JsonPropertyName("events_cleared")] public List<string> EventsCleared { get; set; } = new();
        public List<string> Milestones { get; set; } = new();
        [JsonPropertyName("unlocked_stations")] public List<string> UnlockedStations { get; set; } = new();
    }

    public sealed class SaveMigration
    {
        [JsonPropertyName("migrated_from")] public int? MigratedFrom { get; set; }
        public List<int> Applied { get; set; } = new();
    }

    /// <summary>游戏设置（音量/按键/画质等，存档格式 v1 新增节）。</summary>
    public sealed class SaveSettings
    {
        public SaveAudio Audio { get; set; } = new();
        public SaveVideo Video { get; set; } = new();
        public SaveInput Input { get; set; } = new();
    }

    public sealed class SaveAudio
    {
        public double Master { get; set; } = 1.0;
        public double Bgm { get; set; } = 0.8;
        public double Sfx { get; set; } = 1.0;
    }

    public sealed class SaveVideo
    {
        public bool Fullscreen { get; set; }
        public int Resolution { get; set; } = 1;
        public bool VSync { get; set; } = true;
    }

    public sealed class SaveInput
    {
        public Dictionary<string, string> Keybinds { get; set; } = new();
    }
}
