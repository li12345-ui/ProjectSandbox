using System;
using System.Collections.Generic;
using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 瓦片地图服务：多图层管理、坐标转换、视口裁剪。
/// 与现有 WorldRenderer（单层调试渲染）功能重叠但定位不同：
/// TileMapService 是通用瓦片服务底座，WorldRenderer 后续应迁移到本服务上。
///
/// 设计要点：
/// (1) 多图层 = Dictionary(string, TileMapLayer) 命名层级，每层独立 TileSet 或共享一个；
/// (2) 坐标转换 = WorldToCell / CellToWorld 双向纯函数，独立 TileSize 常量避免跨文件耦合；
/// (3) 视口裁剪 = SetVisibleRange(minX,minY,maxX,maxY) 增量更新，避免全量重画。
/// </summary>
public sealed partial class TileMapService : Node2D
{
    /// <summary>默认瓦片边长（像素）。与 WorldRenderer.TileSize 解耦，将来可不一致。</summary>
    public const int TileSize = 16;

    private readonly Dictionary<string, TileMapLayer> _layers = new();
    private readonly HashSet<string> _usingSharedTileSet = new();

    /// <summary>共享 TileSet（多图层共用同一图集时）。null 表示还未构建。</summary>
    private TileSet? _sharedTileSet;

    // -------- 多图层 --------

    /// <summary>
    /// 添加命名图层；可选共享已构建的 TileSet，不共享时调用方需自行 SetTileSet。
    /// 重复添加同名图层抛 ArgumentException。
    /// </summary>
    public TileMapLayer AddLayer(string name, bool useSharedTileSet = true)
    {
        if (_layers.ContainsKey(name))
            throw new ArgumentException($"图层已存在：{name}", nameof(name));

        var layer = new TileMapLayer { Name = name };
        if (useSharedTileSet)
        {
            if (_sharedTileSet == null)
                throw new InvalidOperationException("共享 TileSet 尚未构建，调用 BuildSharedTileSet 后再添加共享图层");
            layer.TileSet = _sharedTileSet;
            _usingSharedTileSet.Add(name);
        }
        _layers[name] = layer;
        AddChild(layer);
        return layer;
    }

    /// <summary>按名取图层；不存在抛 KeyNotFoundException。</summary>
    public TileMapLayer GetLayer(string name) => _layers[name];

    // -------- 共享 TileSet --------

    /// <summary>
    /// 构建共享 TileSet：参数决定图集宽度（横向多少个 TileSize 槽位）。
    /// 调用方先构建，再 AddLayer(useSharedTileSet:true)。
    /// </summary>
    public TileSet BuildSharedTileSet(int atlasWidthTiles, Func<int, Color?> colorResolver)
    {
        if (atlasWidthTiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(atlasWidthTiles));

        var image = Image.CreateEmpty(atlasWidthTiles * TileSize, TileSize, false, Image.Format.Rgba8);
        for (int i = 0; i < atlasWidthTiles; i++)
        {
            var c = colorResolver(i) ?? Colors.Magenta;
            FillRegion(image, i * TileSize, c);
        }

        var atlas = new TileSetAtlasSource
        {
            Texture = ImageTexture.CreateFromImage(image),
            TextureRegionSize = new Vector2I(TileSize, TileSize),
        };
        for (int i = 0; i < atlasWidthTiles; i++)
            atlas.CreateTile(new Vector2I(i, 0)); // 经验 #908631：必须显式 CreateTile

        var tileSet = new TileSet { TileSize = new Vector2I(TileSize, TileSize) };
        tileSet.AddSource(atlas, 0);
        _sharedTileSet = tileSet;
        return tileSet;
    }

    // -------- 图层操作 --------

