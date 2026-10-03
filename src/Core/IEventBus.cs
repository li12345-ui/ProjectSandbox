using System;

namespace ProjectSandbox.Core;

/// <summary>
/// 全局事件总线接口：系统间解耦通信的唯一通道（目录规范第 7 条冻结设施）。
/// 事件请使用不可变数据载体；订阅方不得修改事件内容。
/// </summary>
public interface IEventBus
{
    /// <summary>订阅某类事件，返回退订句柄；句柄 Dispose 即退订。</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;

    /// <summary>向所有订阅者发布事件。</summary>
    void Publish<TEvent>(TEvent evt) where TEvent : class;
}