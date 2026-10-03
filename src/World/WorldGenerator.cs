using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using ProjectSandbox.Core;

namespace ProjectSandbox.World;

/// <summary>
/// 世界生成器：双模式——
/// 1) Generate() 无参 = 旧分层填充（S1 兼容路径，现有自测断言零变化）；
/// 2) Generate(int seed) = 六模块管线（docs/世界生成算法设计.md §3）：
///    Perlin 高度场 → Voronoi 群系 → CA 洞穴 → 矿脉（阈值+游走）→ 逐格 Compose。
/// 
/// 管线触发条件：world_gen.json 出现 terrain/biomes/caves/veins 任一段（v2 schema）；
/// 全缺省时 Generate(int seed) 也降级走旧分层（数据向后兼容）。
///
/// 确定性：svc 由调用方显式传非 0；0 = 时间派生（设计文档 §5）。
/// 世界完全由 seed 重放，不落盘（存档格式 v1 零改动）。
/// </summary>
public sealed class WorldGenerator
{
    // -------- 旧分层字段 --------
    private readonly WorldGenDef _def;
    private readonly BlockRegistry _registry;
    private readonly ushort[] _layerIndices;

    // -------- v2 管线缓存（构造期解析块 ID，数据错配尽早暴露） --------
    private readonly bool _hasPipeline;
    private readonly int _basaltTop;        // 玄武岩顶行（含）——底盘游戏边界
    private readonly ushort _basaltIndex;
    private readonly int[] _biomeWeights = Array.Empty<int>();   // 群系权重（下标 = defs 索引）
    private readonly ushort[] _surfaceIdx = Array.Empty<ushort>();  // 群系表层块索引（下标 = defs 索引）
    private readonly ushort[] _subIdx = Array.Empty<ushort>();      // 群系下层块索引（下标 = defs 索引）
    private readonly List<VeinPlan> _veinPlans = new(); // 解析后的矿脉计划

    public WorldGenerator(IDataLoader loader, BlockRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _def = loader.Load<WorldGenDef>("world_gen.json") ?? throw new InvalidOperationException("world_gen.json 解析为空");

        // ---- 旧分层校验（fallback 路径仍完全使用） ----
        if (_def.Width <= 0) throw new InvalidOperationException($"world_gen 宽度非法：{_def.Width}");
        if (_def.Height <= 0) throw new InvalidOperationException($"world_gen 高度非法：{_def.Height}");
        if (_def.AirRows < 0 || _def.AirRows >= _def.Height)
            throw new InvalidOperationException($"world_gen 空行数非法：{_def.AirRows}");
        if (_def.Layers == null || _def.Layers.Count == 0)
            throw new InvalidOperationException("world_gen 缺少分层配置");

        _layerIndices = new ushort[_def.Layers.Count];
        var fixedRows = 0;
        for (int i = 0; i < _def.Layers.Count; i++)
        {
            var layer = _def.Layers[i];
            var isLast = i == _def.Layers.Count - 1;
            _layerIndices[i] = _registry.GetIndex(layer.Block);
            if (isLast && layer.Thickness == -1) continue;
            if (layer.Thickness <= 0)
                throw new InvalidOperationException($"第 {i + 1} 层厚度非法：{layer.Thickness}（仅末层可用 -1 表剩余）");
            fixedRows += layer.Thickness;
        }
        if (_def.Layers[^1].Thickness != -1)
            throw new InvalidOperationException("末层厚度须为 -1（填满剩余行），避免高度改后出现未填充区");
        if (_def.AirRows + fixedRows > _def.Height)
            throw new InvalidOperationException($"分层总厚 {fixedRows} + 空行 {_def.AirRows} 超出高度 {_def.Height}");

        // ---- v2 管线解析 ----
        _hasPipeline = _def.Terrain != null || _def.Biomes != null || _def.Caves != null || _def.Veins.Count > 0;
        if (_hasPipeline)
        {
            var t = _def.Terrain ?? new TerrainDef();          // 缺段用内置默认常量
            var b = _def.Biomes ?? new BiomeRootDef();
            var c = _def.Caves ?? new CaveDef();
            if (b.Defs == null || b.Defs.Count == 0)
                throw new InvalidOperationException("管线模式 biomes.defs 不可为空");

            if (t.Octaves <= 0) throw new InvalidOperationException($"terrain.octaves 非法：{t.Octaves}");
            if (t.BasaltThickness <= 0) throw new InvalidOperationException($"terrain.basalt_thickness 非法：{t.BasaltThickness}");
            _basaltTop = _def.Height - t.BasaltThickness;
            if (_basaltTop < _def.AirRows + 2)
                throw new InvalidOperationException($"玄武岩顶 {_basaltTop} 与空行 {_def.AirRows} 冲突（无可生成带）");
            _basaltIndex = _registry.GetIndex(t.BasaltBlock);

            // 高度场钳制上界：保证洞穴带 [maxH+2, basaltTop-2] 合法
            float maxSurface = _basaltTop - 4;
            if (t.BaseHeight + t.HeightAmp > maxSurface || t.BaseHeight - t.HeightAmp < _def.AirRows + 1)
                throw new InvalidOperationException(
                    $"terrain 高度带 [{t.BaseHeight - t.HeightAmp},{t.BaseHeight + t.HeightAmp}] 超出可生成范围 [{_def.AirRows + 1},{maxSurface}]");

            _biomeWeights = new int[b.Defs.Count];
            _surfaceIdx = new ushort[b.Defs.Count];
            _subIdx = new ushort[b.Defs.Count];
            for (int i = 0; i < b.Defs.Count; i++)
            {
                var d = b.Defs[i];
                if (d.Weight <= 0) throw new InvalidOperationException($"群系 {d.Id} 权重非法：{d.Weight}");
                _biomeWeights[i] = d.Weight;
                _surfaceIdx[i] = _registry.GetIndex(d.Surface);
                _subIdx[i] = _registry.GetIndex(d.Sub);
            }

            if (b.CellCount <= 0 || b.CellCount > _def.Width * _def.Height / 4)
                throw new InvalidOperationException($"biomes.cell_count 非法：{b.CellCount}");
            if (b.BlendWidth < 0) throw new InvalidOperationException($"biomes.blend_width 非法：{b.BlendWidth}");
            if (c.Density < 0f || c.Density > 1f) throw new InvalidOperationException($"caves.density 非法：{c.Density}");
            if (c.Iterations < 0) throw new InvalidOperationException($"caves.iterations 非法：{c.Iterations}");

            foreach (var v in _def.Veins)
                _veinPlans.Add(new VeinPlan(_registry.GetIndex(v.Ore)) { Def = v });
        }
    }

