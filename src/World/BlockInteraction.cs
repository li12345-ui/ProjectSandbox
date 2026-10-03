namespace ProjectSandbox.World;

/// <summary>
/// 挖掘与放置规则（S2）：对 BlockGrid 做受规则约束的修改。
/// 只依赖网格与注册表，不引用实体与 UI（目录规范第四节）；
/// 返回 bool 而非抛异常——"挖不动/放不下"是玩家的常规操作结果，不是错误。
/// </summary>
public sealed class BlockInteraction
{
    private readonly BlockGrid _grid;
    private readonly BlockRegistry _registry;

    public BlockInteraction(BlockGrid grid, BlockRegistry registry)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// 挖掘指定格：越界、空块、不可挖（minable=false）返回 false；
    /// 成功则该格清空为空块。
    /// </summary>
    public bool Mine(int x, int y)
    {
        if (!_grid.InBounds(x, y)) return false;
        var index = _grid.Get(x, y);
        if (index == BlockGrid.EmptyIndex || !_registry.IsMinable(index)) return false;
        _grid.Set(x, y, BlockGrid.EmptyIndex);
        return true;
    }

    /// <summary>
    /// 向指定格放置块：越界、目标非空、传入空索引（0）返回 false；
    /// 成功则该格写入指定索引。
    /// </summary>
    public bool Place(int x, int y, ushort blockIndex)
    {
        if (!_grid.InBounds(x, y)) return false;
        if (_grid.Get(x, y) != BlockGrid.EmptyIndex) return false;
        if (blockIndex == BlockGrid.EmptyIndex) return false;
        _grid.Set(x, y, blockIndex);
        return true;
    }
}
