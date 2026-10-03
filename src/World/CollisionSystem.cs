using System;
using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 瓦片碰撞系统：纯 C# AABB 碰撞解析，对 BlockGrid 做分轴滑动检测。
/// 与 BlockInteraction 同模式——只依赖 BlockGrid + BlockRegistry，不引用 Godot Physics 2D 系统。
///
/// 为什么不用 Godot TileSet 碰撞形状？
/// (1) 约束只改此文件，无法修改 WorldRenderer 给 TileSet 加 collision shape；
/// (2) 经验 #411597：TileSet 碰撞配置依赖编辑器或复杂资源序列化，手动 AABB 对网格更可控；
/// (3) 沙盒游戏需要精确的逐格 solid 判定（BlockRegistry.IsSolid），Godot 物理层无法直接拿到。
///
/// 滑动实现：分轴解析——先位移 X 并解决 X 方向穿透（取最浅穿透量推回），
/// 再位移 Y 并解决 Y 方向穿透，这样玩家贴墙移动时 Y 速度不受阻（典型 2D 平台跳跃手感）。
/// </summary>
public sealed class CollisionSystem
{
    private readonly BlockGrid _grid;
    private readonly BlockRegistry _registry;
    private readonly int _tileSize;

    public CollisionSystem(BlockGrid grid, BlockRegistry registry, int tileSize)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        if (tileSize <= 0) throw new ArgumentOutOfRangeException(nameof(tileSize));
        _tileSize = tileSize;
    }

    // -------- 核心 API --------

    /// <summary>
    /// 解析 AABB 与瓦片网格的碰撞：给定当前位置、AABB 半尺寸、速度、delta，
    /// 返回修正后的位置（已解决穿透，滑动手感）。
    /// </summary>
    /// <param name="position">AABB 左上角（世界像素坐标）。</param>
    /// <param name="halfSize">AABB 半尺寸（宽/2, 高/2）。</param>
    /// <param name="velocity">本帧速度（px/s）。</param>
    /// <param name="delta">本帧 dt（秒）。</param>
    /// <returns>修正后的位置（AABB 左上角）。</returns>
    public Vector2 ResolveCollision(Vector2 position, Vector2 halfSize, Vector2 velocity, double delta)
    {
        float dt = (float)delta;
        float tx = velocity.X * dt;
        float ty = velocity.Y * dt;

        // X 轴：先位移，再扫描穿格，取最浅穿透量推回
        var newPos = position with { X = position.X + tx };
        newPos.X = ResolveAxis(newPos, halfSize, tx, isX: true);

        // Y 轴：同理
        newPos.Y += ty;
        newPos.Y = ResolveAxis(newPos, halfSize, ty, isX: false);

        return newPos;
    }

    /// <summary>
    /// AABB 下沿 +1px 是否踩在 solid 格上——供 CharacterBody2D.IsOnFloor 手动实现。
    /// </summary>
    /// <param name="position">AABB 左上角。</param>
    /// <param name="halfSize">AABB 半尺寸。</param>
    public bool IsOnGround(Vector2 position, Vector2 halfSize)
    {
        float probeY = position.Y + halfSize.Y * 2 + 1f; // AABB 下沿 + 1px
        float left = position.X;
        float right = position.X + halfSize.X * 2;

        int cx0 = Mathf.FloorToInt(left / _tileSize);
        int cx1 = Mathf.FloorToInt(right / _tileSize);
        int cy = Mathf.FloorToInt(probeY / _tileSize);

        for (int x = cx0; x <= cx1; x++)
        {
            if (_grid.InBounds(x, cy) && _registry.IsSolid(_grid.Get(x, cy)))
                return true;
        }
        return false;
    }

    // -------- 私有：分轴穿透解决 --------

    /// <summary>
    /// 在指定轴上扫描 AABB 覆盖的格，对每个 solid 格算 AABB-vs-格的穿透量，
    /// 取最浅（绝对值最小）的推回量。未穿透返回原值。
    /// </summary>
    private float ResolveAxis(Vector2 pos, Vector2 half, float travel, bool isX)
    {
        float aabbMin = isX ? pos.X : pos.Y;
        float aabbMax = aabbMin + (isX ? half.X * 2 : half.Y * 2);

        // 覆盖格范围（FloorToInt 取 min，Mathf.CeilToInt-1 取 max，避免浮点边界漏格）
        int c0 = Mathf.FloorToInt(aabbMin / _tileSize);
        int c1 = Mathf.Max(c0, Mathf.CeilToInt(aabbMax / _tileSize) - 1);

        float pushback = 0f; // 正=往负方向推回，负=往正方向推回

        for (int c = c0; c <= c1; c++)
        {
            // 遍历另一轴的覆盖格
            int otherMin = isX ? Mathf.FloorToInt(pos.Y / _tileSize) : Mathf.FloorToInt(pos.X / _tileSize);
            int otherMax = Mathf.Max(otherMin, Mathf.CeilToInt((isX ? pos.Y + half.Y * 2 : pos.X + half.X * 2) / _tileSize) - 1);

            for (int o = otherMin; o <= otherMax; o++)
            {
                int gx = isX ? c : o;
                int gy = isX ? o : c;
                if (!_grid.InBounds(gx, gy)) continue;

                var idx = _grid.Get(gx, gy);
                if (!_registry.IsSolid(idx)) continue;

                // 格的 AABB：[gx*tile, (gx+1)*tile) × [gy*tile, (gy+1)*tile)
                float tileMin = (isX ? gx : gy) * _tileSize;
                float tileMax = tileMin + _tileSize;

                // AABB 与格在该轴上的重叠区间
                float overlapMin = Mathf.Max(aabbMin, tileMin);
                float overlapMax = Mathf.Min(aabbMax, tileMax);
                float overlap = overlapMax - overlapMin;

                // 未重叠 = 未穿透
                if (overlap <= 0f) continue;

                // 推回方向：根据本帧移动方向决定。移动为 0 时取离 AABB 更近的一侧
                float pb;
                if (travel > 0f)
                    pb = -(aabbMax - tileMin); // 右移 → 把 AABB 左沿拉到 tileMin
                else if (travel < 0f)
                    pb = tileMax - aabbMin;     // 左移 → 把 AABB 右沿推到 tileMax
                else
                {
                    // 静止：取离 AABB 最近的 tile 边
                    float distLeft = aabbMin - tileMin;
                    float distRight = tileMax - aabbMax;
                    pb = distLeft < distRight ? -distLeft : distRight;
                }

                // 取绝对值最小的推回量（最浅穿透）
                if (Mathf.Abs(pb) < Mathf.Abs(pushback) || pushback == 0f)
                    pushback = pb;
            }
        }

        return aabbMin + pushback;
    }
}
