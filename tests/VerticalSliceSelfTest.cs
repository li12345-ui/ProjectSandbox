using Godot;
using ProjectSandbox;
using ProjectSandbox.Core;
using ProjectSandbox.World;

/// <summary>
/// 垂直切片自测：覆盖移动、碰撞、挖掘、放置、背包、合成、敌人、存档八大系统。
/// 运行方式：godot --headless --path . --script res://tests/VerticalSliceSelfTest.cs --quit
/// 全部通过退出码 0，任一失败退出码 1。命名空间留空（同 CoreSelfTest：避免 --script 类型解析歧义）。
///
/// 与 CoreSelfTest/WorldSelfTest 的分工：
/// - CoreSelfTest 管 Core 三服务与数据加载；WorldSelfTest 管 World 数据层三件套；
/// - 本文件做"系统级行为验证"——模拟输入驱动 PlayerMovement/EnemyAI 的 _PhysicsProcess，
///   走真实 BlockRegistry 数据，验证多系统协同（放置+背包消耗、合成+背包、伤害+生命链路）。
/// </summary>
public partial class VerticalSliceSelfTest : SceneTree
{
    private int _failed;

    public override void _Initialize()
    {
        EnsureInputActions(); // PlayerMovement 依赖的 action 必须先注册（经验 #301911）
        TestMovement();
        TestCollision();
        TestDigAndPlace();
        TestInventory();
        TestCrafting();
        TestEnemy();
        TestSaveSystem();
        GD.Print(_failed == 0
            ? "[tests] 垂直切片自测全部通过"
            : $"[tests] 垂直切片自测失败 {_failed} 项");
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

    /// <summary>注册移动 action（幂等）——headless 下查询未注册 action 会触发引擎报错。</summary>
    private static void EnsureInputActions()
    {
        foreach (var action in new[] { "move_left", "move_right", "move_up" })
            if (!InputMap.HasAction(action))
                InputMap.AddAction(action);
    }

    // ================================================================
    // 1. 移动：重力下落 → 站立稳定 → 跳跃 → 水平移动（走真实碰撞注入）
    // ================================================================

    private void TestMovement()
    {
        var registry = new BlockRegistry(new JsonDataLoader());
        var shale = registry.GetIndex("tile_stone_shale");
        var grid = new BlockGrid(8, 5);
        for (int x = 0; x < 8; x++) grid.Set(x, 4, shale); // 地面：第 4 行
        var collision = new CollisionSystem(grid, registry, WorldRenderer.TileSize);

        var player = new PlayerMovement();
        player.SetCollisionSystem(collision);
        // 起始悬空：中心 (64, 20) → AABB 下沿 40，地面顶 64 → 需下落 24px
        player.Position = new Vector2(64f, 20f);

        // 1) 重力下落 + 落地：推 30 帧后稳在地面顶（中心 Y = 64 - 20 = 44）
        for (int i = 0; i < 30; i++) player._PhysicsProcess(0.016);
        Check(Mathf.Abs(player.Position.Y - 44f) < 0.5f, "移动：重力下落后站立在地面（Y≈44）");

        float yRest = player.Position.Y;
        for (int i = 0; i < 5; i++) player._PhysicsProcess(0.016);
        Check(Mathf.Abs(player.Position.Y - yRest) < 0.001f, "移动：站立时 Y 稳定（着地清零速度）");

        // 2) 跳跃：着地状态按跳 → Y 上升
        Input.ActionPress("move_up");
        player._PhysicsProcess(0.016);
        Input.ActionRelease("move_up");
        Check(player.Position.Y < yRest - 4f, "移动：跳跃触发后 Y 上升（JumpForce=-400）");

        // 3) 水平移动：落回地面后按住右键 → X 增大（MoveSpeed=200 → 3.2px/帧）
        for (int i = 0; i < 40; i++) player._PhysicsProcess(0.016);
        float xBefore = player.Position.X;
        Input.ActionPress("move_right");
        for (int i = 0; i < 3; i++) player._PhysicsProcess(0.016);
        Input.ActionRelease("move_right");
        Check(player.Position.X > xBefore + 6f, "移动：按住右键 X 增大（≈9.6px/3帧）");
    }

    // ================================================================
    // 2. 碰撞：分轴穿透推回（下落/横移）+ 可站立探测
    // ================================================================

    private void TestCollision()
    {
        var registry = new BlockRegistry(new JsonDataLoader());
        var shale = registry.GetIndex("tile_stone_shale");
        var grid = new BlockGrid(5, 5);
        for (int x = 0; x < 5; x++) grid.Set(x, 4, shale); // 地面：第 4 行（tile 顶 Y=64）
        grid.Set(4, 2, shale);                             // 墙：第 2 行第 4 列
        var collision = new CollisionSystem(grid, registry, WorldRenderer.TileSize);

        // 下落穿透：AABB 16x16 从 Y=32 落 dt=0.4（ty=40）→ 下沿 88 穿入地面 24px → 推回下沿 64（topLeft=48）
        var resolved = collision.ResolveCollision(new Vector2(8f, 32f), new Vector2(8f, 8f),
            new Vector2(0f, 100f), 0.4);
        Check(Mathf.Abs(resolved.Y - 48f) < 0.01f, "碰撞：下落穿透推回到地面顶（下沿=64）");

        // 横移撞墙：AABB 右缘 72→82 穿入墙格（tile 左缘 64）→ 推回右缘=64
        var hitWall = collision.ResolveCollision(new Vector2(56f, 32f), new Vector2(8f, 8f),
            new Vector2(50f, 0f), 0.2);
        Check(Mathf.Abs(hitWall.X - 48f) < 0.01f, "碰撞：横移撞墙推回（右缘=64）");

        // 可站立探测：下沿贴地（63）+1px 探到第 4 行 → true；悬空 → false
        Check(collision.IsOnGround(new Vector2(8f, 47f), new Vector2(8f, 8f)), "碰撞：下沿贴地时 IsOnGround=true");
        Check(!collision.IsOnGround(new Vector2(8f, 31f), new Vector2(8f, 8f)), "碰撞：悬空时 IsOnGround=false");
    }

    // ================================================================
    // 3. 挖掘 + 放置：BlockInteraction 规则链路 + 挖/放回环 + Hardness 数据
    // ================================================================

    private void TestDigAndPlace()
    {
        var registry = new BlockRegistry(new JsonDataLoader());
        var soil = registry.GetIndex("tile_soil_loam");
        var grid = new BlockGrid(4, 4);
        for (int x = 0; x < 4; x++) grid.Set(x, 3, soil);
        var interact = new BlockInteraction(grid, registry);

        Check(registry.GetDef(soil).Hardness >= 1, "挖掘：壤土块 Hardness ≥ 1（挖掘耗时数据就绪）");

        // 挖掘规则：越界/空块拒绝，可挖块清空
        Check(!interact.Mine(4, 0) && !interact.Mine(1, 1), "挖掘：越界与空块拒绝");
        Check(interact.Mine(1, 3) && grid.Get(1, 3) == BlockGrid.EmptyIndex, "挖掘：壤土挖掉后清空");

        // 放置规则：空格成功 → 非空格/越界/空索引拒绝
        Check(interact.Place(1, 3, soil) && grid.Get(1, 3) == soil, "放置：空格放回壤土成功");
        Check(!interact.Place(1, 3, soil), "放置：非空格拒绝");
        Check(!interact.Place(9, 9, soil) && !interact.Place(0, 0, BlockGrid.EmptyIndex), "放置：越界与空索引拒绝");

        // 回环：挖 → 放 → 挖
        Check(interact.Mine(2, 3) && interact.Place(2, 3, soil) && interact.Mine(2, 3),
            "挖掘放置：挖/放/再挖回环成功");
    }

    // ================================================================
    // 4. 背包：AddItem 堆叠/溢出、RemoveItem、MoveStack 三义、SplitStack
    // ================================================================

    private void TestInventory()
    {
        var inv = new Inventory(4, 2);

        // AddItem：同 ID 堆叠 + 溢出开新栈
        Check(inv.AddItem(1, 5, 99) == 0 && inv.GetStack(0).Count == 5, "背包：首次 AddItem 入槽 0");
        Check(inv.AddItem(1, 100, 99) == 0 && inv.GetStack(0).Count == 99 && inv.GetStack(1).Count == 6,
            "背包：超栈溢出自动开新栈（99+6）");

        // 背包满：返回剩余
        inv.AddItem(2, 50, 99); // 槽 2
        inv.AddItem(3, 50, 99); // 槽 3 → 已满
        Check(inv.AddItem(4, 7, 99) == 7, "背包：背包装满时 AddItem 返回剩余 7");

        // RemoveItem：超扣返回实际量，扣空归 default
        var inv2 = new Inventory(2, 1);
        inv2.AddItem(5, 10, 99);
        Check(inv2.RemoveItem(0, 3) == 3 && inv2.GetStack(0).Count == 7, "背包：RemoveItem 部分扣除");
        Check(inv2.RemoveItem(0, 99) == 7 && inv2.IsEmpty(0), "背包：超扣取实际量且扣空清槽");

        // MoveStack：空槽移动 / 同 ID 合并 / 异 ID 交换
        var inv3 = new Inventory(4, 2);
        inv3.AddItem(1, 30, 99);
        Check(inv3.MoveStack(0, 1, 10) && inv3.GetStack(1).ItemId == 1 && inv3.GetStack(1).Count == 10,
            "背包：MoveStack 空槽移动");
        inv3.MoveStack(0, 2, 20); // 槽 2: (1,20)，槽 0 清空
        Check(inv3.MoveStack(2, 1, 20) && inv3.GetStack(1).Count == 30 && inv3.IsEmpty(2),
            "背包：MoveStack 同 ID 合并");
        inv3.AddItem(2, 5, 99); // 槽 0: (2,5)（落最先空槽）
        Check(inv3.MoveStack(0, 1, 5) && inv3.GetStack(0).ItemId == 1 && inv3.GetStack(1).ItemId == 2,
            "背包：MoveStack 异 ID 交换整槽");
        var inv4 = new Inventory(2, 1);
        inv4.AddItem(1, 3, 99);
        inv4.AddItem(2, 4, 99);
        Check(!inv4.MoveStack(0, 0, 1) && !inv4.MoveStack(0, 1, 0), "背包：MoveStack 自移/零量拒绝");

        // SplitStack：目标非空拒绝；非法构造抛异常
        Check(!inv4.SplitStack(0, 1, 1), "背包：SplitStack 到非空槽拒绝");
        var throws = false;
        try { new Inventory(5, 0); }
        catch (System.ArgumentOutOfRangeException) { throws = true; }
        Check(throws, "背包：非法 hotbar 构造抛异常");
    }

    // ================================================================
    // 5. 合成：CanCraft 校验 → Craft 消耗+产出 → 材料不足原子拒绝
    // ================================================================

    private void TestCrafting()
    {
        var inv = new Inventory(10, 5);
        // 解析桥：item_a→1, item_b→2, item_c→3（maxStack 99）
        (ushort, int) Resolve(string id) => id switch
        {
            "item_a" => (1, 99),
            "item_b" => (2, 99),
            "item_c" => (3, 99),
            _ => (0, 99),
        };
        var recipe = new CraftingSystem.Recipe
        {
            Id = "craft_test",
            Output = new CraftingSystem.RecipeOutput { ItemId = "item_c", Count = 1 },
            Ingredients = new()
            {
                new CraftingSystem.RecipeIngredient { ItemId = "item_a", Count = 2 },
                new CraftingSystem.RecipeIngredient { ItemId = "item_b", Count = 1 },
            },
        };
        var crafting = new CraftingSystem(inv, new() { recipe }, Resolve);

        // 材料不足：CanCraft false 且 Craft 原子拒绝（不消耗）
        inv.AddItem(1, 1, 99);
        Check(!crafting.CanCraft(recipe), "合成：材料不足 CanCraft=false");
        Check(!crafting.Craft(recipe) && inv.GetStack(0).Count == 1, "合成：材料不足 Craft 拒绝且不消耗");

        // 材料齐：CanCraft true → Craft 消耗 2a+1b 产出 1c
        inv.AddItem(1, 1, 99);   // a 共 2
        inv.AddItem(2, 1, 99);   // b 共 1
        Check(crafting.CanCraft(recipe), "合成：材料齐 CanCraft=true");
        Check(crafting.Craft(recipe), "合成：Craft 执行成功");
        Check(inv.GetStack(0).ItemId == 3 && inv.GetStack(0).Count == 1 && inv.GetStack(1).Count == 0,
            "合成：材料扣空且产出 1 个 item_c 入包（落最先空槽）");

        // 未知物品解析为 0 → 不可合成（安全降级）
        var badRecipe = new CraftingSystem.Recipe
        {
            Id = "craft_bad",
            Output = new CraftingSystem.RecipeOutput { ItemId = "item_x", Count = 1 },
            Ingredients = new() { new CraftingSystem.RecipeIngredient { ItemId = "item_a", Count = 1 } },
        };
        Check(!crafting.CanCraft(badRecipe), "合成：未知物品解析失败拒绝合成");
    }

    // ================================================================
    // 6. 敌人：巡逻/追击/攻击行为 + 受击死亡（HealthSystem 委托）+ 伤害链路
    // ================================================================

    private void TestEnemy()
    {
        var player = new PlayerMovement();
        var enemy = new EnemyAI();

        // 巡逻：无目标时位置随时间变化（随机转向移动）
        enemy.SetTarget(null);
        enemy.Position = new Vector2(100f, 100f);
        for (int i = 0; i < 3; i++) enemy._PhysicsProcess(0.016);
        Check(enemy.Position.DistanceTo(new Vector2(100f, 100f)) > 0.5f, "敌人：巡逻状态下位置移动");

        // 追击：玩家进入 DetectRange(120) → 向玩家收敛
        player.Position = enemy.Position + new Vector2(60f, 0f);
        enemy.SetTarget(player);
        for (int i = 0; i < 2; i++) enemy._PhysicsProcess(0.016);
        Check(enemy.Position.DistanceTo(player.Position) < 60f, "敌人：检测到玩家后追击收敛");

        // 攻击：进入 AttackRange(24) → 停止移动（速度清零）
        player.Position = enemy.Position + new Vector2(10f, 0f);
        var posAtAttack = enemy.Position;
        for (int i = 0; i < 3; i++) enemy._PhysicsProcess(0.016);
        Check(enemy.Position.DistanceTo(posAtAttack) < 0.01f, "敌人：攻击状态下停止移动");

        // 受击链路：TakeDamage 委托 HealthSystem；IFrameDuration=0 保证连续受击
        var enemyHp = new HealthSystem(30) { IFrameDuration = 0f };
        enemy.SetHealthSystem(enemyHp);
        enemy.SetDamageSystem(new DamageSystem());
        Check(enemy.TakeDamage(10) && enemy.CurrentHp == 20 && !enemy.IsDead, "敌人：TakeDamage 委托扣血至 20");
        Check(enemy.TakeDamage(100) && enemy.IsDead, "敌人：致命伤后 IsDead");
        Check(!enemy.TakeDamage(5), "敌人：死亡后受击拒绝");
        enemy._PhysicsProcess(0.016); // 死亡态应直接 return，不崩溃
        Check(true, "敌人：死亡后 _PhysicsProcess 安全跳过");

        // 生命系统细节：无敌帧 + 事件
        var hp = new HealthSystem(50);
        bool deathFired = false;
        hp.OnDeath += _ => deathFired = true;
        Check(hp.TakeDamage(10) && !hp.TakeDamage(10), "敌人：无敌帧内第二次受击被忽略");
        hp.Tick(0.6f);
        Check(hp.TakeDamage(10), "敌人：无敌帧过期后可再次受击");

        // 伤害系统统一入口：rawDamage - defense，最低 1 点
        var dmg = new DamageSystem();
        var hp2 = new HealthSystem(30) { IFrameDuration = 0f };
        dmg.ApplyDamage(hp2, 10, 4); // Max(1, 10-4)=6 → 24
        Check(hp2.CurrentHp == 24, "敌人：DamageSystem 扣减防御后造成 6 点");
        var hp3 = new HealthSystem(30) { IFrameDuration = 0f };
        dmg.ApplyDamage(hp3, 10, 50); // 防御高于伤害 → 最低 1 点
        Check(hp3.CurrentHp == 29, "敌人：防御超伤时保底 1 点");
        Check(!deathFired, "敌人：未死亡时 OnDeath 不触发");
    }

    // ================================================================
    // 7. 存档：Save/Load 回环、版本门禁、ListSlots、Delete（高位槽位隔离）
    // ================================================================

    private void TestSaveSystem()
    {
        var save = new SaveSystem();
        const int slot = 90;

        // 回环：写 → 读 → 字段一致
        var data = new SaveSystem.SaveData();
        data.Player.Pos = new double[] { 12.5, 34.5 };
        data.Player.Hp = 88;
        data.Player.MaxHp = 100;
        data.Player.HotbarSize = 5;
        data.Player.Inventory.Add(new SaveSystem.SaveInventorySlot { Slot = 0, ItemId = "item_a", Count = 3 });
        data.Header.Seed = 20261003;
        Check(save.Save(slot, data), "存档：Save 写入 slot 90 成功");
        Check(Godot.FileAccess.FileExists(SaveSystem.SlotPath(slot)), "存档：槽位文件存在");

        var loaded = save.Load(slot);
        Check(loaded != null, "存档：Load 读回非空");
        if (loaded != null)
        {
            Check(System.Math.Abs(loaded.Player.Pos[0] - 12.5) < 0.001
                && System.Math.Abs(loaded.Player.Pos[1] - 34.5) < 0.001,
                "存档：玩家坐标回环一致");
            Check(loaded.Player.Hp == 88 && loaded.Player.MaxHp == 100 && loaded.Player.HotbarSize == 5,
                "存档：HP/MaxHp/HotbarSize 回环一致");
            Check(loaded.Player.Inventory.Count == 1 && loaded.Player.Inventory[0].ItemId == "item_a"
                && loaded.Player.Inventory[0].Count == 3,
                "存档：背包槽位回环一致");
            Check(loaded.Header.Seed == 20261003 && !string.IsNullOrEmpty(loaded.Header.SaveId),
                "存档：Seed/SaveId 回环一致");
        }

        // 版本门禁：非法版本 / 无版本号 → 拒绝
        WriteRawSave(91, "{\"format_version\": 99}");
        WriteRawSave(92, "{\"foo\": 1}");
        Check(save.Load(91) == null, "存档：版本号不兼容拒绝加载");
        Check(save.Load(92) == null, "存档：无版本号拒绝加载");
        Check(save.Load(89) == null, "存档：不存在的槽位返回 null");

        // ListSlots 包含已写槽位；Delete 后不可再读
        var slots = save.ListSlots();
        Check(slots.Contains(slot) && slots.Contains(91) && slots.Contains(92), "存档：ListSlots 列出已写槽位");
        Check(save.Delete(slot) && save.Load(slot) == null, "存档：Delete 后槽位不可读");
        Check(!save.Delete(89), "存档：Delete 不存在的槽位返回 false");

        // 清理测试槽位
        save.Delete(91);
        save.Delete(92);
    }

    private static void WriteRawSave(int slot, string json)
    {
        using var fa = Godot.FileAccess.Open(SaveSystem.SlotPath(slot), Godot.FileAccess.ModeFlags.Write);
        fa.StoreString(json);
    }
}
