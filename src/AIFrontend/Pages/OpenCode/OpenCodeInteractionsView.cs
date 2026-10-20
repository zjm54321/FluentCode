using System;
using System.Collections.Generic;
using System.Text.Json;
using AIFrontend.Services.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIFrontend.Pages.OpenCode;

/// <summary>一次 Apply 中“已不再待处理且非本页操作导致”的条目（供页面提示，不猜测原因）。</summary>
internal sealed record OpenCodeInteractionsRemovedItem(bool IsForm, string Id, string Title);

internal sealed class PermissionDecisionRequestedEventArgs : EventArgs
{
    internal PermissionDecisionRequestedEventArgs(OpenCodePermissionRequest request, string decision)
    {
        Request = request;
        Decision = decision;
    }

    public OpenCodePermissionRequest Request { get; }

    public string Decision { get; }
}

internal sealed class PermissionRecheckRequestedEventArgs : EventArgs
{
    internal PermissionRecheckRequestedEventArgs(string requestId) => RequestId = requestId;

    public string RequestId { get; }
}

internal sealed class FormSubmitRequestedEventArgs : EventArgs
{
    internal FormSubmitRequestedEventArgs(OpenCodeFormInfo form, IReadOnlyDictionary<string, JsonElement> answer)
    {
        Form = form;
        Answer = answer;
    }

    public OpenCodeFormInfo Form { get; }

    public IReadOnlyDictionary<string, JsonElement> Answer { get; }
}

internal sealed class FormCancelRequestedEventArgs : EventArgs
{
    internal FormCancelRequestedEventArgs(OpenCodeFormInfo form) => Form = form;

    public OpenCodeFormInfo Form { get; }
}

internal sealed class FormRecheckRequestedEventArgs : EventArgs
{
    internal FormRecheckRequestedEventArgs(string formId) => FormId = formId;

    public string FormId { get; }
}

/// <summary>
/// 阻断态“必要确认”结果：<see cref="Retry"/> 为 true 表示用户显式选择“仍要重新决定”
/// （接受可能重复生效的风险），false 表示“视为已结算”。两者都不由本视图发送任何请求。
/// </summary>
internal sealed class OpenCodeResolutionRequestedEventArgs : EventArgs
{
    internal OpenCodeResolutionRequestedEventArgs(bool isForm, string id, bool retry)
    {
        IsForm = isForm;
        Id = id;
        Retry = retry;
    }

    public bool IsForm { get; }

    public string Id { get; }

    public bool Retry { get; }
}

/// <summary>
/// 待处理交互面板（权限卡片 + 表单卡片）的纯 C# 容器，增量更新以保持用户输入与阅读位置。
/// </summary>
/// <remarks>
/// <para>
/// Apply 按 id + 指纹增量维护卡片：未变化的卡片不触碰（表单中输入中的内容不会因 2 秒轮询被重置）；
/// 指纹变化时就地重建该卡片（明确提示已重置输入）；消失的条目被移除，
/// 且仅当消失不是本页操作（RemoveSettled / MarkOtherPermissionsSettled）导致时才向上报告，
/// 由页面以低干扰方式提示“可能已由其他客户端处理”。
/// </para>
/// <para>
/// 面板只负责展示与收集用户输入；所有提交都必须回到页面，由页面重新查询、校验后调用客户端。
/// </para>
/// </remarks>
internal sealed class OpenCodeInteractionsView : StackPanel
{
    private sealed class CardEntry
    {
        public required string Key { get; init; }

        public required bool IsForm { get; init; }

        public required string Id { get; init; }

        public required string Title { get; init; }

        public required FrameworkElement Element { get; init; }

        public OpenCodePermissionCard? PermissionCard { get; init; }

        public OpenCodeFormCard? FormCard { get; init; }

        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>提问条目当前所在的时间线锚点；位于本面板（待回答区）时为 null。</summary>
        public OpenCodeQuestionAnchorHost? AnchorHost { get; set; }
    }

