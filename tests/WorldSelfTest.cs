using Godot;
using ProjectSandbox.Core;
using ProjectSandbox.World;

/// <summary>
/// M1 World 层最小自测：BlockGrid / BlockRegistry / BlockInteraction 三类的静态+动态验证。
/// 运行方式：godot --headless --path . --script res://tests/WorldSelfTest.cs --quit
/// 全部通过退出码 0，任一失败退出码 1。命名空间留空（同 CoreSelfTest：避免 --script 类型解析歧义）。
/// </summary>
public partial class WorldSelfTest : SceneTree
{
    private int _failed;

    public override void _Initialize()
    {
        TestBlockGrid();
        TestBlockRegistry();
        TestBlockInteraction();
        GD.Print(_failed == 0
            ? "[tests] World 自测全部通过"
            : $"[tests] World 自测失败 {_failed} 项");
        Quit(_failed == 0 ? 0 : 1);
    }

    private void Check(bool condition, string name)
    {
        if (condition)
        {
            GD.Print($"[tests] 通过：{name}");
        }
        else
        {
            _failed++;
            GD.PushError($"[tests] 失败：{name}");
        }
    }

    private void TestBlockGrid()
    {
        var grid = new BlockGrid(4, 3);
        Check(grid.Width == 4 && grid.Height == 3, "BlockGrid 构造宽高正确");
        Check(grid.InBounds(0, 0) && grid.InBounds(3, 2), "BlockGrid 边界内坐标判定为界内");
        Check(!grid.InBounds(4, 2) && !grid.InBounds(-1, 0) && !grid.InBounds(0, 3), "BlockGrid 边界外坐标判定为界外");
        Check(grid.Get(2, 1) == BlockGrid.EmptyIndex, "BlockGrid 新网格默认全空块");

        grid.Set(1, 1, 7);
        Check(grid.Get(1, 1) == 7, "BlockGrid 写读回环正确");

        var getThrows = false;
        try { grid.Get(4, 0); }
        catch (ArgumentOutOfRangeException) { getThrows = true; }
        Check(getThrows, "BlockGrid 越界读抛异常");

        var setThrows = false;
        try { grid.Set(0, 3, 1); }
        catch (ArgumentOutOfRangeException) { setThrows = true; }
        Check(setThrows, "BlockGrid 越界写抛异常");

        var ctorThrows = false;
        try { new BlockGrid(0, 3); }
        catch (ArgumentOutOfRangeException) { ctorThrows = true; }
        Check(ctorThrows, "BlockGrid 非法宽高构造抛异常");
    }

    private void TestBlockRegistry()
    {
        var registry = new BlockRegistry(new JsonDataLoader());
        Check(registry.Count == 5, "BlockRegistry 加载 blocks.json 共五条");

        var soil = registry.GetIndex("tile_soil_loam");
        Check(soil == 1, "BlockRegistry 首个块索引为 1（0 保留为空块）");
        Check(registry.GetDef(soil).DisplayName == "壤土块", "BlockRegistry 定义中文名正确");

        var unknownThrows = false;
        try { registry.GetIndex("tile_not_exists"); }
        catch (KeyNotFoundException) { unknownThrows = true; }
        Check(unknownThrows, "BlockRegistry 未知 id 抛异常");

        Check(registry.IsMinable(0) == false, "BlockRegistry 空块不可挖");
        Check(registry.IsSolid(0) == false, "BlockRegistry 空块不阻挡");
        Check(registry.IsMinable(registry.GetIndex("tile_basalt_firm")) == false, "BlockRegistry 坚玄武岩 minable=false");
        Check(registry.IsSolid(registry.GetIndex("tile_moss_lantern")) == false, "BlockRegistry 苔灯 solid=false");
    }

    private void TestBlockInteraction()
    {
        var grid = new BlockGrid(4, 3);
        var registry = new BlockRegistry(new JsonDataLoader());
        var interact = new BlockInteraction(grid, registry);

        var soil = registry.GetIndex("tile_soil_loam");
        var firm = registry.GetIndex("tile_basalt_firm");

        grid.Set(1, 1, soil);
        grid.Set(2, 2, firm);

        Check(!interact.Mine(0, 0), "挖掘空块返回 false");
        Check(!interact.Mine(2, 2), "挖掘不可挖块（坚玄武岩）返回 false");
        Check(!interact.Mine(4, 0), "挖掘越界返回 false");
        Check(interact.Mine(1, 1), "挖掘壤土块成功");
        Check(grid.Get(1, 1) == BlockGrid.EmptyIndex, "挖掘成功后该格清空");

        Check(interact.Place(3, 0, soil), "向空格放置壤土块成功");
        Check(grid.Get(3, 0) == soil, "放置后该格为指定索引");
        Check(!interact.Place(3, 0, soil), "向非空格放置返回 false");
        Check(!interact.Place(4, 0, soil), "越界放置返回 false");
        Check(!interact.Place(0, 0, BlockGrid.EmptyIndex), "放置空索引返回 false");
    }
}
