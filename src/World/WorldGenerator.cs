using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProjectSandbox.Core;

namespace ProjectSandbox.World;

/// <summary>
/// 世界生成器（S1 最小版）：按 data/world_gen.json 的分层模板填充 BlockGrid。
/// 当前为平面分层填充（地表壤土 → 地下页岩 → 底层坚玄武岩），噪声与矿脉留待 S1 完整任务；
/// seed 字段已入数据结构供后续噪声使用，现阶段生成是纯确定性的分层填充。
/// </summary>
public sealed class WorldGenerator
{
    private readonly WorldGenDef _def;
    private readonly BlockRegistry _registry;
    private readonly ushort[] _layerIndices;

    public WorldGenerator(IDataLoader loader, BlockRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _def = loader.Load<WorldGenDef>("world_gen.json") ?? throw new InvalidOperationException("world_gen.json 解析为空");

        if (_def.Width <= 0) throw new InvalidOperationException($"world_gen 宽度非法：{_def.Width}");
        if (_def.Height <= 0) throw new InvalidOperationException($"world_gen 高度非法：{_def.Height}");
        if (_def.AirRows < 0 || _def.AirRows >= _def.Height)
            throw new InvalidOperationException($"world_gen 空行数非法：{_def.AirRows}");
        if (_def.Layers == null || _def.Layers.Count == 0)
            throw new InvalidOperationException("world_gen 缺少分层配置");

        // 解析块 ID 为运行时索引（未知 ID 会在 GetIndex 处抛出，数据错配尽早暴露）
        _layerIndices = new ushort[_def.Layers.Count];
        var fixedRows = 0;
        for (int i = 0; i < _def.Layers.Count; i++)
        {
            var layer = _def.Layers[i];
            var isLast = i == _def.Layers.Count - 1;
            // 先解析索引再处理 -1 分支——否则末层索引留 0（空块），底层会被挖空
            _layerIndices[i] = _registry.GetIndex(layer.Block);
            if (isLast && layer.Thickness == -1) continue; // 末层 -1 = 填满剩余
            if (layer.Thickness <= 0)
                throw new InvalidOperationException($"第 {i + 1} 层厚度非法：{layer.Thickness}（仅末层可用 -1 表剩余）");
            fixedRows += layer.Thickness;
        }
        if (_def.Layers[^1].Thickness != -1)
            throw new InvalidOperationException("末层厚度须为 -1（填满剩余行），避免高度改后出现未填充区");
        if (_def.AirRows + fixedRows > _def.Height)
            throw new InvalidOperationException($"分层总厚 {fixedRows} + 空行 {_def.AirRows} 超出高度 {_def.Height}");
    }

    public int Width => _def.Width;
    public int Height => _def.Height;

    /// <summary>生成新网格：按空行 + 分层顺序自上而下填充。</summary>
    public BlockGrid Generate()
    {
        var grid = new BlockGrid(_def.Width, _def.Height);

        // 预计算每行对应的块索引（行数 ≤ 高度，逐行一次判定）
        var rowBlock = new ushort[_def.Height];
        var cursor = _def.AirRows;
        for (int i = 0; i < _def.Layers.Count; i++)
        {
            var rows = _def.Layers[i].Thickness == -1
                ? _def.Height - cursor
                : _def.Layers[i].Thickness;
            for (int r = 0; r < rows && cursor < _def.Height; r++, cursor++)
                rowBlock[cursor] = _layerIndices[i];
        }

        for (int y = 0; y < _def.Height; y++)
        {
            if (rowBlock[y] == 0) continue; // 空行保持 EmptyIndex
            for (int x = 0; x < _def.Width; x++)
                grid.Set(x, y, rowBlock[y]);
        }
        return grid;
    }

    private sealed class WorldGenDef
    {
        [JsonPropertyName("width")]
        public int Width { get; set; }

        [JsonPropertyName("height")]
        public int Height { get; set; }

        [JsonPropertyName("air_rows")]
        public int AirRows { get; set; }

        [JsonPropertyName("layers")]
        public List<WorldGenLayer> Layers { get; set; } = new();
    }

    private sealed class WorldGenLayer
    {
        [JsonPropertyName("block")]
        public string Block { get; set; } = "";

        [JsonPropertyName("thickness")]
        public int Thickness { get; set; }
    }
}
