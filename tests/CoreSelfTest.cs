using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Godot;
using ProjectSandbox.Core;

/// <summary>
/// M0 Core 层最小自测：ServiceLocator / MemoryEventBus / JsonDataLoader 三实现的静态+动态验证。
/// 运行方式：godot --headless --path . --script res://tests/CoreSelfTest.cs --quit
/// 全部通过退出码 0，任一失败退出码 1。
/// 说明：本类命名空间留空——Godot 按脚本路径实例化入口类，避免 --script 下类型解析歧义。
/// </summary>
public partial class CoreSelfTest : SceneTree
{
    private int _failed;

    public override void _Initialize()
    {
        TestServiceLocator();
        TestEventBus();
        TestDataLoader();
        GD.Print(_failed == 0
            ? "[tests] Core 自测全部通过"
            : $"[tests] Core 自测失败 {_failed} 项");
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

    private void TestServiceLocator()
    {
        var locator = new ServiceLocator();
        var bus = new MemoryEventBus();
        locator.Register<IEventBus>(bus);
        Check(ReferenceEquals(locator.Get<IEventBus>(), bus), "ServiceLocator 注册后可得同一实例");

        var duplicateThrows = false;
        try { locator.Register<IEventBus>(new MemoryEventBus()); }
        catch (InvalidOperationException) { duplicateThrows = true; }
        Check(duplicateThrows, "ServiceLocator 重复注册抛异常");

        var missingThrows = false;
        try { locator.Get<IDataLoader>(); }
        catch (InvalidOperationException) { missingThrows = true; }
        Check(missingThrows, "ServiceLocator 未注册获取抛异常");
    }

    private void TestEventBus()
    {
        var bus = new MemoryEventBus();
        var count = 0;
        var sub = bus.Subscribe<TestEvent>(e => count += e.Value);
        bus.Publish(new TestEvent(1));
        bus.Publish(new TestEvent(2));
        Check(count == 3, "EventBus 订阅期间收到两次发布");

        sub.Dispose();
        bus.Publish(new TestEvent(10));
        Check(count == 3, "EventBus 退订后不再收到发布");

        var called = 0;
        IDisposable self = null!;
        self = bus.Subscribe<TestEvent>(_ =>
        {
            called++;
            self.Dispose(); // 发布中自我退订：快照语义下本轮已执行
        });
        bus.Publish(new TestEvent(0));
        bus.Publish(new TestEvent(0));
        Check(called == 1, "EventBus 快照语义：回调内退订，本轮照常执行且后续不再送达");
    }

    private void TestDataLoader()
    {
        var loader = new JsonDataLoader();
        var catalog = loader.Load<TestItemCatalog>("test_items.json");
        Check(catalog.Items.Count == 1, "DataLoader 读取 items 数组共一条");
        Check(catalog.Items[0].ItemId == "mat_crystal_shell", "DataLoader 反序列化 item_id（snake_case 映射）");
        Check(catalog.Items[0].MaxStack == 99, "DataLoader 数值字段正确");

        var missingThrows = false;
        try { loader.Load<TestItemCatalog>("not_exists.json"); }
        catch (FileNotFoundException) { missingThrows = true; }
        Check(missingThrows, "DataLoader 文件缺失抛异常");
    }

    private sealed class TestEvent
    {
        public TestEvent(int value) { Value = value; }
        public int Value { get; }
    }

    private sealed class TestItemCatalog
    {
        [JsonPropertyName("items")]
        public List<TestItemEntry> Items { get; set; } = new();
    }

    private sealed class TestItemEntry
    {
        [JsonPropertyName("item_id")]
        public string ItemId { get; set; } = "";

        [JsonPropertyName("max_stack")]
        public int MaxStack { get; set; }
    }
}