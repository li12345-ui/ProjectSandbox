using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProjectSandbox.Core;

namespace ProjectSandbox.World;

/// <summary>块定义数据载体：与 blocks.json 条目一一对应（snake_case 契约映射）。</summary>
public sealed class BlockDef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("solid")]
    public bool Solid { get; set; } = true;

    [JsonPropertyName("minable")]
    public bool Minable { get; set; } = true;

    [JsonPropertyName("hardness")]
    public int Hardness { get; set; } = 1;
}

/// <summary>
/// 物块注册表：加载 data/blocks.json，维护 字符串 ID ↔ 运行时索引 映射。
/// 索引 0 保留为空块；合法索引从 1 开始，BlockGrid 直接存索引以省内存。
/// 运行时查询用 ushort 索引（热路径零字符串比较），字符串 ID 仅在加载/编辑期使用。
/// </summary>
public sealed class BlockRegistry
{
    private readonly List<BlockDef> _defs = new();
    private readonly Dictionary<string, ushort> _indexById = new(StringComparer.Ordinal);

    public BlockRegistry(IDataLoader loader)
    {
        var catalog = loader.Load<BlockCatalog>("blocks.json");
        var blocks = catalog.Blocks;
        if (blocks.Count > ushort.MaxValue)
            throw new InvalidOperationException($"物块数量 {blocks.Count} 超出 ushort 索引容量");

        for (ushort i = 0; i < blocks.Count; i++)
        {
            var def = blocks[i];
            if (string.IsNullOrEmpty(def.Id))
                throw new InvalidOperationException($"blocks.json 第 {i + 1} 条缺少 id");
            if (_indexById.ContainsKey(def.Id))
                throw new InvalidOperationException($"物块 id 重复：{def.Id}");
            _defs.Add(def);
            _indexById.Add(def.Id, (ushort)(i + 1)); // 索引 0 保留为空块
        }
    }

    /// <summary>已注册的块数量（不含空块）。</summary>
    public int Count => _defs.Count;

    /// <summary>按字符串 ID 取运行时索引；未知 ID 抛异常（数据错配尽早暴露）。</summary>
    public ushort GetIndex(string blockId)
    {
        if (!_indexById.TryGetValue(blockId, out var index))
            throw new KeyNotFoundException($"未知物块 id：{blockId}");
        return index;
    }

    /// <summary>按运行时索引取定义；索引 0（空块）无定义，抛异常。</summary>
    public BlockDef GetDef(ushort index)
    {
        if (index == 0 || index > (ushort)_defs.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"非法物块索引：{index}");
        return _defs[index - 1];
    }

    /// <summary>空块不阻挡通行。</summary>
    public bool IsSolid(ushort index) => index != 0 && GetDef(index).Solid;

    /// <summary>空块不可挖。</summary>
    public bool IsMinable(ushort index) => index != 0 && GetDef(index).Minable;

    private sealed class BlockCatalog
    {
        [JsonPropertyName("blocks")]
        public List<BlockDef> Blocks { get; set; } = new();
    }
}
