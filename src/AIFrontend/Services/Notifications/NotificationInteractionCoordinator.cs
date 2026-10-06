using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIFrontend.Services.OpenCode;

namespace AIFrontend.Services.Notifications;

internal sealed record NotificationInteractionButton(string Label, string Nonce);
internal sealed record NotificationInteractionPlan(string BodyNonce, string Title, string Body,
    IReadOnlyList<NotificationInput> Inputs, IReadOnlyList<NotificationInteractionButton> Buttons);
internal sealed record NotificationPreparation(NotificationInteractionResult Result, NotificationInteractionPlan? Plan = null);

/// <summary>No Windows APIs, registration, polling, persisted secrets, UI or untrusted endpoint resolution.</summary>
internal sealed class NotificationInteractionCoordinator : IDisposable
{
    internal const string NonceKey = "nonce";
    internal const int Capacity = 256;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, OpenCodeClient> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<OpenCodeClient> _revokedClients = new();
    private readonly OpenCodeInteractionGate _interactionGate;
    private readonly Dictionary<(string Source, string Session, string Request, bool Form), Entry> _entries = new();
    private readonly Dictionary<string, (Entry Entry, ActionKind Action)> _tokens = new(StringComparer.Ordinal);
    private bool _disposed;

    internal NotificationInteractionCoordinator(TimeProvider? timeProvider = null, OpenCodeInteractionGate? interactionGate = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _interactionGate = interactionGate ?? OpenCodeInteractionGate.Shared;
    }

    private enum ActionKind { Navigate, Once, Always, Reject, Answer }
    private enum EntryState { Available, Claimed, Retired }
    private sealed class Entry(TrustedOpenCodeNotificationSource source, string session, string request,
        bool form, string fingerprint, NotificationField[] fields, DateTimeOffset expires)
    {
        internal readonly TrustedOpenCodeNotificationSource Source = source;
        internal readonly string Session = session;
        internal readonly string Request = request;
        internal readonly bool Form = form;
        internal readonly string Fingerprint = fingerprint;
        internal readonly NotificationField[] Fields = fields;
        internal readonly DateTimeOffset Expires = expires;
        internal EntryState State;
        internal NotificationInteractionStatus FinalStatus = NotificationInteractionStatus.Duplicate;
    }

