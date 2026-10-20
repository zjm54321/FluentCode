using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AIFrontend.Services.OpenCode;

public sealed partial class OpenCodeClient
{
    /// <summary>读取所属 session 的 pending permission；404 表示不存在或已处理，不转换成成功。</summary>
    public async Task<OpenCodePermissionRequest> GetPendingPermissionAsync(
        string sessionId, string requestId, CancellationToken cancellationToken = default)
    {
        var path = InteractionPath(sessionId, "permission", requestId);
        const string label = "GET api/session/{sessionID}/permission/{requestID}";
        using var document = await SendForJsonAsync(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
        var pending = RequireData<OpenCodePermissionRequest>(document, label);
        ValidatePermission(pending, sessionId);
        if (pending.Id != requestId) throw InvalidInteractionResponse();
        return pending;
    }

    /// <summary>
    /// 回复上层刚重新查询的 pending 快照；只执行一次 POST，只有 204 为成功。
    /// Always 仅保存 save patterns 对应项目规则；Reject 还会拒绝本 session 的其他 pending。
    /// </summary>
    public async Task ReplyPermissionAsync(
        string sessionId, OpenCodePermissionRequest pendingRequest, string decision,
        string? message = null, CancellationToken cancellationToken = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(pendingRequest);
        ValidatePermission(pendingRequest, sessionId);
        if (decision is not (OpenCodePermissionDecisions.Once or OpenCodePermissionDecisions.Always or OpenCodePermissionDecisions.Reject))
            throw new ArgumentException("decision 必须为 once、always 或 reject。", nameof(decision));
        var path = InteractionPath(sessionId, "permission", pendingRequest.Id!) + "/reply";
        using var content = CreateJsonContent(new PermissionReplyBody(decision, message));
        await SendForNoContentAsync(HttpMethod.Post, path, null, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取 pending Form.Info 列表；不是 V1 Question API，不从工具 input 推断 Ask。</summary>
    public async Task<IReadOnlyList<OpenCodeFormInfo>> GetPendingFormsAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        var path = "api/session/" + Uri.EscapeDataString(sessionId) + "/form";
        using var document = await SendForJsonAsync(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
        var response = Deserialize<OpenCodeFormListResponse>(document, "GET api/session/{sessionID}/form");
        var forms = response.Data ?? throw InvalidInteractionResponse();
        foreach (var form in forms) ValidateForm(form, sessionId);
        return forms;
    }

    /// <summary>读取 Form.Detail 和显式 pending/answered/cancelled 状态；不把已 settled 的记录当 pending。</summary>
    public async Task<OpenCodeFormDetail> GetFormAsync(
        string sessionId, string formId, CancellationToken cancellationToken = default)
    {
        var path = InteractionPath(sessionId, "form", formId);
        using var document = await SendForJsonAsync(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
        var form = RequireData<OpenCodeFormDetail>(document, "GET api/session/{sessionID}/form/{formID}");
        ValidateFormDetail(form, sessionId);
        if (form.Id != formId) throw InvalidInteractionResponse();
        return form;
    }

    /// <summary>
    /// 回复上层刚重新查询的 pending Form.Detail；拒绝本地非 pending 快照与非法 answer 值。
    /// 字段 required/when/选项/数值边界等业务校验由服务器处理（400），竞争 settled 返回 409。
    /// </summary>
    public async Task ReplyFormAsync(
        string sessionId, OpenCodeFormDetail pendingForm, IReadOnlyDictionary<string, JsonElement> answer,
        CancellationToken cancellationToken = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(pendingForm);
        RequirePendingForm(pendingForm, sessionId);
        ArgumentNullException.ThrowIfNull(answer);
        var snapshot = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in answer)
        {
            if (pair.Key is null || !IsFormValue(pair.Value))
                throw new ArgumentException("answer 的每个值必须是 string、number、bool 或 string[]。", nameof(answer));
            snapshot.Add(pair.Key, pair.Value.Clone());
        }
        var path = InteractionPath(sessionId, "form", pendingForm.Id!) + "/reply";
        using var content = CreateJsonContent(new FormReplyBody(snapshot));
        await SendForNoContentAsync(HttpMethod.Post, path, null, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取消上层刚重新查询的 pending Form.Detail；只执行一次 DELETE，不自动重试。</summary>
    public async Task CancelFormAsync(
        string sessionId, OpenCodeFormDetail pendingForm, string? message = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(pendingForm);
        RequirePendingForm(pendingForm, sessionId);
        var path = InteractionPath(sessionId, "form", pendingForm.Id!);
        var query = message is null ? null : new[] { new KeyValuePair<string, string>("message", message) };
        await SendForNoContentAsync(HttpMethod.Delete, path, query, null, cancellationToken).ConfigureAwait(false);
    }

    private static string InteractionPath(string sessionId, string resource, string id)
    {
        ValidateInteractionId(sessionId, "ses", nameof(sessionId));
        ValidateInteractionId(id, resource == "permission" ? "per" : "frm_", nameof(id));
        return "api/session/" + Uri.EscapeDataString(sessionId) + "/" + resource + "/" + Uri.EscapeDataString(id);
    }

    private static void ValidateInteractionId(string id, string prefix, string parameter)
    {
        if (!IsInteractionId(id, prefix))
            throw new ArgumentException("交互 ID 必须具有正确前缀和非空后缀，且不能包含空白、控制字符或路径分隔符。", parameter);
    }

    private static bool IsInteractionId(string? id, string prefix)
    {
        if (id is null || id.Length <= prefix.Length || !id.StartsWith(prefix, StringComparison.Ordinal)) return false;
        foreach (var character in id)
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\' or '?' or '#') return false;
        return true;
    }

    private static void ValidatePermission(OpenCodePermissionRequest? pending, string sessionId)
    {
        if (pending is null || !IsInteractionId(pending.Id, "per") || pending.SessionId != sessionId
            || pending.Action is null || pending.Resources is null) throw InvalidInteractionResponse();
        foreach (var resource in pending.Resources)
            if (resource is null) throw InvalidInteractionResponse();
    }

    private static void ValidateForm(OpenCodeFormInfo? form, string sessionId)
    {
        if (form is null || !IsInteractionId(form.Id, "frm_") || form.SessionId != sessionId
            || form.Title is null || form.Fields is not { Count: > 0 }) throw InvalidInteractionResponse();
        foreach (var field in form.Fields)
        {
            if (field?.Key is null || field.Type is not (OpenCodeFormFieldTypes.String or OpenCodeFormFieldTypes.Number
                or OpenCodeFormFieldTypes.Integer or OpenCodeFormFieldTypes.Boolean or OpenCodeFormFieldTypes.Multiselect
                or OpenCodeFormFieldTypes.External)) throw InvalidInteractionResponse();
            if (field.Type == OpenCodeFormFieldTypes.Multiselect && field.Options is null) throw InvalidInteractionResponse();
            if (field.Type == OpenCodeFormFieldTypes.External && (field.AdditionalProperties is null
                || !field.AdditionalProperties.TryGetValue("url", out var url) || url.ValueKind != JsonValueKind.String))
                throw InvalidInteractionResponse();
            if (field.Options is not null)
                foreach (var option in field.Options)
                    if (option?.Value is null || option.Label is null) throw InvalidInteractionResponse();
        }
    }

    private static void ValidateFormDetail(OpenCodeFormDetail form, string sessionId)
    {
        ValidateForm(form, sessionId);
        var state = form.State;
        if (state is null) throw InvalidInteractionResponse();
        switch (state.Status)
        {
            case OpenCodeFormStatuses.Pending:
                if (state.Answer is not null || state.Message is not null) throw InvalidInteractionResponse();
                break;
            case OpenCodeFormStatuses.Answered:
                if (state.Answer is null || state.Message is not null) throw InvalidInteractionResponse();
                foreach (var value in state.Answer.Values)
                    if (!IsFormValue(value)) throw InvalidInteractionResponse();
                break;
            case OpenCodeFormStatuses.Cancelled:
                if (state.Answer is not null) throw InvalidInteractionResponse();
                break;
            default:
                throw InvalidInteractionResponse();
        }
    }

    private static void RequirePendingForm(OpenCodeFormDetail form, string sessionId)
    {
        ValidateFormDetail(form, sessionId);
        if (form.State!.Status != OpenCodeFormStatuses.Pending)
            throw new InvalidOperationException("Form 快照不是 pending；不会发送回复或取消请求。");
    }

    private static bool IsFormValue(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return true;
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String) return false;
        return true;
    }

    private static OpenCodeProtocolException InvalidInteractionResponse() => new(
        "权限/Form 数据缺少必填字段、类型/状态无效或 ID 与请求会话不匹配。");

    private static string GetEndpointTemplate(string path)
    {
        if (path is "api/location" or "api/session") return path;
        var segments = path.Split('/');
        if (segments.Length < 4 || segments[0] != "api" || segments[1] != "session") return "api/request";
        var resource = segments[3];
        if (resource is not ("prompt" or "interrupt" or "message" or "inbox" or "permission" or "form")) return "api/request";
        var template = "api/session/{sessionID}/" + resource;
        if (segments.Length >= 5 && resource is "permission" or "form")
            template += resource == "permission" ? "/{requestID}" : "/{formID}";
        if (segments.Length == 5 && resource == "inbox") template += "/{inboxID}";
        if (segments.Length == 6 && segments[5] == "reply") template += "/reply";
        return template;
    }

    private async Task SendForNoContentAsync(HttpMethod method, string path,
        IReadOnlyList<KeyValuePair<string, string>>? query, HttpContent? content, CancellationToken cancellationToken)
    {
        var label = method.Method + " " + GetEndpointTemplate(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var response = await SendAsync(method, path, query, content, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OpenCodeApiException((int)response.StatusCode, null, null,
                    $"OpenCode 服务返回 HTTP {(int)response.StatusCode}（{label}）。");
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new OpenCodeProtocolException($"响应必须为 HTTP 204（{label}）。");
            // A 204 has no representation to deserialize. Never read success/error body or retry mutations.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("OpenCode 请求已取消。", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new OpenCodeTimeoutException($"OpenCode 请求超时（{label}）。");
        }
        catch (HttpRequestException)
        {
            throw new OpenCodeTransportException($"OpenCode 请求失败（{label}）。");
        }
        catch (IOException)
        {
            throw new OpenCodeTransportException($"OpenCode 请求失败（{label}）。");
        }
    }
}

internal sealed record PermissionReplyBody(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record FormReplyBody(
    [property: JsonPropertyName("answer")] IReadOnlyDictionary<string, JsonElement> Answer);
