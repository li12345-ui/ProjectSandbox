using Godot;

namespace ProjectSandbox;

/// <summary>
/// 跟随相机 + 调试模式：平滑跟随目标节点、边界限制、滚轮缩放；
/// Shift+WASD 手动覆盖跟随——覆盖时不钳边界，松开 Shift 自动切回跟随。
/// </summary>
public sealed partial class CameraController : Camera2D
{
    [Export] public float SmoothSpeed { get; set; } = 5f;
    [Export] public float MinZoom { get; set; } = 0.5f;
    [Export] public float MaxZoom { get; set; } = 4f;
    [Export] public float ZoomStep { get; set; } = 0.1f;
    [Export] public float DebugMoveSpeed { get; set; } = 480f;

    private Node2D? _target;
    private Rect2 _worldBounds;
    private bool _debugOverride;

    public CameraController() { }

    public CameraController(Node2D? target, Rect2 worldBounds)
    {
        _target = target;
        _worldBounds = worldBounds;
    }

    public void SetTarget(Node2D? target) => _target = target;
    public void SetWorldBounds(Rect2 bounds) => _worldBounds = bounds;

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        // 调试模式：Shift+WASD 手动覆盖；无目标时自动进入
        _debugOverride = Input.IsKeyPressed(Key.Shift) || _target == null;

        if (_debugOverride)
        {
            // 手动平移（WASD + 方向键）
            float dx = (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right) ? 1f : 0f)
                     - (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left) ? 1f : 0f);
            float dy = (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down) ? 1f : 0f)
                     - (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up) ? 1f : 0f);
            var manualDir = new Vector2(dx, dy);
            if (manualDir != Vector2.Zero)
            {
                Position += manualDir.Normalized() * DebugMoveSpeed * dt;
            }
        }
        else if (_target != null)
        {
            // 平滑跟随：Lerp(target.Position, smooth * dt)
            var desired = _target.GlobalPosition;
            float lerpAmount = Mathf.Clamp(SmoothSpeed * dt, 0f, 1f);
            Position = Position.Lerp(desired, lerpAmount);
            ClampToBounds();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true } mb) return;

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

    private static float ClampAxis(float value, float min, float max, float center)
        => min > max ? center : Mathf.Clamp(value, min, max);
}