    internal async Task<NotificationPreparation> PrepareAsync(TrustedOpenCodeNotificationSource source,
        string sessionId, string requestId, bool form, CancellationToken cancellationToken)
    {
        if (source is null || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(requestId))
            return new(new(NotificationInteractionStatus.Failed, "无效的受信来源或请求标识。"));
        lock (_gate)
        {
            if (_disposed) return new(new(NotificationInteractionStatus.Unavailable, "通知服务已关闭。"));
            if (_revokedClients.Contains(source.Client)) return new(new(NotificationInteractionStatus.Expired, "来源已撤销。"));
            if (_sources.TryGetValue(source.Identity, out var bound) && !ReferenceEquals(bound, source.Client))
                return new(new(NotificationInteractionStatus.NeedsApp, "来源绑定已改变，请在应用重新验证。"));
            if (!_sources.ContainsKey(source.Identity) && _sources.Count >= Capacity)
                return new(new(NotificationInteractionStatus.NeedsApp, "运行期来源容量已满。"));
            _sources[source.Identity] = source.Client;
            if (_entries.TryGetValue((source.Identity, sessionId, requestId, form), out var old))
                return new(Result(old, ExistingStatus(old), "此请求已存在；不自动重发交互通知。"));
            if (_entries.Count >= Capacity)
                return new(new(NotificationInteractionStatus.NeedsApp, "运行期交互容量已满，请在应用处理。"));
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            string fingerprint;
            string title;
            string body;
            NotificationInput[] inputs = [];
            NotificationField[] fields = [];
            bool navigateOnly = false;
            if (form)
            {
                var pending = await source.Client.GetFormAsync(sessionId, requestId, linked.Token).ConfigureAwait(false);
                if (pending.State?.Status != OpenCodeFormStatuses.Pending)
                    return new(new(NotificationInteractionStatus.Expired, "表单已结算。", source.Identity, sessionId, requestId));
                if (!NotificationFormPolicy.TryCreate(pending, out inputs, out fields))
                {
                    navigateOnly = true;
                    inputs = [];
                    fields = [];
                }
                fingerprint = Fingerprint(pending);
                title = "OpenCode · " + pending.Title;
                body = "请填写后点击提交。有选项且允许自定义时，非空自定义文本优先。";
            }
            else
            {
                var pending = await source.Client.GetPendingPermissionAsync(sessionId, requestId, linked.Token).ConfigureAwait(false);
                fingerprint = Fingerprint(pending);
                title = "OpenCode · 权限请求";
                body = $"动作：{pending.Action}\n资源：{string.Join(", ", pending.Resources!)}\nsave patterns：{string.Join(", ", pending.Save ?? [])}\n始终允许仅保存上述 patterns 对应的项目规则；拒绝还影响本会话其他待处理权限。";
                // Never truncate a permission scope into a deceptively narrower grant.
                if (body.Length > 1000 || body.Any(c => char.IsControl(c) && c != '\n')) navigateOnly = true;
            }
            if (navigateOnly) { title = "OpenCode · 待处理请求"; body = "请打开应用查看完整请求并处理；此通知不提交任何决定。"; }
            lock (_gate)
            {
                if (_disposed || linked.IsCancellationRequested || _revokedClients.Contains(source.Client))
                    return new(new(NotificationInteractionStatus.Cancelled, "已取消准备通知。"));
                var key = (source.Identity, sessionId, requestId, form);
                if (_entries.TryGetValue(key, out var old))
                    return new(Result(old, ExistingStatus(old), "此请求已存在。"));
                if (_entries.Count >= Capacity)
                    return new(new(NotificationInteractionStatus.NeedsApp, "运行期交互容量已满。"));
                var entry = new Entry(source, sessionId, requestId, form, fingerprint, fields, _time.GetUtcNow() + Lifetime);
                _entries.Add(key, entry);
                string bodyNonce = Token(entry, ActionKind.Navigate);
                NotificationInteractionButton[] buttons = navigateOnly ? [new("打开应用", bodyNonce)] : form
                    ? [new("提交", Token(entry, ActionKind.Answer))]
                    : [new("允许一次", Token(entry, ActionKind.Once)), new("始终允许", Token(entry, ActionKind.Always)),
                        new("拒绝", Token(entry, ActionKind.Reject))];
                return new(Result(entry, navigateOnly ? NotificationInteractionStatus.NeedsApp : NotificationInteractionStatus.Prepared,
                    navigateOnly ? "已准备仅导航降级通知；不提交答案或权限。" : "交互已准备；尚未提交通知。"),
                    new(bodyNonce, title, body, inputs, buttons));
            }
        }
        catch (OpenCodeApiException ex)
        {
            return new(new(ex.StatusCode is 404 or 409 ? NotificationInteractionStatus.Expired : NotificationInteractionStatus.Failed,
                "读取待处理请求失败，请在应用重新验证。", source.Identity, sessionId, requestId, ex.StatusCode));
        }
        catch (OperationCanceledException)
        {
            return new(new(NotificationInteractionStatus.Cancelled, "读取已取消或超时。", source.Identity, sessionId, requestId));
        }
        catch
        {
            return new(new(NotificationInteractionStatus.Failed, "无法准备交互通知。", source.Identity, sessionId, requestId));
        }
    }

