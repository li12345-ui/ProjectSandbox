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
        TestWorldGenerator();
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

    private void TestWorldGenerator()
    {
        var loader = new JsonDataLoader();
        var registry = new BlockRegistry(loader);
        var generator = new WorldGenerator(loader, registry);
        var grid = generator.Generate();

        Check(grid.Width == 64 && grid.Height == 48, "生成器产出 64x48 网格");
        Check(grid.Get(0, 0) == BlockGrid.EmptyIndex && grid.Get(63, 11) == BlockGrid.EmptyIndex, "空行区（0~11 行）为空块");

        var soil = registry.GetIndex("tile_soil_loam");
        var shale = registry.GetIndex("tile_stone_shale");
        var basalt = registry.GetIndex("tile_basalt_firm");

        Check(grid.Get(0, 12) == soil && grid.Get(63, 15) == soil, "壤土层位于 12~15 行");
        Check(grid.Get(0, 16) == shale && grid.Get(63, 25) == shale, "页岩层位于 16~25 行");
        Check(grid.Get(0, 26) == basalt && grid.Get(63, 47) == basalt, "坚玄武岩层位于 26~47 行直至底部");

        // 确定性：同参数两次生成逐格一致
        var again = generator.Generate();
        var identical = true;
        for (int y = 0; y < grid.Height && identical; y++)
            for (int x = 0; x < grid.Width && identical; x++)
                identical = grid.Get(x, y) == again.Get(x, y);
        Check(identical, "同参数两次生成逐格一致（确定性）");

        // 联动校验：最底行坚玄武岩 minable=false（不可挖的底盘）
        Check(!registry.IsMinable(grid.Get(30, 47)), "最底行块不可挖（坚玄武岩底盘）");
    }
}
