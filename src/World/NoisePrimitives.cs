using System;
using System.Collections.Generic;

namespace ProjectSandbox.World;

// ============================================================================
// 世界生成原语库（阶段 1）：Perlin 噪声 + Voronoi 分区 + 细胞自动机洞穴。
//
// 设计约束（docs/世界生成算法设计.md §4）：
// 1) 纯 C#，零 Godot 依赖——本库输出可被纯逻辑层（WorldGenerator）直接消费；
// 2) 零第三方库——三个算法全部原创手写（公开教科书算法，非复制任何游戏代码）；
// 3) 确定性——全部随机源来自 System.Random(seed)。
//
// 与游戏运行时随机的分界（项目纪律补充说明）：游戏内掉落等运行时随机沿用
// RandomNumberGenerator（Godot 自带）；而世界生成是离线性管线，要求「同一
// seed 跨引擎版本稳定重放存档世界」，因此这里改用 System.Random(seed)——
// .NET 规范保证同 seed 同序列，跨平台/跨版本输出一致。两套随机源互不混用。
// ============================================================================

/// <summary>
/// 经典梯度噪声（2D）+ 分形叠加。值域 [-1, 1]。
/// 置换表由 seed 派生的洗牌生成——非固定表，避免所有世界共享同一噪声形状。
/// </summary>
public sealed class PerlinNoise
{
    private readonly int[] _perm = new int[512];

    public PerlinNoise(int seed)
    {
        // seed 派生标准 Fisher-Yates 洗牌（System.Random 同 seed 同序列，确定性成立）
        var rng = new Random(seed);
        var p = new int[256];
        for (int i = 0; i < 256; i++) p[i] = i;
        for (int i = 255; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++) _perm[i] = p[i & 255];
    }

    /// <summary>单倍频采样，值域 [-1, 1]。</summary>
    public float Noise(float x, float y)
    {
        int xi = FloorToInt(x) & 255;
        int yi = FloorToInt(y) & 255;
        float xf = x - MathF.Floor(x);
        float yf = y - MathF.Floor(y);

        // 晶格四角梯度点乘
        float n00 = Grad(_perm[_perm[xi] + yi], xf, yf);
        float n10 = Grad(_perm[_perm[xi + 1] + yi], xf - 1f, yf);
        float n01 = Grad(_perm[_perm[xi] + yi + 1], xf, yf - 1f);
        float n11 = Grad(_perm[_perm[xi + 1] + yi + 1], xf - 1f, yf - 1f);

        // fade 曲线混合：先 X 后 Y
        float u = Fade(xf);
        float v = Fade(yf);
        float nx0 = n00 + u * (n10 - n00);
        float nx1 = n01 + u * (n11 - n01);
        return nx0 + v * (nx1 - nx0);
    }

    /// <summary>
    /// 分形叠加（多倍频）：octaves 层、persistence 振幅衰减、lacunarity 频率倍增。
    /// 返回归一化到 [-1, 1]。
    /// </summary>
    public float Fractal(float x, float y, int octaves, float persistence, float lacunarity)
    {
        if (octaves <= 0) throw new ArgumentOutOfRangeException(nameof(octaves));

        float total = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            total += Noise(x * frequency, y * frequency) * amplitude;
            norm += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return total / norm;
    }

    private static int FloorToInt(float v) => (int)MathF.Floor(v);

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    /// <summary>8 方向单位梯度（hash 低 3 位映射，对角/轴向混合）。</summary>
    private static float Grad(int hash, float x, float y)
    {
        switch (hash & 7)
        {
            case 0: return x;                                  // ( 1, 0)
            case 1: return -x;                                 // (-1, 0)
            case 2: return y;                                  // ( 0, 1)
            case 3: return -y;                                 // ( 0,-1)
            case 4: return 0.70710678f * (x + y);              // ( a, a)
            case 5: return 0.70710678f * (-x + y);             // (-a, a)
            case 6: return 0.70710678f * (x - y);              // ( a,-a)
            default: return 0.70710678f * (-x - y);            // (-a,-a)
        }
    }
}

/// <summary>
/// Voronoi 分区原语：单元撒点 + 逐格最近邻指派 + 单元类别权重随机。
/// 阶段 2 的 WorldGenerator 把「单元类别」映射为群系（BiomeMap）。
/// </summary>
public static class VoronoiField
{
    public readonly struct CellPoint
    {
        public readonly float X;
        public readonly float Y;

        public CellPoint(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>在 [0,width)×[0,height) 内撒 count 个单元点（seed 确定）。</summary>
    public static CellPoint[] SpawnPoints(int seed, int width, int height, int count)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

        var rng = new Random(seed);
        var points = new CellPoint[count];
        for (int i = 0; i < count; i++)
            points[i] = new CellPoint((float)(rng.NextDouble() * width), (float)(rng.NextDouble() * height));
        return points;
    }

    /// <summary>
    /// 逐格指派最近单元：返回 int[width*height]（row-major），每格 ∈ [0, count)。
    /// 暴力最近邻——64×48×8 点 ≈ 2.5 万次距离计算，微秒级；大世界再换 JFA（阶段 2 预留）。
    /// </summary>
    public static int[] Assign(int width, int height, CellPoint[] points)
    {
        if (points == null || points.Length == 0) throw new ArgumentException("单元点为空", nameof(points));

        var map = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int best = 0;
                float bestDist = float.MaxValue;
                for (int i = 0; i < points.Length; i++)
                {
                    float dx = x + 0.5f - points[i].X;
                    float dy = y + 0.5f - points[i].Y;
                    float d = dx * dx + dy * dy;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = i;
                    }
                }
                map[y * width + x] = best;
            }
        }
        return map;
    }

    /// <summary>
    /// 为每个单元抽一个类别索引（权重随机，前缀和命中——与 LootSystem 同模式）。
    /// weights.Length 必须 ≥ 类别数；权重全 0 或不足时抛异常（配置错误早暴露）。
    /// </summary>
    public static int[] NextCellClass(int seed, int cellCount, int[] weights)
    {
        if (cellCount <= 0) throw new ArgumentOutOfRangeException(nameof(cellCount));
        if (weights == null || weights.Length == 0) throw new ArgumentException("权重表为空", nameof(weights));

        int total = 0;
        foreach (var w in weights)
        {
            if (w < 0) throw new ArgumentOutOfRangeException(nameof(weights), "权重不允许负数");
            total += w;
        }
        if (total == 0) throw new InvalidOperationException("权重全为 0，无法抽取类别");

        var rng = new Random(seed);
        var classes = new int[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            int roll = rng.Next(total);
            int acc = 0;
            for (int c = 0; c < weights.Length; c++)
            {
                acc += weights[c];
                if (roll < acc)
                {
                    classes[i] = c;
                    break;
                }
            }
        }
        return classes;
    }
}

