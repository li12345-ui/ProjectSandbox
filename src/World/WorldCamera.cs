using Godot;

namespace ProjectSandbox.World;

/// <summary>
/// 调试相机（M1 过渡方案）：方向键/WASD 平移，滚轮缩放，视口钳制在世界矩形内。
/// 无玩家实体前的取景手段；玩家与跟随相机落地后由其替换并删除本类。
/// 直接轮询物理键位而不注册 input map——避免为调试功能改动 project.godot。
/// </summary>
public sealed partial class WorldCamera : Camera2D
{
    private const float MoveSpeed = 480f; // px/s，调试取景速度，非玩法数值
    private const float ZoomStep = 0.1f;
    private const float MinZoom = 0.5f;
    private const float MaxZoom = 4f;

    private readonly Rect2 _bounds;

    public WorldCamera(Rect2 worldBounds)
    {
        _bounds = worldBounds;
        Position = worldBounds.GetCenter();
        Zoom = Vector2.One;
    }

    public override void _Process(double delta)
    {
        var dir = Vector2.Zero;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) dir.X -= 1f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) dir.X += 1f;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) dir.Y -= 1f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) dir.Y += 1f;

        if (dir == Vector2.Zero) return;
        Position += dir.Normalized() * MoveSpeed * (float)delta;
        ClampToBounds();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex == MouseButton.WheelUp)
            ApplyZoom(Zoom.X + ZoomStep);
        else if (mb.ButtonIndex == MouseButton.WheelDown)
            ApplyZoom(Zoom.X - ZoomStep);
    }

    private void ApplyZoom(float z)
    {
        var clamped = Mathf.Clamp(z, MinZoom, MaxZoom);
        Zoom = new Vector2(clamped, clamped);
        ClampToBounds(); // 缩放改变可视范围后重新钳制
    }

    private void ClampToBounds()
    {
        // 视口半宽高（世界单位）随 zoom 变化：zoom>1 放大后可视世界范围更小
        var half = GetViewportRect().Size / (2f * Zoom);
        var center = _bounds.GetCenter();
        Position = new Vector2(
            ClampAxis(Position.X, _bounds.Position.X + half.X, _bounds.End.X - half.X, center.X),
            ClampAxis(Position.Y, _bounds.Position.Y + half.Y, _bounds.End.Y - half.Y, center.Y));
    }

    /// <summary>单轴钳制；世界比视口窄（min&gt;max）时该轴锁世界中心。</summary>
    private float ClampAxis(float value, float min, float max, float center)
        => min > max ? center : Mathf.Clamp(value, min, max);
}