    internal async Task<NotificationInteractionResult> ActivateAsync(IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> userInput, CancellationToken cancellationToken)
    {
        Entry entry;
        ActionKind action;
        Dictionary<string, JsonElement>? answer = null;
        lock (_gate)
        {
            if (_disposed) return new(NotificationInteractionStatus.Unavailable, "通知服务已关闭。请在应用重新验证。");
            if (arguments is null || arguments.Count != 1 || !arguments.TryGetValue(NonceKey, out var nonce)
                || nonce is null || nonce.Length != 64 || !_tokens.TryGetValue(nonce, out var binding))
                return new(NotificationInteractionStatus.NeedsApp, "未知或冷启动通知。请打开应用重新验证；未执行授权。");
            entry = binding.Entry;
            action = binding.Action;
            if (entry.Expires <= _time.GetUtcNow() || entry.State == EntryState.Retired)
                return Result(entry, ExistingStatus(entry), "请求已过期、已处理或结果未知；请在应用重新验证。");
            if (action == ActionKind.Navigate)
                return Result(entry, NotificationInteractionStatus.Navigate, "仅请求导航，不执行授权或回答。");
            if (entry.State != EntryState.Available)
                return Result(entry, NotificationInteractionStatus.Duplicate, "同一请求正在处理，已拒绝重复点击。");
            if (userInput is null || (entry.Form
                ? !NotificationFormPolicy.TryAnswer(entry.Fields, userInput, out answer)
                : userInput.Count != 0))
                return Result(entry, NotificationInteractionStatus.InvalidInput, "请填写有效答案；未发送请求。");
            // One claim covers ALL action nonces of this request, not merely the clicked button.
            entry.State = EntryState.Claimed;
        }

        bool mutationStarted = false;
        OpenCodeInteractionGate.Lease? lease = null;
        NotificationInteractionResult result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var client = entry.Source.Client; // The original trusted instance, never a URL from activation.
            lease = await _interactionGate.EnterAsync(entry.Source.Identity, entry.Session, linked.Token).ConfigureAwait(false);
            var kind = entry.Form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission;
            if (lease.GetProtectionStatus(entry.Request, kind) is { } protection)
                result = Result(entry, protection, "共享请求保护生效；请显式重新验证，未提交。");
            else if (entry.Form)
            {
                var fresh = await client.GetFormAsync(entry.Session, entry.Request, linked.Token).ConfigureAwait(false);
                if (fresh.State?.Status != OpenCodeFormStatuses.Pending)
                    result = Result(entry, NotificationInteractionStatus.Expired, "表单已结算。");
                else if (Fingerprint(fresh) != entry.Fingerprint)
                    result = Result(entry, NotificationInteractionStatus.NeedsApp, "表单字段或范围已变化，请在应用重新验证。");
                else
                {
                    CheckBeforeMutation(entry, linked.Token);
                    lease.BeginMutation(entry.Request, kind);
                    mutationStarted = true;
                    await client.ReplyFormAsync(entry.Session, fresh, answer!, linked.Token).ConfigureAwait(false);
                    result = Result(entry, NotificationInteractionStatus.Succeeded, "服务器已接受回答（HTTP 204）。");
                }
            }
            else
            {
                var fresh = await client.GetPendingPermissionAsync(entry.Session, entry.Request, linked.Token).ConfigureAwait(false);
                if (Fingerprint(fresh) != entry.Fingerprint)
                    result = Result(entry, NotificationInteractionStatus.NeedsApp, "权限范围已变化，请在应用重新验证。");
                else
                {
                    string decision = action switch
                    {
                        ActionKind.Once => OpenCodePermissionDecisions.Once,
                        ActionKind.Always => OpenCodePermissionDecisions.Always,
                        ActionKind.Reject => OpenCodePermissionDecisions.Reject,
                        _ => throw new InvalidOperationException()
                    };
                    CheckBeforeMutation(entry, linked.Token);
                    lease.BeginMutation(entry.Request, kind, action == ActionKind.Reject);
                    mutationStarted = true;
                    await client.ReplyPermissionAsync(entry.Session, fresh, decision, cancellationToken: linked.Token).ConfigureAwait(false);
                    result = Result(entry, NotificationInteractionStatus.Succeeded, "服务器已接受权限决定（HTTP 204）。");
                }
            }
        }
        catch (OpenCodeApiException ex)
        {
            var status = ex.StatusCode is 404 or 409 ? NotificationInteractionStatus.Expired
                : mutationStarted && ex.StatusCode is not (400 or 401 or 403)
                    ? NotificationInteractionStatus.OutcomeUnknown : NotificationInteractionStatus.Failed;
            result = Result(entry, status, "服务器未明确确认成功；不自动重发，请在应用重新验证。", ex.StatusCode);
        }
        catch (OperationCanceledException)
        {
            result = Result(entry, mutationStarted ? NotificationInteractionStatus.OutcomeUnknown : NotificationInteractionStatus.Cancelled,
                mutationStarted ? "提交已取消或超时，结果未知；禁止自动重发。" : "提交前读取已取消或超时，未发送决定。");
        }
        catch
        {
            result = Result(entry, mutationStarted ? NotificationInteractionStatus.OutcomeUnknown : NotificationInteractionStatus.Failed,
                "交互处理失败；不自动重发，请在应用重新验证。");
        }
        lock (_gate)
        {
            entry.State = EntryState.Retired;
            entry.FinalStatus = result.Status == NotificationInteractionStatus.Succeeded
                ? NotificationInteractionStatus.Duplicate : result.Status;
            // reject can settle other pending permissions, even when its network outcome is uncertain.
            if (action == ActionKind.Reject && mutationStarted)
                foreach (var other in _entries.Values.Where(e => !e.Form && e.Source.Identity == entry.Source.Identity
                    && e.Session == entry.Session && !ReferenceEquals(e, entry)))
                {
                    other.State = EntryState.Retired;
                    other.FinalStatus = NotificationInteractionStatus.Expired;
                }
        }
        if (mutationStarted) lease!.CompleteMutation(result.Status);
        lease?.Dispose();
        return result;
    }

    internal void Retire(NotificationInteractionPlan plan)
    {
        lock (_gate)
            if (_tokens.TryGetValue(plan.BodyNonce, out var binding))
            {
                binding.Entry.State = EntryState.Retired;
                binding.Entry.FinalStatus = NotificationInteractionStatus.NeedsApp;
            }
    }

    internal bool IsEligible(NotificationInteractionPlan plan)
    {
        lock (_gate) return !_disposed && _tokens.TryGetValue(plan.BodyNonce, out var binding)
            && binding.Entry.State == EntryState.Available && binding.Entry.Expires > _time.GetUtcNow();
    }

    private void CheckBeforeMutation(Entry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
            if (_disposed || entry.State != EntryState.Claimed || entry.Expires <= _time.GetUtcNow())
                throw new OperationCanceledException(token);
    }

    private string Token(Entry entry, ActionKind action)
    {
        string nonce;
        do { nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); } while (_tokens.ContainsKey(nonce));
        _tokens.Add(nonce, (entry, action));
        return nonce;
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private NotificationInteractionStatus ExistingStatus(Entry entry) => entry.State == EntryState.Retired
        ? entry.FinalStatus : entry.Expires <= _time.GetUtcNow() ? NotificationInteractionStatus.Expired : NotificationInteractionStatus.Duplicate;
    private static NotificationInteractionResult Result(Entry entry, NotificationInteractionStatus status, string detail, int? httpStatus = null)
        => new(status, detail, entry.Source.Identity, entry.Session, entry.Request, httpStatus,
            entry.Form ? NotificationNavigationKind.Form : NotificationNavigationKind.Permission);

    internal void InvalidateOpenCodeSource(string endpointIdentity)
    {
        lock (_gate)
        {
            if (_sources.Remove(endpointIdentity, out var client)) _revokedClients.Add(client);
            foreach (var entry in _entries.Values.Where(e => e.Source.Identity == endpointIdentity))
            {
                entry.State = EntryState.Retired;
                if (entry.FinalStatus != NotificationInteractionStatus.OutcomeUnknown)
                    entry.FinalStatus = NotificationInteractionStatus.Expired;
            }
            // Gate tombstones deliberately survive invalidation, client replacement and background polling.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _tokens.Clear();
            _entries.Clear();
            _sources.Clear();
        }
        // Do not Dispose the CTS while in-flight tasks may still link its token.
        try { _shutdown.Cancel(); } catch { /* Cancellation callbacks cannot escape disposal. */ }
    }
}
