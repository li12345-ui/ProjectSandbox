using Godot;

namespace ProjectSandbox;

/// <summary>
/// 跟随相机：平滑跟随目标节点、边界限制、滚轮缩放。
/// 与 WorldCamera（WASD 手动调试）职责分离——CameraController 用于玩家实体落地后的正常跟随。
/// 约束只改此文件，未在 Game.cs 接线——接线任务需后续独立完成。
/// </summary>
public sealed partial class CameraController : Camera2D
{
    /// <summary>跟随平滑速度（lerp = speed * dt，值越大跟随越快）。</summary>
    [Export] public float SmoothSpeed { get; set; } = 5f;

    [Export] public float MinZoom { get; set; } = 0.5f;
    [Export] public float MaxZoom { get; set; } = 4f;
    [Export] public float ZoomStep { get; set; } = 0.1f;

    /// <summary>跟随目标；null 时相机停在原位不动（不崩）。</summary>
    private Node2D? _target;

    /// <summary>世界物理边界（像素）；无边界时为 Rect2() 不钳制。</summary>
    private Rect2 _worldBounds;

    public CameraController() { }

    /// <summary>构造时传入目标节点 + 世界边界（可选）。</summary>
    public CameraController(Node2D? target, Rect2 worldBounds)
    {
        _target = target;
        _worldBounds = worldBounds;
    }

    /// <summary>运行时切换跟随目标。</summary>
    public void SetTarget(Node2D? target) => _target = target;

    /// <summary>运行时更新世界边界。</summary>
    public void SetWorldBounds(Rect2 bounds) => _worldBounds = bounds;

    public override void _PhysicsProcess(double delta)
    {
        if (_target == null) return; // 经验 #273903：null 守卫直接 return，不崩

        float dt = (float)delta;

        // 平滑跟随：Lerp(target.Position, smooth * dt)——delta 感知，避免高帧率过冲低帧率卡顿
        var desired = _target.GlobalPosition;
        float lerpAmount = Mathf.Clamp(SmoothSpeed * dt, 0f, 1f);
        Position = Position.Lerp(desired, lerpAmount);

        ClampToBounds();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } mb) return;

        // 滚轮缩放：上放大下缩小，clamp 后重新钳边界
        if (mb.ButtonIndex == MouseButton.WheelUp)
        {
            ApplyZoom(Zoom.X + ZoomStep);
            GetViewport().SetInputAsHandled();
        }
        else if (mb.ButtonIndex == MouseButton.WheelDown)
        {
            ApplyZoom(Zoom.X - ZoomStep);
            GetViewport().SetInputAsHandled();
        }
    }

    private void ApplyZoom(float z)
    {
        float clamped = Mathf.Clamp(z, MinZoom, MaxZoom);
        Zoom = new Vector2(clamped, clamped);
        ClampToBounds();
    }

    /// <summary>
    /// 视口半宽随 zoom 变化钳制相机位置——避免看见世界外的空洞；
    /// 世界比视口窄时锁中心轴（ClampAxis 处理 min > max 的情况）。
    /// </summary>
    private void ClampToBounds()
    {
        if (_worldBounds.Size == Vector2.Zero) return;

        var half = GetViewportRect().Size / (2f * Zoom.X);
        var pos = Position;
        var center = _worldBounds.GetCenter();

        pos.X = ClampAxis(pos.X, _worldBounds.Position.X + half.X, _worldBounds.End.X - half.X, center.X);
        pos.Y = ClampAxis(pos.Y, _worldBounds.Position.Y + half.Y, _worldBounds.End.Y - half.Y, center.Y);

        Position = pos;
    }

    /// <summary>单轴钳制；世界比视口窄（min > max）时该轴锁中心。</summary>
    private static float ClampAxis(float value, float min, float max, float center)
        => min > max ? center : Mathf.Clamp(value, min, max);
}