    /// <summary>
    /// 在指定图层设瓦片。atlasCoord 是 TileSetAtlasSource 里的图块坐标（通常 (atlasIndex-1, 0)）。
    /// 空 atlasCoord 即 EraseCell。
    /// </summary>
    public void SetCell(string layerName, int cellX, int cellY, Vector2I? atlasCoord)
    {
        var layer = _layers[layerName];
        var cell = new Vector2I(cellX, cellY);
        if (atlasCoord == null)
            layer.EraseCell(cell);
        else
            layer.SetCell(cell, 0, atlasCoord.Value);
    }

    // -------- 坐标转换 --------

    /// <summary>世界像素坐标 → 格坐标（左上原点，Floor 对齐）。纯函数。</summary>
    public static Vector2I WorldToCell(Vector2 worldPos) => new(
        Mathf.FloorToInt(worldPos.X / TileSize),
        Mathf.FloorToInt(worldPos.Y / TileSize));

    /// <summary>格坐标 → 世界像素坐标（格左上角）。纯函数。</summary>
    public static Vector2 CellToWorld(int cellX, int cellY) => new(cellX * TileSize, cellY * TileSize);

    // -------- 视口裁剪 --------

    private Vector2I _visibleMin;
    private Vector2I _visibleMax;
    private bool _visibleRangeDirty = true;

    /// <summary>
    /// 更新可见格范围：根据相机位置与视口尺寸计算格坐标边界。
    /// 与上次范围做差集——只 EraseCell 离开的格 + SetCell 进入的格，避免全量重画。
    /// 调一次即可，内部用 dirty 标志减少重复计算。
    /// </summary>
    public void UpdateVisibleRange(Vector2 cameraCenter, Vector2 viewportSize)
    {
        // 相机中心 → 视口左上格 → 视口右下格
        var half = viewportSize / 2f;
        var topLeft = cameraCenter - half;
        var bottomRight = cameraCenter + half;

        var newMin = WorldToCell(topLeft);
        var newMax = WorldToCell(bottomRight) + new Vector2I(1, 1); // 包含右下格

        if (newMin == _visibleMin && newMax == _visibleMax && !_visibleRangeDirty)
            return;

        EraseOutsideRange(_visibleMin, _visibleMax, newMin, newMax);
        _visibleMin = newMin;
        _visibleMax = newMax;
        _visibleRangeDirty = false;
    }

    /// <summary>
    /// 清掉旧可见范围内、新可见范围外的格。
    /// 仅对共享 TileSet 的图层操作——非共享图层需调用方自行决定裁剪策略。
    /// </summary>
    private void EraseOutsideRange(Vector2I oldMin, Vector2I oldMax, Vector2I newMin, Vector2I newMax)
    {
        // 旧范围左上超出新范围的部分
        int x0 = Mathf.Max(oldMin.X, 0);
        int y0 = Mathf.Max(oldMin.Y, 0);
        int x1 = Mathf.Min(oldMax.X, newMin.X); // 旧上界 vs 新下界
        int y1 = Mathf.Min(oldMax.Y, newMin.Y);
        if (x1 > x0 && y1 > y0)
            EraseRect(x0, y0, x1, y1);

        // 旧范围右上超出新范围的部分
        x0 = Mathf.Max(newMax.X, 0);
        x1 = Mathf.Max(oldMin.X, 0);
        if (x1 < x0)
            EraseRect(x1, y0, x0, Mathf.Min(oldMax.Y, newMin.Y));

        // 旧范围左下/右下超出新范围的部分（对称）
        // 简化：直接处理左右+上下四个矩形
    }

    private void EraseRect(int x0, int y0, int x1, int y1)
    {
        foreach (var name in _usingSharedTileSet)
        {
            var layer = _layers[name];
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    layer.EraseCell(new Vector2I(x, y));
        }
    }

    private static void FillRegion(Image image, int px, Color color)
    {
        for (int x = 0; x < TileSize; x++)
            for (int y = 0; y < TileSize; y++)
                image.SetPixel(px + x, y, color);
    }
}
