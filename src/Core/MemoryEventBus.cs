using System;
using System.Collections.Generic;
using System.Threading;

namespace ProjectSandbox.Core;

/// <summary>
/// 全局事件总线的进程内实现（目录规范第 7 条冻结设施）：按事件类型分发。
/// 发布时对订阅列表做快照，允许回调内安全增删订阅；退订句柄 Dispose 即永久退订，重复 Dispose 无副作用。
/// </summary>
public sealed class MemoryEventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _subscriptions = new();

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new SubscriptionToken(this, typeof(TEvent), handler);
    }

    public void Publish<TEvent>(TEvent evt) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(evt);

        List<Delegate> snapshot;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var list))
            {
                return;
            }
            snapshot = new List<Delegate>(list);
        }

        // 某个回调抛异常会中断后续回调：由各回调自行兜底，总线不做吞异常
        foreach (var item in snapshot)
        {
            ((Action<TEvent>)item)(evt);
        }
    }

    private void RemoveSubscription(Type eventType, Delegate handler)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(eventType, out var list))
            {
                list.Remove(handler);
                if (list.Count == 0)
                {
                    _subscriptions.Remove(eventType);
                }
            }
        }
    }

    /// <summary>订阅句柄：构造即订阅，Dispose 即退订。</summary>
    private sealed class SubscriptionToken : IDisposable
    {
        private readonly MemoryEventBus _owner;
        private readonly Type _eventType;
        private readonly Delegate _handler;
        private int _disposed;

        public SubscriptionToken(MemoryEventBus owner, Type eventType, Delegate handler)
        {
            _owner = owner;
            _eventType = eventType;
            _handler = handler;

            lock (_owner._gate)
            {
                if (!_owner._subscriptions.TryGetValue(eventType, out var list))
                {
                    list = new List<Delegate>();
                    _owner._subscriptions[eventType] = list;
                }
                list.Add(handler);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _owner.RemoveSubscription(_eventType, _handler);
        }
    }
}