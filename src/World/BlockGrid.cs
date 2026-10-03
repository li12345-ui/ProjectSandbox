namespace ProjectSandbox.World;

/// <summary>
/// 物块网格：世界层的地块存储（S2 挖掘放置的底座）。
/// 索引 0 保留为"空块"，注册表映射的块索引从 1 开始。
/// 坐标系与 Godot 2D 一致：x 向右、y 向下，左上角为 (0,0)。
/// </summary>
public sealed class BlockGrid
{
    /// <summary>空块索引：网格创建后默认填充此值。</summary>
    public const ushort EmptyIndex = 0;

    public int Width { get; }
    public int Height { get; }

    private readonly ushort[] _tiles;

    public BlockGrid(int width, int height)
    {
        // 宽高来自生成器/场景配置，错误值会静默产出残缺世界，在此把关（fail fast）
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "网格宽度必须为正");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "网格高度必须为正");
        Width = width;
        Height = height;
        _tiles = new ushort[width * height];
    }

    /// <summary>坐标是否在网格范围内。</summary>
    public bool InBounds(int x, int y)
        => (uint)x < (uint)Width && (uint)y < (uint)Height;

    /// <summary>读取指定格的块索引；越界抛异常（调用方先用 InBounds 把关）。</summary>
    public ushort Get(int x, int y)
    {
        if (!InBounds(x, y))
            throw new ArgumentOutOfRangeException(nameof(x), $"读取越界：({x},{y}) 不在 {Width}x{Height} 内");
        return _tiles[y * Width + x];
    }

    /// <summary>写入指定格的块索引；越界抛异常（调用方先用 InBounds 把关）。</summary>
    public void Set(int x, int y, ushort blockIndex)
    {
        if (!InBounds(x, y))
            throw new ArgumentOutOfRangeException(nameof(x), $"写入越界：({x},{y}) 不在 {Width}x{Height} 内");
        _tiles[y * Width + x] = blockIndex;
    }
}