    private readonly List<CardEntry> _entries = new();
    private readonly Dictionary<string, CardEntry> _byKey = new(StringComparer.Ordinal);

    /// <summary>本页操作已结算的条目 key（其消失不再向上报告，避免与操作结果提示重复）。</summary>
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);

    /// <summary>已按共享保护/用户选择抑制的条目 key（服务端仍返回时不再重建卡片，直至其消失）。</summary>
    private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);

    internal OpenCodeInteractionsView()
    {
        Orientation = Orientation.Vertical;
        Spacing = 8;
    }

    internal event EventHandler<PermissionDecisionRequestedEventArgs>? PermissionDecisionRequested;

    internal event EventHandler<PermissionRecheckRequestedEventArgs>? PermissionRecheckRequested;

    internal event EventHandler<FormSubmitRequestedEventArgs>? FormSubmitRequested;

    internal event EventHandler<FormCancelRequestedEventArgs>? FormCancelRequested;

    internal event EventHandler<FormRecheckRequestedEventArgs>? FormRecheckRequested;

    /// <summary>阻断态“必要确认”结果（仍要重新决定 / 视为已结算）；由页面执行重查与解除。</summary>
    internal event EventHandler<OpenCodeResolutionRequestedEventArgs>? ResolutionRequested;

    /// <summary>
    /// 留在本面板（“待处理交互”区）的条目数；已放入时间线提问锚点的条目不计入（页面据此显示区头与可见性）。
    /// </summary>
    internal int Count
    {
        get
        {
            var count = 0;
            foreach (var entry in _entries)
            {
                if (entry.AnchorHost is null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// 全部提问条目（按服务端顺序；含已放入时间线锚点的）。提问遮罩据此同步（重设计 wave3 F9）。
    /// </summary>
    internal IReadOnlyList<OpenCodeFormCard> FormCards
    {
        get
        {
            var list = new List<OpenCodeFormCard>();
            foreach (var entry in _entries)
            {
                if (entry.FormCard is { } card)
                {
                    list.Add(card);
                }
            }

            return list;
        }
    }

    /// <summary>
    /// 按 Form metadata.tool 的 messageID / id 查找时间线中 question 工具锚点（由页面接到会话视图）；
    /// 为 null 或查不到时条目留在本面板。
    /// </summary>
    internal Func<string, string, OpenCodeQuestionAnchorHost?>? AnchorResolver { get; set; }

    /// <summary>时间线锚点变化（会话视图重渲染 / 分页 / 清空）后重新放置提问条目。</summary>
    internal void RefreshPlacement()
    {
        PlaceForms(_entries);
        AlignChildren(_entries);
    }

    /// <summary>
    /// 从 Form metadata 读取 question 锚点：仅当 kind == "question" 且 tool.messageID / tool.id 均为非空字符串。
    /// </summary>
    internal static bool TryReadQuestionAnchor(OpenCodeFormInfo? form, out string messageId, out string partId)
    {
        messageId = string.Empty;
        partId = string.Empty;
        if (form?.Metadata is not { ValueKind: JsonValueKind.Object } metadata
            || !metadata.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String
            || !string.Equals(kind.GetString(), "question", StringComparison.Ordinal)
            || !metadata.TryGetProperty("tool", out var tool)
            || tool.ValueKind != JsonValueKind.Object
            || !tool.TryGetProperty("messageID", out var message)
            || message.ValueKind != JsonValueKind.String
            || !tool.TryGetProperty("id", out var part)
            || part.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        messageId = message.GetString() ?? string.Empty;
        partId = part.GetString() ?? string.Empty;
        return messageId.Length > 0 && partId.Length > 0;
    }

    private void PlaceForms(List<CardEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.FormCard is not { } card)
            {
                continue;
            }

            OpenCodeQuestionAnchorHost? target = null;
            if (AnchorResolver is { } resolver && TryReadQuestionAnchor(card.Form, out var messageId, out var partId))
            {
                try
                {
                    target = resolver(messageId, partId);
                }
                catch (Exception)
                {
                    target = null;
                }
            }

            if (ReferenceEquals(target, entry.AnchorHost))
            {
                continue;
            }

            // 先离开旧父级（锚点或本面板），再放入新位置。
            if (entry.AnchorHost is { } old)
            {
                old.Detach(entry.Element);
                entry.AnchorHost = null;
            }
            else
            {
                Children.Remove(entry.Element);
            }

            if (target is not null)
            {
                target.Attach(entry.Element);
                entry.AnchorHost = target;
            }
        }
    }

    /// <summary>把条目从所在位置（锚点或本面板）移除，并通知打开中的回答对话框“已被处理”。</summary>
    private void DetachEntry(CardEntry entry)
    {
        if (entry.AnchorHost is { } host)
        {
            host.Detach(entry.Element);
            entry.AnchorHost = null;
        }
        else
        {
            Children.Remove(entry.Element);
        }

        entry.FormCard?.MarkDetached();
    }

    /// <summary>
    /// 应用一次待处理快照；null 表示该类本次未成功获取（保留现有卡片，不推断“无等待”）。
    /// 返回“外部消失”条目（非本页操作导致），供页面提示。
    /// </summary>
    internal IReadOnlyList<OpenCodeInteractionsRemovedItem> Apply(
        IReadOnlyList<OpenCodePermissionRequest>? permissions,
        IReadOnlyList<OpenCodeFormInfo>? forms)
    {
        var newEntries = new List<CardEntry>();

        if (permissions is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in permissions)
            {
                if (permission is null)
                {
                    continue;
                }

                var id = permission.Id ?? string.Empty;
                if (id.Length == 0 || !seen.Add(id))
                {
                    // 契约保证 id；缺失/重复条目跳过，不建卡（客户端校验已在获取时把关）。
                    continue;
                }

                var key = "p:" + id;
                if (_suppressed.Contains(key))
                {
                    continue;
                }

                var fingerprint = OpenCodeInteractionFingerprint.ForPermission(permission);
                if (_byKey.TryGetValue(key, out var existing) && existing.PermissionCard is { } card)
                {
                    if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        card.Update(permission);
                        existing.Fingerprint = fingerprint;
                    }

                    newEntries.Add(existing);
                }
                else
                {
                    var created = new OpenCodePermissionCard(
                        permission,
                        (request, decision) => PermissionDecisionRequested?.Invoke(this, new(request, decision)),
                        request => PermissionRecheckRequested?.Invoke(this, new(request.Id ?? string.Empty)),
                        (request, retry) => ResolutionRequested?.Invoke(this, new(false, request.Id ?? string.Empty, retry)));
                    var entry = new CardEntry
                    {
                        Key = key,
                        IsForm = false,
                        Id = id,
                        Title = string.IsNullOrWhiteSpace(permission.Action) ? id : permission.Action!,
                        Element = created,
                        PermissionCard = created,
                        Fingerprint = fingerprint,
                    };
                    _byKey[key] = entry;
                    newEntries.Add(entry);
                }
            }
        }
        else
        {
            foreach (var entry in _entries)
            {
                if (!entry.IsForm)
                {
                    newEntries.Add(entry);
                }
            }
        }

        if (forms is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var form in forms)
            {
                if (form is null)
                {
                    continue;
                }

                var id = form.Id ?? string.Empty;
                if (id.Length == 0 || !seen.Add(id))
                {
                    continue;
                }

                var key = "f:" + id;
                if (_suppressed.Contains(key))
                {
                    continue;
                }

                var fingerprint = OpenCodeInteractionFingerprint.ForForm(form);
                if (_byKey.TryGetValue(key, out var existing) && existing.FormCard is { } card)
                {
                    if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        card.Update(form);
                        existing.Fingerprint = fingerprint;
                    }

                    newEntries.Add(existing);
                }
                else
                {
                    var created = new OpenCodeFormCard(
                        form,
                        (source, answer) => FormSubmitRequested?.Invoke(this, new(source, answer)),
                        source => FormCancelRequested?.Invoke(this, new(source)),
                        source => FormRecheckRequested?.Invoke(this, new(source.Id ?? string.Empty)),
                        (source, retry) => ResolutionRequested?.Invoke(this, new(true, source.Id ?? string.Empty, retry)));
                    // “回答”由提问遮罩订阅（重设计 wave3 F9，不再打开 ContentDialog）；提交仍走上面的既有回调（页面重新查询 / 比对指纹）。
                    var entry = new CardEntry
                    {
                        Key = key,
                        IsForm = true,
                        Id = id,
                        Title = string.IsNullOrWhiteSpace(form.Title) ? id : form.Title!,
                        Element = created,
                        FormCard = created,
                        Fingerprint = fingerprint,
                    };
                    _byKey[key] = entry;
                    newEntries.Add(entry);
                }
            }
        }
        else
        {
            foreach (var entry in _entries)
            {
                if (entry.IsForm)
                {
                    newEntries.Add(entry);
                }
            }
        }

        var removed = new List<OpenCodeInteractionsRemovedItem>();
        var retained = new HashSet<CardEntry>(newEntries);
        foreach (var entry in _entries)
        {
            if (retained.Contains(entry))
            {
                continue;
            }

            DetachEntry(entry);
            _byKey.Remove(entry.Key);
            if (!_settled.Remove(entry.Key))
            {
                removed.Add(new OpenCodeInteractionsRemovedItem(entry.IsForm, entry.Id, entry.Title));
            }
        }

        PruneSuppressed(permissions, forms);
        PlaceForms(newEntries);
        AlignChildren(newEntries);
        _entries.Clear();
        _entries.AddRange(newEntries);
        return removed;
    }

    /// <summary>清空全部卡片与内部状态（切换会话 / 修改后端 / 页面卸载时调用）。</summary>
    internal void ClearAll()
    {
        foreach (var entry in _entries)
        {
            DetachEntry(entry);
        }

        Children.Clear();
        _entries.Clear();
        _byKey.Clear();
        _settled.Clear();
        _suppressed.Clear();
    }

    /// <summary>移除一个由本页操作结算的条目；其后续消失不再作为“外部变化”提示。</summary>
    internal void RemoveSettled(bool isForm, string id)
    {
        var key = (isForm ? "f:" : "p:") + id;
        if (_byKey.Remove(key, out var entry))
        {
            DetachEntry(entry);
            _entries.Remove(entry);
        }

        _settled.Add(key);
    }

    /// <summary>
    /// 抑制一个条目（共享保护判定已接受/已结算，或用户选择视为已结算）：立即移除卡片，
    /// 且在服务端仍返回该 pending 条目期间不再重建，直至其真正消失。绝不发送任何请求。
    /// </summary>
    internal void Suppress(bool isForm, string id)
    {
        var key = (isForm ? "f:" : "p:") + id;
        _suppressed.Add(key);
        if (_byKey.Remove(key, out var entry))
        {
            DetachEntry(entry);
            _entries.Remove(entry);
        }
    }

    /// <summary>
    /// 把指定卡片滚动到可视区并把焦点移到卡片自身（不聚焦任何会触发决定/回答的按钮）。
    /// 返回 false 表示当前面板中不存在该卡片（调用方据此给出明确状态，绝不虚构成功）。
    /// </summary>
    internal bool TryFocusCard(bool isForm, string id)
    {
        if (string.IsNullOrEmpty(id) || !_byKey.TryGetValue((isForm ? "f:" : "p:") + id, out var entry))
        {
            return false;
        }

        try
        {
            entry.Element.StartBringIntoView(new BringIntoViewOptions());
        }
        catch
        {
            // 滚动失败不影响可用性；仍尝试聚焦。
        }

        if (entry.PermissionCard is { } permissionCard)
        {
            permissionCard.FocusCard();
        }
        else if (entry.FormCard is { } formCard)
        {
            formCard.FocusCard();
        }

        return true;
    }

    /// <summary>用刚查询到的权限快照替换卡片（指纹变化或重新查询成功时调用）。</summary>
    internal void ReplacePermission(OpenCodePermissionRequest fresh)
    {
        if (fresh is null)
        {
            return;
        }

        var id = fresh.Id ?? string.Empty;
        if (id.Length == 0)
        {
            return;
        }

        if (_byKey.TryGetValue("p:" + id, out var entry) && entry.PermissionCard is { } card)
        {
            card.Update(fresh);
            entry.Fingerprint = OpenCodeInteractionFingerprint.ForPermission(fresh);
        }
    }

    /// <summary>用刚查询到的表单快照重建卡片（指纹变化或重新查询成功时调用）。</summary>
    internal void ReplaceForm(OpenCodeFormInfo fresh)
    {
        if (fresh is null)
        {
            return;
        }

        var id = fresh.Id ?? string.Empty;
        if (id.Length == 0)
        {
            return;
        }

        if (_byKey.TryGetValue("f:" + id, out var entry) && entry.FormCard is { } card)
        {
            card.Update(fresh);
            entry.Fingerprint = OpenCodeInteractionFingerprint.ForForm(fresh);
        }
    }

    /// <summary>
    /// 重新查询成功后的权限快照应用：内容未变化时仅解除阻断（保留确认状态），变化时重建。
    /// 返回 true 表示内容已变化（页面据此提示“范围已变化”）。
    /// </summary>
    internal bool ApplyRecheckedPermission(OpenCodePermissionRequest fresh)
    {
        if (fresh is null)
        {
            return false;
        }

        var id = fresh.Id ?? string.Empty;
        if (id.Length == 0 || !_byKey.TryGetValue("p:" + id, out var entry))
        {
            return false;
        }

        var card = entry.PermissionCard;
        if (card is null)
        {
            return false;
        }

        var fingerprint = OpenCodeInteractionFingerprint.ForPermission(fresh);
        if (string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            card.Unblock();
            return false;
        }

        card.Update(fresh);
        entry.Fingerprint = fingerprint;
        return true;
    }

    /// <summary>
    /// 重新查询成功后的表单快照应用：内容未变化时仅解除阻断（保留已输入内容），变化时重建并提示。
    /// 返回 true 表示内容已变化（页面据此提示“已重置输入”）。
    /// </summary>
    internal bool ApplyRecheckedForm(OpenCodeFormInfo fresh)
    {
        if (fresh is null)
        {
            return false;
        }

        var id = fresh.Id ?? string.Empty;
        if (id.Length == 0 || !_byKey.TryGetValue("f:" + id, out var entry))
        {
            return false;
        }

        var card = entry.FormCard;
        if (card is null)
        {
            return false;
        }

        var fingerprint = OpenCodeInteractionFingerprint.ForForm(fresh);
        if (string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            card.Unblock();
            return false;
        }

        card.Update(fresh);
        entry.Fingerprint = fingerprint;
        return true;
    }

    /// <summary>把指定权限卡片置为阻断态（结果未知），只保留“重新查询状态”。</summary>
    internal void MarkPermissionBlocked(string id, string detail)
    {
        if (_byKey.TryGetValue("p:" + id, out var entry))
        {
            entry.PermissionCard?.MarkBlocked(detail);
        }
    }

    /// <summary>把指定表单卡片置为阻断态（结果未知），只保留“重新查询状态”。</summary>
    internal void MarkFormBlocked(string id, string detail)
    {
        if (_byKey.TryGetValue("f:" + id, out var entry))
        {
            entry.FormCard?.MarkBlocked(detail);
        }
    }

    /// <summary>
    /// 在指定卡片内展示阻断态“必要确认”（仍要重新决定 / 视为已结算）；卡片当前不存在时返回 false。
    /// 仅展示确认入口，不发送任何请求。
    /// </summary>
    internal bool ShowResolutionConfirm(bool isForm, string id, string detail)
    {
        if (string.IsNullOrEmpty(id) || !_byKey.TryGetValue((isForm ? "f:" : "p:") + id, out var entry))
        {
            return false;
        }

        if (entry.PermissionCard is { } permissionCard)
        {
            permissionCard.ShowResolutionConfirm(detail);
            return true;
        }

        if (entry.FormCard is { } formCard)
        {
            formCard.ShowResolutionConfirm(detail);
            return true;
        }

        return false;
    }

    /// <summary>拒绝操作发起后，保守作废同会话其他权限卡片的操作入口（即使结果未知）。</summary>
    internal void InvalidateOtherPermissions(string exceptId, string detail)
    {
        foreach (var entry in _entries)
        {
            if (!entry.IsForm && !string.Equals(entry.Id, exceptId, StringComparison.Ordinal))
            {
                entry.PermissionCard?.MarkBlocked(detail);
            }
        }
    }

    /// <summary>拒绝操作发起后，其他权限条目的消失视为预期连带结果，不再单独提示。</summary>
    internal void MarkOtherPermissionsSettled(string exceptId)
    {
        foreach (var entry in _entries)
        {
            if (!entry.IsForm && !string.Equals(entry.Id, exceptId, StringComparison.Ordinal))
            {
                _settled.Add(entry.Key);
            }
        }
    }

    /// <summary>
    /// 提交/查询进行中：禁用全部卡片按钮并让目标卡片显示进度；结束后恢复（阻断态保持阻断）。
    /// </summary>
    internal void SetSubmitting(bool isForm, string id, bool busy, string label)
    {
        foreach (var entry in _entries)
        {
            entry.PermissionCard?.SetActionsEnabled(!busy);
            entry.FormCard?.SetActionsEnabled(!busy);
        }

        if (!_byKey.TryGetValue((isForm ? "f:" : "p:") + id, out var active))
        {
            return;
        }

        if (active.PermissionCard is { } permissionCard)
        {
            permissionCard.SetBusy(busy, busy ? label : null);
        }
        else if (active.FormCard is { } formCard)
        {
            formCard.SetBusy(busy, busy ? label : null);
        }
    }

    /// <summary>
    /// 清理抑制集：某条目在服务端已不再 pending（该类本次成功获取且不含该 id）时，
    /// 解除其抑制，使未来同 id 的重新出现（若发生）可再次建卡。
    /// </summary>
    private void PruneSuppressed(
        IReadOnlyList<OpenCodePermissionRequest>? permissions,
        IReadOnlyList<OpenCodeFormInfo>? forms)
    {
        if (_suppressed.Count == 0)
        {
            return;
        }

        if (permissions is not null)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in permissions)
            {
                if (permission?.Id is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            _suppressed.RemoveWhere(key => key.Length > 2 && key[0] == 'p' && key[1] == ':' && !ids.Contains(key[2..]));
        }

        if (forms is not null)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var form in forms)
            {
                if (form?.Id is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            _suppressed.RemoveWhere(key => key.Length > 2 && key[0] == 'f' && key[1] == ':' && !ids.Contains(key[2..]));
        }
    }

    private void AlignChildren(List<CardEntry> newEntries)
    {
        // 只对齐留在本面板的条目；已放入时间线锚点的条目不在本面板中。
        var local = new List<CardEntry>(newEntries.Count);
        foreach (var entry in newEntries)
        {
            if (entry.AnchorHost is null)
            {
                local.Add(entry);
            }
        }

        for (var i = 0; i < local.Count; i++)
        {
            var element = local[i].Element;
            if (i < Children.Count && ReferenceEquals(Children[i], element))
            {
                continue;
            }

            Children.Remove(element);
            Children.Insert(Math.Min(i, Children.Count), element);
        }

        while (Children.Count > local.Count)
        {
            Children.RemoveAt(Children.Count - 1);
        }
    }
}
