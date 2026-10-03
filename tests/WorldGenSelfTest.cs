using Godot;
using System.Text.Json.Serialization;
using ProjectSandbox.Core;
using ProjectSandbox.World;

/// <summary>
/// 世界生成自测：PerlinNoise / VoronoiField / CaveAutomata 三件套 + 管线世界形状验证。
/// 运行方式：godot --headless --path . --script res://tests/WorldGenSelfTest.cs --quit
/// 全部通过退出码 0，任一失败退出码 1。命名空间留空（同 WorldSelfTest：避免 --script 类型解析歧义）。
/// 阶段 1 覆盖原语库；阶段 3 起扩展管线世界形状断言（设计文档 §8）。
/// </summary>
public partial class WorldGenSelfTest : SceneTree
{
    private int _failed;

    public override void _Initialize()
    {
        TestPerlin();
        TestVoronoi();
        TestCaveAutomata();
        TestPipelineWorld();
        TestLegacyLayering();
        GD.Print(_failed == 0
            ? "[tests] WorldGen 自测全部通过"
            : $"[tests] WorldGen 自测失败 {_failed} 项");
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

    // -------- Perlin --------

    private void TestPerlin()
    {
        var a = new PerlinNoise(12345);
        var b = new PerlinNoise(12345);
        var c = new PerlinNoise(99999);

        bool same = true;
        for (int i = 0; i < 64 && same; i++)
            same = a.Noise(i * 0.37f, i * 0.91f) == b.Noise(i * 0.37f, i * 0.91f);
        Check(same, "Perlin 同 seed 采样逐点相等（确定性）");

        bool diff = false;
        for (int i = 0; i < 64 && !diff; i++)
            diff = a.Noise(i * 0.37f, i * 0.91f) != c.Noise(i * 0.37f, i * 0.91f);
        Check(diff, "Perlin 不同 seed 存在差异采样点");

        bool inRange = true;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < 4096 && inRange; i++)
        {
            float v = a.Noise(i * 0.173f, i * 1.337f);
            min = Mathf.Min(min, v);
            max = Mathf.Max(max, v);
            if (v < -1f - 1e-4f || v > 1f + 1e-4f) inRange = false;
        }
        Check(inRange, $"Perlin 单倍频值域 [-1,1]（4096 采样，实测 [{min:F3},{max:F3}]）");

        bool fractalInRange = true;
        for (int i = 0; i < 512 && fractalInRange; i++)
        {
            float v = a.Fractal(i * 0.11f, i * 0.53f, octaves: 4, persistence: 0.5f, lacunarity: 2f);
            if (v < -1f - 1e-4f || v > 1f + 1e-4f) fractalInRange = false;
        }
        Check(fractalInRange, "Perlin Fractal 归一化值域 [-1,1]（4 倍频，512 采样）");

        var octavesThrows = false;
        try { a.Fractal(0.5f, 0.5f, 0, 0.5f, 2f); }
        catch (ArgumentOutOfRangeException) { octavesThrows = true; }
        Check(octavesThrows, "Perlin Fractal octaves=0 抛异常");
    }

    // -------- Voronoi --------

    private void TestVoronoi()
    {
        const int W = 32, H = 20, CELLS = 6;

        var points = VoronoiField.SpawnPoints(seed: 777, W, H, CELLS);
        bool inBounds = points.Length == CELLS;
        for (int i = 0; i < points.Length && inBounds; i++)
            inBounds = points[i].X >= 0f && points[i].X < W && points[i].Y >= 0f && points[i].Y < H;
        Check(inBounds, "Voronoi 撒点数量与界内约束正确");

        var map = VoronoiField.Assign(W, H, points);
        bool validIndex = true;
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < map.Length && validIndex; i++)
        {
            if (map[i] < 0 || map[i] >= CELLS) { validIndex = false; break; }
            seen.Add(map[i]);
        }
        Check(validIndex, "Voronoi 指派每格 ∈ [0, cellCount)");

