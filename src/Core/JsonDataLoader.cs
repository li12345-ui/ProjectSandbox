using System;
using System.IO;
using System.Text.Json;
using Godot;

namespace ProjectSandbox.Core;

/// <summary>
/// data/ 软内容的 JSON 读取实现（data 只读：本类不提供任何写回入口）。
/// 经 Godot FileAccess 读取 res://data/ 下文本，用 System.Text.Json 反序列化为强类型模型；
/// 键名映射策略由数据模型通过 JsonPropertyName 等特性自行声明，本类不干预。
/// </summary>
public sealed class JsonDataLoader : IDataLoader
{
    public T Load<T>(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("数据相对路径不能为空", nameof(relativePath));
        }

        var fullPath = "res://data/" + relativePath;
        using var file = Godot.FileAccess.Open(fullPath, Godot.FileAccess.ModeFlags.Read);
        if (file == null)
        {
            throw new FileNotFoundException($"数据文件缺失：{fullPath}", fullPath);
        }

        var json = file.GetAsText();
        try
        {
            return JsonSerializer.Deserialize<T>(json)
                ?? throw new JsonException($"数据文件内容为空或为 null：{fullPath}");
        }
        catch (JsonException ex)
        {
            throw new JsonException($"数据文件解析失败：{fullPath}（{ex.Message}）", ex);
        }
    }
}