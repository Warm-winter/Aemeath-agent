# 优化修复总结

本次优化解决了三个主要问题。

## 问题1：MCP 服务超时和连接失败

### 现象
- tavily-mcp (SSE) 连接超时（90秒）
- windows_odr (stdio) 进程意外退出，stderr 显示乱码

### 修复
**文件**: `src/Aemeath.Core/MCP/McpRuntimeService.cs`

- 增加 SSE/HTTP 超时时间：
  - 后台加载：90秒 → 150秒
  - 手动测试：120秒 → 180秒
- stdio 超时保持 30秒/60秒（合理范围）

### 影响
- 改善慢速 MCP 服务的连接成功率
- 对于需要初始化时间较长的 SSE 服务更友好

---

## 问题2：输入框光标显示不准确

### 现象
使用键盘方向键移动光标时：
- 输入框显示的光标位置不更新（一直在最后）
- 实际光标位置已移动（输入/删除在正确位置）
- 光标的视觉位置与逻辑位置不同步

### 根因分析
在 `df4ada6` (UI彻底重构) 提交时，`ImeFixBehavior` 被简化：
- 移除了 `KeyUp` 事件处理
- 移除了 `RefreshCaretVisual` 方法中的 `CaretIndex` 重新设置
- 只保留了 `InvalidateVisual()`，不足以触发光标重绘

### 修复
**文件**: `src/Aemeath.Desktop/Behaviors/ImeFixBehavior.cs`

恢复完整的光标修复逻辑（从重构前版本）：

1. **恢复 KeyUp 处理**：
   - 监听方向键（Left/Right/Home/End/Up/Down）
   - 检测 IME 状态，避免干扰输入法
   - 触发光标视觉刷新

2. **恢复完整的 RefreshCaretVisual**：
   ```csharp
   private static void RefreshCaretVisual(TextBox textBox)
   {
       var idx = textBox.CaretIndex;
       
       // 强制布局重新计算
       textBox.InvalidateMeasure();
       textBox.InvalidateArrange();
       textBox.InvalidateVisual();
       
       // 重新设置 CaretIndex 强制光标装饰器更新
       Dispatcher.UIThread.Post(() =>
       {
           if (textBox.IsFocused)
           {
               textBox.CaretIndex = idx;
           }
       }, DispatcherPriority.Render);
   }
   ```

3. **关键点**：
   - 两阶段刷新：先使布局失效，再重设 CaretIndex
   - 使用 Dispatcher.Post 确保在布局完成后执行
   - 保持 IME 兼容性检查

### 影响
- 方向键移动光标时，视觉位置立即更新
- IME 输入不受影响
- 光标位置与逻辑位置始终同步

---

## 问题3：图片附件发送改进

### 现象
模型回复"无法直接看到图片"，只能看到文件

### 分析
代码本身已经正确实现了图片字节发送：
- `File.ReadAllBytesAsync` 读取图片
- `new ImageContent(bytes, mimeType)` 创建图片内容
- 并非发送路径字符串

可能的原因：
1. 文件读取失败但错误信息不够详细
2. 文件路径或权限问题导致静默失败
3. 空文件或损坏的图片

### 改进
**文件**: `src/Aemeath.Core/AI/KernelMixinBase.cs`

增强 `AppendImageAttachmentAsync` 的错误处理和日志：

```csharp
private static async Task AppendImageAttachmentAsync(...)
{
    try
    {
        // 1. 显式检查文件存在性
        if (!File.Exists(attachment.Path))
        {
            textBuilder.AppendLine($"图片文件不存在：{attachment.Path}");
            return;
        }

        // 2. 读取并检查文件大小
        var bytes = await File.ReadAllBytesAsync(attachment.Path, cancellationToken);
        if (bytes.Length == 0)
        {
            textBuilder.AppendLine("图片文件为空。");
            return;
        }

        // 3. 添加图片内容（字节数组）
        contentItems.Add(new ImageContent(bytes, attachment.MimeType));
        
        // 4. 明确告知模型图片已附加
        textBuilder.AppendLine($"已附加图片内容（{FormatBytes(bytes.Length)}），请查看并结合图片回答。");
    }
    catch (Exception ex)
    {
        // 5. 详细的错误信息，包含路径
        textBuilder.AppendLine($"图片读取失败：{ex.Message}，路径：{attachment.Path}");
    }
}
```

### 改进点
1. **显式文件存在检查**：避免 FileNotFound 异常
2. **空文件检测**：防止发送 0 字节内容
3. **详细成功消息**：明确告知模型已附加图片和大小
4. **详细错误信息**：包含完整路径便于诊断

### 影响
- 图片发送成功时有明确提示
- 失败时提供详细诊断信息
- 更容易排查文件访问问题

---

## 编译验证

所有修复已通过编译验证：

```
dotnet build Aemeath.sln -c Release
已成功生成。
    0 个警告
    0 个错误
```

---

## 测试建议

### MCP 超时
1. 测试 tavily-mcp SSE 连接（现在有 150秒超时）
2. 检查 windows_odr 的 stderr 输出是否仍有乱码
3. 如果仍失败，检查 MCP 服务器配置和网络连接

### 光标问题
1. 在聊天输入框输入文本
2. 使用方向键（←→）移动光标
3. 验证光标视觉位置与实际位置同步
4. 测试 IME 输入（拼音）确保不受影响

### 图片发送
1. 上传图片并发送给模型
2. 检查模型是否能看到并描述图片内容
3. 如果失败，查看聊天记录中的错误消息
4. 确认图片文件路径、权限和格式正确

---

## 技术说明

### 为什么光标问题需要 CaretIndex 重设？

Avalonia TextBox 的光标渲染机制：
1. `CaretIndex` 属性存储逻辑位置（实际编辑位置）
2. 光标装饰器（Caret Adorner）负责视觉渲染
3. 当用户按方向键时，`CaretIndex` 更新但装饰器可能不重绘
4. `InvalidateVisual()` 只是标记需要重绘，不保证触发装饰器更新
5. **重新设置 `CaretIndex`**（即使是相同值）会强制装饰器重新定位

这是 Avalonia 的一个已知限制，特别是在 IME 输入后。

### 为什么需要两阶段刷新？

```csharp
// 阶段1：使布局失效
textBox.InvalidateMeasure();
textBox.InvalidateArrange();
textBox.InvalidateVisual();

// 阶段2：布局完成后重设 CaretIndex
Dispatcher.UIThread.Post(() => {
    textBox.CaretIndex = idx;
}, DispatcherPriority.Render);
```

1. 第一阶段触发布局系统重新计算文本度量
2. `Dispatcher.Post` 确保在布局完成后执行第二阶段
3. 重设 `CaretIndex` 通知装饰器使用新的布局信息
4. `DispatcherPriority.Render` 确保在渲染前完成

---

## 遗留问题

如果 MCP 服务仍然失败，可能需要：

1. **tavily-mcp 超时**：
   - 检查网络连接和防火墙
   - 验证 API 密钥配置
   - 查看 tavily API 状态

2. **windows_odr 进程退出**：
   - stderr 乱码可能是编码问题（UTF-8 vs GBK）
   - 检查 odr.exe 是否正确安装和配置
   - 验证进程启动参数和环境变量

3. **图片仍无法查看**：
   - 检查使用的 AI 模型是否支持视觉（如 GPT-4V, Claude 3+）
   - 确认图片格式（PNG/JPG/WEBP）和大小限制
   - 查看聊天记录中的详细错误消息
