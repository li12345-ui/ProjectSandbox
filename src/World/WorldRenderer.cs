using System;
using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 世界调试渲染器：把 BlockGrid 画到 TileMapLayer 上。
/// 占位纹理为运行时程序生成的 16×16 纯色块（原创调试用，正式美术按像素资产规范另开任务替换）；
/// 调试配色与块 ID 一一对应，仅服务"可见性验证"，不属于游戏内容数值。
/// </summary>
public sealed partial class WorldRenderer : Node2D
{
    /// <summary>块 ID → 调试纯色（原创配色，避开任何对标作品的标志性色组）。</summary>
    private static readonly System.Collections.Generic.Dictionary<string, Color> DebugColors = new()
    {
        ["tile_soil_loam"] = new Color(0.55f, 0.38f, 0.26f),   // 壤土：暖棕
        ["tile_stone_shale"] = new Color(0.42f, 0.45f, 0.50f), // 页岩：冷灰
        ["tile_bough_timber"] = new Color(0.48f, 0.33f, 0.20f),// 伐木：木褐
        ["tile_moss_lantern"] = new Color(0.72f, 0.85f, 0.45f),// 苔灯：苔绿
        ["tile_basalt_firm"] = new Color(0.20f, 0.20f, 0.24f), // 坚玄武岩：深岩黑
    };

    private const int TileSize = 16;

    private TileMapLayer _layer = null!;
    private BlockGrid _grid = null!;
    private BlockRegistry _registry = null!;

    /// <summary>构建 TileSet 并整格绘制网格。重复调用会先清空旧层。</summary>
    public void Initialize(BlockGrid grid, BlockRegistry registry)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        _layer = new TileMapLayer { Name = "DebugTiles" };
        _layer.TileSet = BuildTileSet();
        AddChild(_layer);
        PaintAll();
    }

    private TileSet BuildTileSet()
    {
        // 单图集：n 个块横向排开，每个块占一个 16×16 图块
        var count = _registry.Count;
        var image = Image.CreateEmpty(count * TileSize, TileSize, false, Image.Format.Rgba8);
        for (ushort i = 1; i <= count; i++)
        {
            var def = _registry.GetDef(i);
            var color = DebugColors.TryGetValue(def.Id, out var c) ? c : Colors.Magenta; // 未配置色=品红警示
            FillRegion(image, (i - 1) * TileSize, color);
        }

        var atlas = new TileSetAtlasSource
        {
            Texture = ImageTexture.CreateFromImage(image),
            TextureRegionSize = new Vector2I(TileSize, TileSize),
        };
        for (ushort i = 0; i < count; i++)
            atlas.CreateTile(new Vector2I(i, 0));

        var tileSet = new TileSet { TileSize = new Vector2I(TileSize, TileSize) };
        tileSet.AddSource(atlas, 0);
        return tileSet;
    }

    private static void FillRegion(Image image, int px, Color color)
    {
        for (int x = 0; x < TileSize; x++)
            for (int y = 0; y < TileSize; y++)
                image.SetPixel(px + x, y, color);
    }

    private void PaintAll()
    {
        for (int y = 0; y < _grid.Height; y++)
            for (int x = 0; x < _grid.Width; x++)
                UpdateCell(x, y);
    }

    /// <summary>刷新单格显示（供挖掘/放置后同步调用）；空块清格。</summary>
    public void UpdateCell(int x, int y)
    {
        var index = _grid.Get(x, y);
        if (index == BlockGrid.EmptyIndex)
            _layer.EraseCell(new Vector2I(x, y));
        else
            _layer.SetCell(new Vector2I(x, y), 0, new Vector2I(index - 1, 0));
    }
}
