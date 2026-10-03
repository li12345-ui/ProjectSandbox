using System;
using System.Diagnostics;

namespace ProjectSandbox.Core;

/// <summary>
/// 固定时间步主循环策略：渲染帧（_Process 的 delta）与逻辑帧（固定 dt）解耦。
/// 采用 accumulator + 上限 0.25s 的经典 fixed-step 模式（防 death spiral）。
/// 不依赖 Godot API，纯 C# 可单测；Game.cs 负责在 SceneTree 中驱动本类。
/// </summary>
public sealed class MainLoop
{
    /// <summary>固定逻辑步长（秒）。当前 1/60 = 约 16.67ms 一逻辑步。</summary>
    public double FixedDt { get; } = 1.0 / 60.0;

    /// <summary>accumulator 上限（秒）。持续低 FPS 时截断追赶，避免 spiral of death。</summary>
    public double MaxAccumulator { get; } = 0.25;

    /// <summary>全局时间缩放，默认 1.0；暂停时 Game 层置为 0 即可。</summary>
    public double TimeScale { get; set; } = 1.0;

    /// <summary>是否暂停。暂停时 Step 不推进 accumulator，逻辑不运行。</summary>
    public bool IsPaused { get; private set; }

    // -------- 运行时状态 --------

    private double _accumulator;
    private long _totalStepsPerformed;
    private int _frameStepsPerformed;
    private long _frameDurationNs;
    private Stopwatch _watch = Stopwatch.StartNew();
    private double _fps;
    private double _fpsAccumulator;
    private int _fpsFrameCount;
    private const int FpsWindow = 120;

    // -------- 公开只读观测 --------

    /// <summary>最近一次 _Process 中执行的固定步次数（0 表示本帧未执行逻辑步）。</summary>
    public int FrameStepsPerformed => _frameStepsPerformed;

    /// <summary>本帧处理耗时（纳秒）。由 Game.cs 在 Step 前后 Stopwatch 采样写入。</summary>
    public long FrameDurationNs => _frameDurationNs;

    /// <summary>滚动平均 FPS（最近 120 帧）。</summary>
    public double Fps => _fps;

    /// <summary>累计逻辑步总数。</summary>
    public long TotalStepsPerformed => _totalStepsPerformed;

    // -------- 控制 --------

    public void Pause() => IsPaused = true;
    public void Resume() => IsPaused = false;

    /// <summary>
    /// 推进主循环。由 Game._Process 在每帧调用一次。
    /// </summary>
    /// <param name="delta">Godot _Process 传入的渲染帧 delta（秒）。</param>
    public void Step(double delta)
    {
        // 时间采样：记录本帧开始，供 Game.cs 算耗时
        var frameStart = Stopwatch.GetTimestamp();

        // --- 性能计数：FPS 滚动平均 ---
        _fpsAccumulator += delta;
        _fpsFrameCount++;
        if (_fpsFrameCount >= FpsWindow)
        {
            _fps = _fpsFrameCount / _fpsAccumulator;
            _fpsAccumulator = 0;
            _fpsFrameCount = 0;
        }

        // --- 固定步长逻辑 ---
        if (IsPaused)
        {
            _frameStepsPerformed = 0;
            _accumulator = 0; // 暂停期间不累积，避免恢复后追赶
        }
        else
        {
            double scaled = delta * TimeScale;
            if (_accumulator + scaled > MaxAccumulator)
                _accumulator = MaxAccumulator;
            else
                _accumulator += scaled;

            _frameStepsPerformed = 0;
            while (_accumulator >= FixedDt)
            {
                _accumulator -= FixedDt;
                _totalStepsPerformed++;
                _frameStepsPerformed++;
            }
        }

        // --- 帧耗时采样（由 Game.cs 在本方法返回后读取） ---
        _frameDurationNs = Stopwatch.GetTimestamp() - frameStart;
    }
}