/// <summary>
/// 细胞自动机洞穴：经典 4-5 规则（出生需八邻墙 ≥ threshold；存活需八邻墙 ≥ threshold-1）+ 孤立空腔清扫。
/// 「墙」语义 = 石层岩体；输出 open 掩码（true = 洞穴空腔），仅 band 区间内可能为 true。
/// </summary>
public static class CaveAutomata
{
    /// <summary>
    /// 生成洞穴掩码。
    /// </summary>
    /// <param name="bandTop">石层带顶行（含）——cave 仅在此区间生成。</param>
    /// <param name="bandBottom">石层带底行（含）。</param>
    /// <param name="density">初始墙概率 [0,1]。</param>
    /// <param name="iterations">CA 迭代轮数。</param>
    /// <param name="wallThreshold">八邻墙成墙阈值（经典 4-5 规则取 5：出生 ≥5、存活 ≥4）。</param>
    /// <param name="minRoom">孤立空腔清扫阈值：BFS 分量空腔格数小于该值的填回岩体；≤0 跳过清扫。</param>
    /// <returns>bool[width*height]（row-major），true = 该格是洞穴空腔（open）。</returns>
    public static bool[] Generate(int seed, int width, int height,
        int bandTop, int bandBottom, float density, int iterations, int wallThreshold, int minRoom)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (bandTop < 0 || bandBottom >= height || bandTop > bandBottom)
            throw new ArgumentOutOfRangeException(nameof(bandTop), "岩层带行区间非法");
        if (density < 0f || density > 1f) throw new ArgumentOutOfRangeException(nameof(density));
        if (iterations < 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (wallThreshold < 0 || wallThreshold > 8) throw new ArgumentOutOfRangeException(nameof(wallThreshold));

        int size = width * height;
        var rock = new bool[size]; // true = 墙（岩体）

        // 初始化：仅 band 区间内随机成墙
        var rng = new Random(seed);
        for (int y = bandTop; y <= bandBottom; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
                rock[row + x] = rng.NextDouble() < density;
        }

        for (int it = 0; it < iterations; it++)
            rock = GenerationStep(rock, width, height, wallThreshold);

        // 转换为 open 掩码（band 内：!rock；band 外恒 false）
        var open = new bool[size];
        for (int y = bandTop; y <= bandBottom; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
                open[row + x] = !rock[row + x];
        }

        if (minRoom > 0) RemoveIsolatedCavities(open, width, height, minRoom);
        return open;
    }

    /// <summary>
    /// 单轮 CA 迭代（经典 4-5 规则，八邻含对角）：
    /// 「空」格成墙需墙计数 ≥ threshold（出生 5）；已为「墙」的格子存活需墙计数 ≥ threshold-1（存活 4）。
    /// 越界邻格视为岩体（保守：边缘洞穴不侵蚀世界边界）。
    /// </summary>
    public static bool[] GenerationStep(bool[] current, int width, int height, int threshold)
    {
        var next = new bool[current.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int walls = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        // 边界外视为岩体（保守：边缘洞穴不侵蚀世界边界）
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) { walls++; continue; }
                        if (current[ny * width + nx]) walls++;
                    }
                }
                int idx = y * width + x;
                next[idx] = walls >= threshold || (walls == threshold - 1 && current[idx]);
            }
        }
        return next;
    }

    /// <summary>BFS 连通分量分析：open 分量格数 &lt; minRoom 的孤腔填回 false（碎噪点洞不保留）。</summary>
    private static void RemoveIsolatedCavities(bool[] open, int width, int height, int minRoom)
    {
        var visited = new bool[open.Length];
        var queue = new Queue<int>();
        var component = new List<int>();

        for (int start = 0; start < open.Length; start++)
        {
            if (!open[start] || visited[start]) continue;

            // flood fill 一个 open 分量
            queue.Clear();
            component.Clear();
            queue.Enqueue(start);
            visited[start] = true;
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                component.Add(idx);
                int x = idx % width;
                int y = idx / width;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                        int nIdx = ny * width + nx;
                        if (open[nIdx] && !visited[nIdx])
                        {
                            visited[nIdx] = true;
                            queue.Enqueue(nIdx);
                        }
                    }
                }
            }

            if (component.Count < minRoom)
            {
                foreach (var idx in component)
                    open[idx] = false;
            }
        }
    }
}