    public int Width => _def.Width;
    public int Height => _def.Height;

    /// <summary>旧分层模式（S1 兼容）：无参调用永远走此路径。</summary>
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
            if (rowBlock[y] == 0) continue;
            for (int x = 0; x < _def.Width; x++)
                grid.Set(x, y, rowBlock[y]);
        }
        return grid;
    }

    /// <summary>
    /// 管线模式：seed 非 0 确定性重放；seed=0 时间派生。
    /// v2 数据段缺省时降级旧分层（与 Generate() 等价）。
    /// </summary>
    public BlockGrid Generate(int seed)
    {
        if (!_hasPipeline) return Generate();

        int effSeed = seed != 0
            ? seed
            : unchecked((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7FFFFFFF));
        return GeneratePipelined(effSeed);
    }

    // ==================== 管线 ====================

    private BlockGrid GeneratePipelined(int seed)
    {
        int w = _def.Width, h = _def.Height;
        var grid = new BlockGrid(w, h);

        // ① Perlin 高度场（子种子：黄金比例散列，设计文档 §5）
        var terrain = _def.Terrain ?? new TerrainDef();
        var perlin = new PerlinNoise(seed ^ unchecked((int)0x9E3779B9u));
        float[] heightMap = new float[w];
        int maxH = int.MinValue;
        for (int x = 0; x < w; x++)
        {
            float v = terrain.BaseHeight
                + terrain.HeightAmp * perlin.Fractal(x * terrain.SampleFreq, 0f,
                    terrain.Octaves, terrain.Persistence, terrain.Lacunarity);
            heightMap[x] = Math.Clamp(v, _def.AirRows + 1, _basaltTop - 4);
            maxH = Math.Max(maxH, (int)heightMap[x]);
        }

        // ② Voronoi 群系（单元撒点 → 逐格指派 → 单元类别权重随机）
        var bio = _def.Biomes ?? new BiomeRootDef();
        var points = VoronoiField.SpawnPoints(seed ^ unchecked((int)0x85EBCA6Bu), w, h, bio.CellCount);
        var assign = VoronoiField.Assign(w, h, points);
        var cellClass = VoronoiField.NextCellClass(seed ^ unchecked((int)0x85EBCA6Bu), points.Length, _biomeWeights);

        // 每列（地表行）所属群系
        // 注：高度场 1D 采样 → 地表行随列变化，取该列地表格的单元归属
        var colClass = new int[w];
        for (int x = 0; x < w; x++)
            colClass[x] = cellClass[assign[(int)heightMap[x] * w + x]];

        // ③ CA 洞穴（石层带 [maxH+2, basaltTop-2]）
        var caveDef = _def.Caves ?? new CaveDef();
        var open = CaveAutomata.Generate(seed ^ unchecked((int)0xC2B2AE3Du), w, h,
            bandTop: maxH + 2, bandBottom: _basaltTop - 2,
            caveDef.Density, caveDef.Iterations, caveDef.WallThreshold, caveDef.MinRoom);

        // ④ 矿脉（阈值通道 + 锚点游走通道）
        var oreMark = new ushort[w * h];
        foreach (var plan in _veinPlans)
            StampVeins(plan, seed, w, h, heightMap, oreMark);

        // ⑤ Compose：自底向上叠加（设计文档 §3 优先级：表面 > 下层；矿 > 下层；洞穴清空最高）
        int blW = bio.BlendWidth;
        for (int x = 0; x < w; x++)
        {
            int surfY = (int)heightMap[x];
            int cls = colClass[x];
            // 下层（地表下、玄武岩上）
            for (int y = surfY + 1; y < _basaltTop; y++)
                grid.Set(x, y, _subIdx[cls]);
            // 玄武岩底盘
            for (int y = _basaltTop; y < h; y++)
                grid.Set(x, y, _basaltIndex);
            // 表层块（blend 带内二选一）
            grid.Set(x, surfY, SurfaceForColumn(x, cls, colClass, w, blW, perlin));
        }
        // 矿脉覆盖下层（深度带不与地表重叠）
        for (int i = 0; i < oreMark.Length; i++)
            if (oreMark[i] != 0) grid.Set(i % w, i / w, oreMark[i]);
        // 洞穴清空（最高优先）
        for (int y = maxH + 2; y <= _basaltTop - 2; y++)
            for (int x = 0; x < w; x++)
                if (open[y * w + x]) grid.Set(x, y, 0);

        return grid;
    }

    /// <summary>
    /// 群系边界混合（简化实现）：本列 ±blendWidth 内出现异群系时，
    /// 用高度场噪声阈值在本列群系表层块与首个异群系表层块间确定性二选一。
    /// </summary>
    private ushort SurfaceForColumn(int x, int cls, int[] colClass, int w, int blendWidth, PerlinNoise noise)
    {
        if (blendWidth <= 0) return _surfaceIdx[cls];

        int other = -1;
        for (int dx = -blendWidth; dx <= blendWidth && other < 0; dx++)
        {
            if (dx == 0) continue;
            int nx = x + dx;
            if (nx < 0 || nx >= w) continue;
            if (colClass[nx] != cls) other = colClass[nx];
        }
        if (other < 0) return _surfaceIdx[cls]; // 群系内部：无混合

        float n = noise.Fractal(x * 0.31f, 13.7f, octaves: 2, persistence: 0.5f, lacunarity: 2f);
        return n > 0f ? _surfaceIdx[cls] : _surfaceIdx[other];
    }

    /// <summary>矿脉双通道：① 阈值噪声条带；② 锚点随机游走团簇。</summary>
    private void StampVeins(VeinPlan plan, int seed, int w, int h, float[] heightMap, ushort[] oreMark)
    {
        var v = plan.Def;
        if (v.DepthBand == null || v.DepthBand.Length != 2 || v.DepthBand[0] >= v.DepthBand[1])
            throw new InvalidOperationException($"矿脉 {v.Ore} depth_band 非法");

        int bandTop = v.DepthBand[0];
        int bandBottom = Math.Min(v.DepthBand[1], _basaltTop - 1);
        var noise = new PerlinNoise(seed ^ unchecked((int)0x27D4EB2Fu) ^ plan.OreIndex);

        // ① 阈值通道
        for (int y = bandTop; y <= bandBottom; y++)
            for (int x = 0; x < w; x++)
            {
                if (y <= (int)heightMap[x]) continue; // 不侵占地表
                if (noise.Fractal(x * 0.12f, y * 0.12f, octaves: 3, persistence: 0.5f, lacunarity: 2f) > v.NoiseThreshold)
                    oreMark[y * w + x] = plan.OreIndex;
            }

        // ② 锚点游走通道
        var rng = new Random(seed ^ unchecked((int)0x27D4EB2Fu) ^ (plan.OreIndex * 7919));
        int stepLo = Math.Min(v.WalkSteps[0], v.WalkSteps[1]);
        int stepHi = Math.Max(v.WalkSteps[0], v.WalkSteps[1]);
        int half = Math.Max(1, v.Cluster / 2);
        for (int x = 0; x < w; x++)
        {
            if (rng.NextDouble() >= v.AnchorPerCol) continue;

            // 锚点从深度带上沿出发游走
            int cx = x, cy = bandTop + rng.Next(1, Math.Max(2, bandBottom - bandTop + 1));
            int steps = rng.Next(stepLo, stepHi + 1);
            for (int s = 0; s < steps; s++)
            {
                switch (rng.Next(8))
                {
                    case 0: cx++; break;
                    case 1: cx--; break;
                    case 2: cy++; break;
                    case 3: cy--; break;
                    case 4: cx++; cy++; break;
                    case 5: cx--; cy++; break;
                    case 6: cx++; cy--; break;
                    default: cx--; cy--; break;
                }
                cx = Math.Clamp(cx, 0, w - 1);
                cy = Math.Clamp(cy, bandTop, bandBottom);
                // 团簇落墨（集群 3 → 3×3；勿越深度带）
                for (int dy = -half; dy < v.Cluster - half; dy++)
                    for (int dx = -half; dx < v.Cluster - half; dx++)
                    {
                        int px = cx + dx, py = cy + dy;
                        if (px < 0 || px >= w || py < bandTop || py > bandBottom) continue;
                        oreMark[py * w + px] = plan.OreIndex;
                    }
            }
        }
    }

    /// <summary>解析后的矿脉计划（块索引 + 配置）。</summary>
    private sealed class VeinPlan
    {
        public ushort OreIndex { get; }
        public VeinDef Def { get; set; } = null!;

        public VeinPlan(ushort oreIndex) => OreIndex = oreIndex;
    }

    // ==================== world_gen.json 定义（v2） ====================

    private sealed class WorldGenDef
    {
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("air_rows")] public int AirRows { get; set; }
        [JsonPropertyName("layers")] public List<WorldGenLayer> Layers { get; set; } = new();
        [JsonPropertyName("seed")] public int Seed { get; set; }
        [JsonPropertyName("terrain")] public TerrainDef? Terrain { get; set; }
        [JsonPropertyName("biomes")] public BiomeRootDef? Biomes { get; set; }
        [JsonPropertyName("caves")] public CaveDef? Caves { get; set; }
        [JsonPropertyName("veins")] public List<VeinDef> Veins { get; set; } = new();
    }

    private sealed class WorldGenLayer
    {
        [JsonPropertyName("block")] public string Block { get; set; } = "";
        [JsonPropertyName("thickness")] public int Thickness { get; set; }
    }

    private sealed class TerrainDef
    {
        [JsonPropertyName("octaves")] public int Octaves { get; set; } = 4;
        [JsonPropertyName("persistence")] public float Persistence { get; set; } = 0.5f;
        [JsonPropertyName("lacunarity")] public float Lacunarity { get; set; } = 2f;
        [JsonPropertyName("base_height")] public float BaseHeight { get; set; } = 18f;
        [JsonPropertyName("height_amp")] public float HeightAmp { get; set; } = 4f;
        [JsonPropertyName("sample_freq")] public float SampleFreq { get; set; } = 0.05f;
        [JsonPropertyName("basalt_thickness")] public int BasaltThickness { get; set; } = 4;
        [JsonPropertyName("basalt_block")] public string BasaltBlock { get; set; } = "tile_basalt_firm";
    }

    private sealed class BiomeRootDef
    {
        [JsonPropertyName("cell_count")] public int CellCount { get; set; } = 8;
        [JsonPropertyName("blend_width")] public int BlendWidth { get; set; } = 2;
        [JsonPropertyName("defs")] public List<BiomeEntryDef> Defs { get; set; } = new();
    }

    private sealed class BiomeEntryDef
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("surface")] public string Surface { get; set; } = "";
        [JsonPropertyName("sub")] public string Sub { get; set; } = "";
        [JsonPropertyName("weight")] public int Weight { get; set; } = 1;
    }

    private sealed class CaveDef
    {
        [JsonPropertyName("density")] public float Density { get; set; } = 0.45f;
        [JsonPropertyName("iterations")] public int Iterations { get; set; } = 4;
        [JsonPropertyName("wall_threshold")] public int WallThreshold { get; set; } = 5;
        [JsonPropertyName("min_room")] public int MinRoom { get; set; } = 8;
    }
}

/// <summary>矿脉配置（world_gen.json veins 段条目）。</summary>
public sealed class VeinDef
{
    [JsonPropertyName("ore")] public string Ore { get; set; } = "";
    [JsonPropertyName("depth_band")] public int[] DepthBand { get; set; } = Array.Empty<int>();
    [JsonPropertyName("anchor_per_col")] public double AnchorPerCol { get; set; }
    [JsonPropertyName("walk_steps")] public int[] WalkSteps { get; set; } = new[] { 20, 40 };
    [JsonPropertyName("cluster")] public int Cluster { get; set; } = 3;
    [JsonPropertyName("noise_threshold")] public float NoiseThreshold { get; set; } = 0.62f;
}