        var classes = VoronoiField.NextCellClass(seed: 42, CELLS, new[] { 3, 1, 0, 5 });
        bool noZeroWeight = true;
        var cc = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < classes.Length && noZeroWeight; i++)
        {
            if (classes[i] == 2) noZeroWeight = false; // 权重 0 的不允许被抽中
            cc.Add(classes[i]);
        }
        Check(noZeroWeight && classes.Length == CELLS, "Voronoi 类别抽取尊重权重（0 权不出现）");

        var zeroThrows = false;
        try { VoronoiField.NextCellClass(seed: 1, CELLS, new[] { 0, 0 }); }
        catch (InvalidOperationException) { zeroThrows = true; }
        Check(zeroThrows, "Voronoi 全 0 权重抛异常（配置错误早暴露）");
    }

    // -------- 细胞自动机洞穴 --------

    private void TestCaveAutomata()
    {
        const int W = 20, H = 12;

        // 手工构造单轮规则：3×3 中心 8 邻中恰 5 墙 → threshold=5 时中心变墙
        var grid = new bool[9];
        // 布局（0=空,1=墙）：中心 (1,1)，四角 + 左上 = 5 墙
        grid[0] = true; grid[1] = false; grid[2] = true;
        grid[3] = false; grid[4] = false; grid[5] = false;
        grid[6] = true; grid[7] = true; grid[8] = true;
        var stepped = CaveAutomata.GenerationStep(grid, 3, 3, threshold: 5);
        Check(stepped[4], "CA 单轮规则：8 邻 5 墙时中心成活（4-5 规则阈值）");

        // 同上布局，threshold=6 → 中心应为空
        var stepped6 = CaveAutomata.GenerationStep(grid, 3, 3, threshold: 6);
        Check(!stepped6[4], "CA 单轮规则：8 邻 5 墙 < 阈值 6 时中心为空");

        // 存活条款（经典 4-5 回归锁）：中心已墙 + 8 邻恰 4 墙 → 存活
        var survive = new bool[9] { true, true, false, true, true, false, false, false, true };
        var steppedSurv = CaveAutomata.GenerationStep(survive, 3, 3, threshold: 5);
        Check(steppedSurv[4], "CA 单轮规则：已墙 + 8 邻 4 墙存活（4-5 存活条款）");

        // 出生反例：中心空 + 8 邻恰 4 墙 → 不生成（< 出生阈值 5）
        survive[4] = false;
        var steppedBorn = CaveAutomata.GenerationStep(survive, 3, 3, threshold: 5);
        Check(!steppedBorn[4], "CA 单轮规则：空 + 8 邻 4 墙不生成（出生需 ≥5）");

        // 生成集成：band 尊重 + 确定性 + 清扫不变量
        var open = CaveAutomata.Generate(seed: 2026, W, H,
            bandTop: 6, bandBottom: 10, density: 0.45f, iterations: 4, wallThreshold: 5, minRoom: 8);

        bool bandRespected = true;
        for (int y = 0; y < H && bandRespected; y++)
            for (int x = 0; x < W && bandRespected; x++)
                if ((y < 6 || y > 10) && open[y * W + x]) bandRespected = false;
        Check(bandRespected, "CA 洞穴掩码尊重岩层带（band 外恒非 open）");

        var open2 = CaveAutomata.Generate(seed: 2026, W, H,
            bandTop: 6, bandBottom: 10, density: 0.45f, iterations: 4, wallThreshold: 5, minRoom: 8);
        bool deterministic = true;
        for (int i = 0; i < open.Length && deterministic; i++)
            if (open[i] != open2[i]) deterministic = false;
        Check(deterministic, "CA 同 seed 两次生成逐格相等（确定性）");

        // 清扫不变量：所有 open 连通分量大小 ∈ {0, ≥ minRoom}（含对角连通）
        var visited = new bool[open.Length];
        var queue = new System.Collections.Generic.Queue<int>();
        bool invariant = true;
        for (int s = 0; s < open.Length && invariant; s++)
        {
            if (!open[s] || visited[s]) continue;
            int count = 0;
            queue.Clear();
            queue.Enqueue(s);
            visited[s] = true;
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                count++;
                int x = idx % W, y = idx / W;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= W || ny < 0 || ny >= H) continue;
                        int nIdx = ny * W + nx;
                        if (open[nIdx] && !visited[nIdx]) { visited[nIdx] = true; queue.Enqueue(nIdx); }
                    }
            }
            if (count < 8) invariant = false;
        }
        Check(invariant, "CA 孤立清扫不变量成立（分量大小 ∈ {0, ≥ minRoom}）");

        var bandThrows = false;
        try { CaveAutomata.Generate(seed: 1, W, H, 9, 8, 0.45f, 4, 5, 0); }
        catch (ArgumentOutOfRangeException) { bandThrows = true; }
        Check(bandThrows, "CA bandTop > bandBottom 抛异常");
    }

    // -------- 管线世界形状（阶段 3，设计文档 §8） --------

    /// <summary>取某一列首个非空行的行号（地表行）；全空返回 -1。</summary>
    private static int FirstSolid(BlockGrid g, int x)
    {
        for (int y = 0; y < g.Height; y++)
            if (g.Get(x, y) != 0) return y;
        return -1;
    }

    private void TestPipelineWorld()
    {
        var loader = new JsonDataLoader();
        var registry = new BlockRegistry(loader);
        var gen = new WorldGenerator(loader, registry);
        var m = loader.Load<GenMirror>("world_gen.json");
        int W = gen.Width, H = gen.Height;
        int airRows = m.AirRows;
        int basaltTop = H - m.Terrain.BasaltThickness;

        // ① 确定性：同 seed 逐格相等；不同 seed 存在差异格
        var w = gen.Generate(12345);
        var w2 = gen.Generate(12345);
        bool same = true;
        for (int y = 0; y < H && same; y++)
            for (int x = 0; x < W && same; x++)
                if (w.Get(x, y) != w2.Get(x, y)) same = false;
        Check(same, "管线同 seed 两次生成逐格相等（确定性）");

        var other = gen.Generate(97531);
        bool diff = false;
        for (int y = 0; y < H && !diff; y++)
            for (int x = 0; x < W && !diff; x++)
                if (w.Get(x, y) != other.Get(x, y)) diff = true;
        Check(diff, "管线不同 seed 存在差异格");

        // ② 高度场：air_rows 带全空；地表行全部落入钳制区间
        bool airOk = true;
        for (int y = 0; y < airRows && airOk; y++)
            for (int x = 0; x < W && airOk; x++)
                if (w.Get(x, y) != 0) airOk = false;
        Check(airOk, "管线 air_rows 带全空");

        var surfaceSet = new System.Collections.Generic.HashSet<ushort>();
        foreach (var b in m.Biomes.Defs)
            surfaceSet.Add(registry.GetIndex(b.Surface));

        int maxFirst = 0;
        bool clampOk = true, surfOk = true;
        var seenSurf = new System.Collections.Generic.HashSet<ushort>();
        for (int x = 0; x < W; x++)
        {
            int fs = FirstSolid(w, x);
            if (fs < airRows + 1 || fs > basaltTop - 4) clampOk = false;
            if (surfaceSet.Contains(w.Get(x, fs))) seenSurf.Add(w.Get(x, fs));
            else surfOk = false;
            if (fs > maxFirst) maxFirst = fs;
        }
        Check(clampOk, "管线地表行全部落入钳制区间 [air+1, basaltTop-4]");
        Check(surfOk, "管线全部地表块 ∈ 群系表层块集合（blend 不越出集合）");
        Check(seenSurf.Count >= 2, $"管线群系出现 {seenSurf.Count} 种表层块（≥2）");

        // ③ 洞穴：带内存在空腔、世界未被掏空、BFS 分量 ≥ min_room
        int bandTop = maxFirst + 2, bandBottom = basaltTop - 2;
        int caveCells = 0, solidCells = 0, bandCells = 0;
        var caveOpen = new bool[W * H];
        for (int y = bandTop; y <= bandBottom; y++)
            for (int x = 0; x < W; x++)
            {
                bandCells++;
                if (w.Get(x, y) == 0) { caveCells++; caveOpen[y * W + x] = true; }
                else solidCells++;
            }
        Check(caveCells > 0, $"管线岩层带内存在洞穴空腔（{caveCells} 格）");
        Check(solidCells * 100 >= bandCells * 25, $"管线世界未被掏空（带内实心 {solidCells * 100 / bandCells}%）");

        if (m.Caves.MinRoom > 0)
        {
            var visited = new bool[W * H];
            var queue = new System.Collections.Generic.Queue<int>();
            bool compOk = true;
            for (int i = 0; i < caveOpen.Length && compOk; i++)
            {
                if (!caveOpen[i] || visited[i]) continue;
                int count = 0;
                queue.Clear();
                queue.Enqueue(i);
                visited[i] = true;
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    count++;
                    int cx = idx % W, cy = idx / W;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || nx >= W || ny < 0 || ny >= H) continue;
                            int n = ny * W + nx;
                            if (caveOpen[n] && !visited[n]) { visited[n] = true; queue.Enqueue(n); }
                        }
                }
                if (count < m.Caves.MinRoom) compOk = false;
            }
            Check(compOk, "管线洞穴 BFS 分量大小 ≥ min_room（孤腔已清扫）");
        }

        // ④ 底盘：玄武岩顶以下全为玄武岩（洞穴/矿脉不越界）
        var basalt = registry.GetIndex(m.Terrain.BasaltBlock);
        bool baseOk = true;
        for (int y = basaltTop; y < H && baseOk; y++)
            for (int x = 0; x < W && baseOk; x++)
                if (w.Get(x, y) != basalt) baseOk = false;
        Check(baseOk, "管线底盘全部玄武岩（洞穴不越玄武岩顶）");

        // ⑤ 矿脉：全部矿格落在各自 depth_band；四矿型全产出；存在同矿相邻对（团簇采样）
        bool bandOk = true;
        var counts = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var v in m.Veins)
        {
            ushort idx = registry.GetIndex(v.Ore);
            int lo = v.DepthBand[0], hi = Math.Min(v.DepthBand[1], basaltTop - 1);
            int cnt = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (w.Get(x, y) != idx) continue;
                    cnt++;
                    if (y < lo || y > hi) bandOk = false;
                }
            counts[v.Ore] = cnt;
        }
        var countMsg = string.Join(" ", System.Linq.Enumerable.Select(counts, kv => $"{kv.Key}={kv.Value}"));
        Check(bandOk, $"管线全部矿格 y ∈ 各自 depth_band（{countMsg}）");

        bool presenceOk = true;
        foreach (var kv in counts)
            if (kv.Value == 0) presenceOk = false;
        Check(presenceOk, "管线全部配置矿型均产出（固定 seed 12345）");

        var oreSet = new System.Collections.Generic.HashSet<ushort>();
        foreach (var v in m.Veins)
            oreSet.Add(registry.GetIndex(v.Ore));

        int pairFound = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                ushort v = w.Get(x, y);
                if (v == 0 || !oreSet.Contains(v)) continue;
                if (x + 1 < W && w.Get(x + 1, y) == v) pairFound++;
                if (y + 1 < H && w.Get(x, y + 1) == v) pairFound++;
            }
        Check(pairFound > 0, $"管线存在同矿型相邻对（团簇连通采样命中 {pairFound} 对）");
    }

    private void TestLegacyLayering()
    {
        var loader = new JsonDataLoader();
        var registry = new BlockRegistry(loader);
        var gen = new WorldGenerator(loader, registry);
        var m = loader.Load<GenMirror>("world_gen.json");
        int W = gen.Width, H = gen.Height;

        // 按旧分层配置推算出逐行期望块（与生成器同源数据，避免硬编码条数）
        var rowBlock = new ushort[H];
        int cursor = m.AirRows;
        foreach (var layer in m.Layers)
        {
            int rows = layer.Thickness == -1 ? H - cursor : layer.Thickness;
            for (int r = 0; r < rows && cursor < H; r++, cursor++)
                rowBlock[cursor] = registry.GetIndex(layer.Block);
        }

        var g = gen.Generate();
        bool equal = true;
        for (int y = 0; y < H && equal; y++)
            for (int x = 0; x < W && equal; x++)
                if (g.Get(x, y) != rowBlock[y]) equal = false;
        Check(equal, "无参 Generate() 与旧分层配置逐格一致（fallback 回归）");
    }

    // -------- world_gen.json 镜像（仅测试需读的字段，snake_case 契约） --------

    private sealed class GenMirror
    {
        [JsonPropertyName("air_rows")] public int AirRows { get; set; }
        [JsonPropertyName("layers")] public System.Collections.Generic.List<LayerMirror> Layers { get; set; } = new();
        [JsonPropertyName("terrain")] public TerrainMirror Terrain { get; set; } = new();
        [JsonPropertyName("biomes")] public BiomesMirror Biomes { get; set; } = new();
        [JsonPropertyName("caves")] public CavesMirror Caves { get; set; } = new();
        [JsonPropertyName("veins")] public System.Collections.Generic.List<VeinMirror> Veins { get; set; } = new();
    }

    private sealed class LayerMirror
    {
        [JsonPropertyName("block")] public string Block { get; set; } = "";
        [JsonPropertyName("thickness")] public int Thickness { get; set; }
    }

    private sealed class TerrainMirror
    {
        [JsonPropertyName("basalt_thickness")] public int BasaltThickness { get; set; }
        [JsonPropertyName("basalt_block")] public string BasaltBlock { get; set; } = "";
    }

    private sealed class BiomesMirror
    {
        [JsonPropertyName("defs")] public System.Collections.Generic.List<BiomeDefMirror> Defs { get; set; } = new();
    }

    private sealed class BiomeDefMirror
    {
        [JsonPropertyName("surface")] public string Surface { get; set; } = "";
    }

    private sealed class CavesMirror
    {
        [JsonPropertyName("min_room")] public int MinRoom { get; set; }
    }

    private sealed class VeinMirror
    {
        [JsonPropertyName("ore")] public string Ore { get; set; } = "";
        [JsonPropertyName("depth_band")] public int[] DepthBand { get; set; } = System.Array.Empty<int>();
    }
}