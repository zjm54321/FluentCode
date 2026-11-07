using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentCode.Pages.OpenCode;

/// <summary>Where a link points. Brands match the exact host only (<c>github.com.evil.io</c> is <see cref="Generic"/>).</summary>
internal enum OpenCodeLinkSite { Generic, Local, GitHub, GitLab, Gitee, Codeberg, Npm, Crates, PyPI, Mdn, StackOverflow, Wikipedia, Jira, Linear }

internal enum OpenCodeLinkKind { Page, Home, Owner, Repository, Issue, PullRequest, MergeRequest, Commit, File, Release, Package, Ticket }

/// <summary>
/// One recognised http(s) link. <see cref="Url"/> is the exact original text (what is sent and copied);
/// <see cref="Label"/> is the ≤32-character chip text; <see cref="DisplayUrl"/> is the hover-card address with
/// credentials and secret query values masked. Presentation only — never changes what the model receives.
/// </summary>
internal sealed record OpenCodeLinkChip(string Url, Uri Uri, OpenCodeLinkSite Site, OpenCodeLinkKind Kind, string Label,
    string Host, string DisplayUrl, bool HasCredentials, bool IsConfusable)
{
    internal string? Owner { get; init; }
    internal string? Repository { get; init; }
    internal string? Number { get; init; }
    internal bool IsForge => Site is OpenCodeLinkSite.GitHub or OpenCodeLinkSite.GitLab or OpenCodeLinkSite.Gitee or OpenCodeLinkSite.Codeberg;
    internal bool IsPrivate => Site == OpenCodeLinkSite.Local;

    /// <summary>Narrator name, e.g. “链接，GitHub issue，oh-my-opencode-slim 1591”.</summary>
    internal string AccessibleName
    {
        get
        {
            var site = OpenCodeLinkChipParser.SiteName(Site);
            var kind = Kind switch
            {
                OpenCodeLinkKind.Issue => " issue", OpenCodeLinkKind.PullRequest => " PR", OpenCodeLinkKind.MergeRequest => " MR",
                OpenCodeLinkKind.Commit => " 提交", OpenCodeLinkKind.File => " 文件", OpenCodeLinkKind.Release => " 版本",
                OpenCodeLinkKind.Repository => " 仓库", OpenCodeLinkKind.Owner => " 用户", _ => string.Empty,
            };
            var spoken = Label.Replace(" #", " ", StringComparison.Ordinal).Replace(" !", " ", StringComparison.Ordinal);
            return "链接，" + site + kind + "，" + spoken + (IsConfusable ? "，警告：域名含易混淆字符" : string.Empty)
                + (HasCredentials ? "，包含用户名或令牌" : string.Empty);
        }
    }
}

/// <summary>A link occurrence in plain text: UTF-16 start/length of the exact URL text.</summary>
internal readonly record struct OpenCodeLinkMatch(int Start, int Length, string Url)
{
    internal int End => Start + Length;
}

/// <summary>
/// Pure link recognition and chip label rules (spec: artifacts/design-rich-chips-20261106T151052Z/spec.md §2, §6).
/// No UI, no network: unit-tested by the composer runner.
/// </summary>
internal static class OpenCodeLinkChipParser
{
    internal const int MaxLabel = 32;
    internal const int MaxUrl = 2048;

    private static readonly HashSet<string> GitHubReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "features", "pricing", "settings", "login", "logout", "signup", "join", "explore", "marketplace", "topics",
        "notifications", "search", "sponsors", "trending", "new", "organizations", "orgs", "enterprise", "security",
        "site", "contact", "collections", "events", "codespaces", "pulls", "issues", "dashboard", "apps", "account",
    };

    // ------------------------------------------------------------------ recognition

    /// <summary>
    /// Parses one candidate URL. Only absolute http/https with a host, ≤ 2048 characters; anything else (file:, vscode:,
    /// mailto:, malformed) stays plain text and is never clickable.
    /// </summary>
    internal static OpenCodeLinkChip? Parse(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrl || url.Any(char.IsWhiteSpace)) return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return null;
        string idn;
        try { idn = uri.IdnHost.ToLowerInvariant(); }
        catch (Exception) { return null; }
        var unicodeHost = uri.Host.ToLowerInvariant();
        var confusable = idn.Contains("xn--", StringComparison.Ordinal) || unicodeHost.Any(c => c > 0x7F);
        var host = idn.Trim('[', ']');
        var shown = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        var credentials = uri.UserInfo.Length > 0 || HasSecretQuery(uri.Query);
        var display = DisplayUrl(uri, idn);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToArray();
        var site = SiteOf(host);
        OpenCodeLinkChip Make(OpenCodeLinkSite s, OpenCodeLinkKind k, string label) => new(url, uri, s, k, Ellipsize(label), host, display, credentials, confusable);

        if (site == OpenCodeLinkSite.Local)
        {
            var authority = (uri.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host) + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
            return Make(site, segments.Length == 0 ? OpenCodeLinkKind.Home : OpenCodeLinkKind.Page, GenericLabel(authority, segments));
        }
        if (site is OpenCodeLinkSite.GitHub or OpenCodeLinkSite.GitLab or OpenCodeLinkSite.Gitee or OpenCodeLinkSite.Codeberg)
            return Forge(site, uri, segments, Make) ?? Make(site, segments.Length == 0 ? OpenCodeLinkKind.Home : OpenCodeLinkKind.Page, GenericLabel(shown, segments));
        var known = Known(site, segments, uri);
        if (known is { } k2) return Make(site, k2.Kind, k2.Label);
        return Make(site, segments.Length == 0 ? OpenCodeLinkKind.Home : OpenCodeLinkKind.Page, GenericLabel(shown, segments));
    }

    private static string Unescape(string segment)
    {
        try { return Uri.UnescapeDataString(segment); }
        catch (Exception) { return segment; }
    }

    internal static OpenCodeLinkSite SiteOf(string host)
    {
        host = host.ToLowerInvariant().Trim('[', ']').TrimEnd('.');
        if (IsPrivateHost(host)) return OpenCodeLinkSite.Local;
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host switch
        {
            "github.com" => OpenCodeLinkSite.GitHub,
            "gitlab.com" => OpenCodeLinkSite.GitLab,
            "gitee.com" => OpenCodeLinkSite.Gitee,
            "codeberg.org" => OpenCodeLinkSite.Codeberg,
            "npmjs.com" => OpenCodeLinkSite.Npm,
            "crates.io" => OpenCodeLinkSite.Crates,
            "pypi.org" => OpenCodeLinkSite.PyPI,
            "developer.mozilla.org" => OpenCodeLinkSite.Mdn,
            "stackoverflow.com" => OpenCodeLinkSite.StackOverflow,
            "linear.app" => OpenCodeLinkSite.Linear,
            _ when host.EndsWith(".wikipedia.org", StringComparison.Ordinal) && host.Count(c => c == '.') == 2 => OpenCodeLinkSite.Wikipedia,
            _ when host.EndsWith(".atlassian.net", StringComparison.Ordinal) && host.Count(c => c == '.') == 2 => OpenCodeLinkSite.Jira,
            _ => OpenCodeLinkSite.Generic,
        };
    }

    internal static string SiteName(OpenCodeLinkSite site) => site switch
    {
        OpenCodeLinkSite.GitHub => "GitHub", OpenCodeLinkSite.GitLab => "GitLab", OpenCodeLinkSite.Gitee => "Gitee",
        OpenCodeLinkSite.Codeberg => "Codeberg", OpenCodeLinkSite.Npm => "npm", OpenCodeLinkSite.Crates => "crates.io",
        OpenCodeLinkSite.PyPI => "PyPI", OpenCodeLinkSite.Mdn => "MDN", OpenCodeLinkSite.StackOverflow => "Stack Overflow",
        OpenCodeLinkSite.Wikipedia => "维基百科", OpenCodeLinkSite.Jira => "Jira", OpenCodeLinkSite.Linear => "Linear",
        OpenCodeLinkSite.Local => "本机或内网", _ => "网站",
    };

    private static OpenCodeLinkChip? Forge(OpenCodeLinkSite site, Uri uri, string[] s,
        Func<OpenCodeLinkSite, OpenCodeLinkKind, string, OpenCodeLinkChip> make)
    {
        if (s.Length == 0) return null;
        // GitLab (and nested groups): ".../group/sub/project/-/issues/1". Others: "owner/repo/...".
        string owner, repo;
        string[] rest;
        var dash = Array.IndexOf(s, "-");
        if (dash >= 2)
        {
            owner = string.Join('/', s[..(dash - 1)]);
            repo = s[dash - 1];
            rest = s[(dash + 1)..];
        }
        else if (s.Length == 1)
        {
            if (GitHubReserved.Contains(s[0])) return null;
            return make(site, OpenCodeLinkKind.Owner, s[0]) with { Owner = s[0] };
        }
        else
        {
            if (GitHubReserved.Contains(s[0])) return null;
            owner = s[0];
            repo = s[1];
            rest = s[2..];
        }
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        OpenCodeLinkChip With(OpenCodeLinkKind kind, string label, string? number = null)
            => make(site, kind, label) with { Owner = owner, Repository = repo, Number = number };
        if (rest.Length == 0) return With(OpenCodeLinkKind.Repository, repo);
        var verb = rest[0].ToLowerInvariant();
        var arg = rest.Length > 1 ? rest[1] : null;
        switch (verb)
        {
            case "issues" when arg is not null && IsNumber(arg):
                return With(OpenCodeLinkKind.Issue, Combine(repo, " #" + arg), arg);
            case "pull" or "pulls" when arg is not null && IsNumber(arg):
                return With(OpenCodeLinkKind.PullRequest, Combine(repo, " #" + arg), arg);
            case "merge_requests" when arg is not null && IsNumber(arg):
                return With(OpenCodeLinkKind.MergeRequest, Combine(repo, " !" + arg), arg);
            case "commit" or "commits" when arg is not null && IsSha(arg):
                return With(OpenCodeLinkKind.Commit, Combine(repo, "@" + arg[..Math.Min(7, arg.Length)].ToLowerInvariant()), arg);
            case "releases" when rest.Length > 2 && rest[1] == "tag":
                return With(OpenCodeLinkKind.Release, Combine(repo, " " + string.Join('/', rest[2..])));
            case "tags" or "tag" when arg is not null:
                return With(OpenCodeLinkKind.Release, Combine(repo, " " + string.Join('/', rest[1..])));
            case "blob" or "raw" or "src" when rest.Length > 2:
            {
                // Gitea/Codeberg: src/branch/main/path; GitHub/GitLab/Gitee: blob/<ref>/path.
                var path = verb == "src" && rest.Length > 3 && rest[1] is "branch" or "tag" or "commit" ? rest[3..] : rest[2..];
                if (path.Length == 0) return With(OpenCodeLinkKind.Repository, repo);
                return With(OpenCodeLinkKind.File, path[^1] + Lines(uri.Fragment));
            }
            default:
                return With(OpenCodeLinkKind.Repository, repo);
        }
    }

    /// <summary>Keeps the distinguishing suffix whole when the repository name is long (label stays ≤ 32).</summary>
    private static string Combine(string repo, string suffix)
        => repo.Length + suffix.Length <= MaxLabel ? repo + suffix : Ellipsize(repo, Math.Max(4, MaxLabel - suffix.Length)) + suffix;

    private static (OpenCodeLinkKind Kind, string Label)? Known(OpenCodeLinkSite site, string[] s, Uri uri)
    {
        switch (site)
        {
            case OpenCodeLinkSite.Npm when s.Length >= 2 && s[0] == "package":
                return (OpenCodeLinkKind.Package, s.Length >= 3 && s[1].StartsWith('@') ? s[1] + "/" + s[2] : s[1]);
            case OpenCodeLinkSite.Crates when s.Length >= 2 && s[0] == "crates":
                return (OpenCodeLinkKind.Package, s[1]);
            case OpenCodeLinkSite.PyPI when s.Length >= 2 && s[0] == "project":
                return (OpenCodeLinkKind.Package, s[1]);
            case OpenCodeLinkSite.Mdn when s.Length >= 1:
                return (OpenCodeLinkKind.Page, "MDN " + s[^1]);
            case OpenCodeLinkSite.StackOverflow when s.Length >= 2 && s[0] == "questions" && IsNumber(s[1]):
                return (OpenCodeLinkKind.Page, s.Length >= 3 ? s[2] : "#" + s[1]);
            case OpenCodeLinkSite.Wikipedia when s.Length >= 2 && s[0] == "wiki":
                return (OpenCodeLinkKind.Page, s[1].Replace('_', ' '));
            case OpenCodeLinkSite.Jira when s.Length >= 2 && s[0] == "browse":
                return (OpenCodeLinkKind.Ticket, s[1]);
            case OpenCodeLinkSite.Linear when s.Length >= 3 && s[1] == "issue":
                return (OpenCodeLinkKind.Ticket, s[2]);
        }
        _ = uri;
        return null;
    }

    private static bool IsNumber(string s) => s.Length is > 0 and <= 12 && s.All(char.IsAsciiDigit);
    private static bool IsSha(string s) => s.Length is >= 7 and <= 64 && s.All(char.IsAsciiHexDigit);

    /// <summary>"#L1224-L1271" → ":1224-1271"; "#L5" → ":5"; GitLab "#L10-20" → ":10-20".</summary>
    internal static string Lines(string? fragment)
    {
        var m = Regex.Match(fragment ?? string.Empty, @"^#L(\d+)(?:-L?(\d+))?$");
        if (!m.Success) return string.Empty;
        return ":" + m.Groups[1].Value + (m.Groups[2].Success ? "-" + m.Groups[2].Value : string.Empty);
    }

    /// <summary>Generic site: host (no www.) + last path segment; home page shows the host only.</summary>
    internal static string GenericLabel(string host, string[] segments)
    {
        if (segments.Length == 0) return host;
        var last = segments[^1];
        return segments.Length == 1 ? host + "/" + last : host + "/…/" + last;
    }

    /// <summary>Middle ellipsis to at most <paramref name="max"/> characters (head keeps the extra one).</summary>
    internal static string Ellipsize(string text, int max = MaxLabel)
    {
        if (text.Length <= max) return text;
        var keep = max - 1;
        var head = (keep + 1) / 2;
        var tail = keep - head;
        if (char.IsHighSurrogate(text[head - 1])) head--;
        if (tail > 0 && char.IsLowSurrogate(text[text.Length - tail])) tail--;
        return text[..head] + "…" + text[^tail..];
    }

    // ------------------------------------------------------------------ privacy

    private static readonly string[] SecretKeys =
        { "token", "access_token", "id_token", "refresh_token", "api_key", "apikey", "key", "secret", "client_secret",
          "password", "pass", "pwd", "sig", "signature", "auth", "authorization", "session", "sessionid", "x-amz-signature",
          "x-amz-credential", "code" };

    internal static bool HasSecretQuery(string? query)
        => QueryPairs(query).Any(p => IsSecretKey(p.Key));

    private static bool IsSecretKey(string key)
        => SecretKeys.Contains(key, StringComparer.OrdinalIgnoreCase) || key.EndsWith("token", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("secret", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Key, string Value)> QueryPairs(string? query)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            yield return eq < 0 ? (pair, string.Empty) : (pair[..eq], pair[(eq + 1)..]);
        }
    }

    /// <summary>Hover-card address: no user info, secret query values replaced by "•••", punycode host.</summary>
    internal static string DisplayUrl(Uri uri, string idnHost)
    {
        var sb = new StringBuilder();
        sb.Append(uri.Scheme).Append("://");
        sb.Append(uri.HostNameType == UriHostNameType.IPv6 && !idnHost.StartsWith('[') ? "[" + idnHost + "]" : idnHost);
        if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        sb.Append(uri.AbsolutePath);
        var pairs = QueryPairs(uri.Query).ToArray();
        if (pairs.Length > 0)
            sb.Append('?').Append(string.Join('&', pairs.Select(p => IsSecretKey(p.Key) ? p.Key + "=•••" : p.Value.Length == 0 && !uri.Query.Contains(p.Key + "=", StringComparison.Ordinal) ? p.Key : p.Key + "=" + p.Value)));
        sb.Append(uri.Fragment);
        return sb.ToString();
    }

    /// <summary>
    /// Loopback, private, link-local, CGNAT, ULA, single-label and intranet suffixes. These never fetch anything
    /// (favicon or metadata) and use the computer / network icon.
    /// </summary>
    internal static bool IsPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        host = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var ip)) return IsPrivateAddress(ip);
        if (!host.Contains('.')) return true; // localhost, intranet single-label names
        foreach (var suffix in new[] { ".localhost", ".local", ".lan", ".internal", ".intranet", ".corp", ".home", ".home.arpa", ".test", ".invalid", ".example" })
            if (host.EndsWith(suffix, StringComparison.Ordinal)) return true;
        return false;
    }

    internal static bool IsPrivateAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC;
        }
        return true;
    }

    // ------------------------------------------------------------------ finding links in text

    /// <summary>
    /// http(s) links in <paramref name="text"/>. Trailing English/Chinese sentence punctuation is excluded, paired
    /// brackets inside the URL are kept. With <paramref name="markdown"/>, inline code and fenced code blocks are skipped.
    /// With <paramref name="streaming"/>, a link that runs to the very end of the text is not yet a link (it may still grow).
    /// </summary>
    internal static IReadOnlyList<OpenCodeLinkMatch> Find(string? text, bool markdown = false, bool streaming = false)
    {
        var result = new List<OpenCodeLinkMatch>();
        if (string.IsNullOrEmpty(text)) return result;
        var code = markdown ? CodeRanges(text) : new List<(int Start, int End)>();
        var i = 0;
        while (i < text.Length)
        {
            var at = IndexOfScheme(text, i);
            if (at < 0) break;
            if (at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] is '/' or '@' or '\\' or ':'))
            {
                i = at + 4;
                continue;
            }
            var end = at;
            while (end < text.Length && IsUrlChar(text[end])) end++;
            end = TrimEnd(text, at, end);
            var inCode = code.Any(r => at >= r.Start && at < r.End);
            var length = end - at;
            var url = text.Substring(at, length);
            if (!inCode && !(streaming && end == text.Length) && Parse(url) is not null)
                result.Add(new(at, length, url));
            i = Math.Max(end, at + 4);
        }
        return result;
    }

    private static int IndexOfScheme(string text, int from)
    {
        var a = text.IndexOf("http://", from, StringComparison.OrdinalIgnoreCase);
        var b = text.IndexOf("https://", from, StringComparison.OrdinalIgnoreCase);
        if (a < 0) return b;
        if (b < 0) return a;
        return Math.Min(a, b);
    }

    private static bool IsUrlChar(char c)
    {
        if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        if (c is '<' or '>' or '"' or '`' or '{' or '}' or '|' or '\\' or '^') return false;
        // CJK text and full-width punctuation directly after a URL are prose, not path.
        if (c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFFEF || c >= 0x3000 && c <= 0x303F
            || c is '…' or '‘' or '’' or '“' or '”') return false;
        return true;
    }

    private static int TrimEnd(string text, int start, int end)
    {
        while (end > start)
        {
            var c = text[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*' or '_' or '~') { end--; continue; }
            if (c is ')' or ']')
            {
                var open = c == ')' ? '(' : '[';
                var span = text.AsSpan(start, end - start);
                int opens = 0, closes = 0;
                foreach (var ch in span) { if (ch == open) opens++; else if (ch == c) closes++; }
                if (closes > opens) { end--; continue; }
            }
            break;
        }
        return end;
    }

    /// <summary>Fenced blocks (``` / ~~~ at line start) and inline backtick spans, as [start, end) ranges.</summary>
    internal static List<(int Start, int End)> CodeRanges(string text)
    {
        var ranges = new List<(int, int)>();
        var i = 0;
        while (i < text.Length)
        {
            var lineStart = i == 0 || text[i - 1] == '\n';
            if (lineStart)
            {
                var j = i;
                while (j < text.Length && j - i < 4 && text[j] == ' ') j++;
                if (j + 2 < text.Length && (text.AsSpan(j, 3).SequenceEqual("```") || text.AsSpan(j, 3).SequenceEqual("~~~")))
                {
                    var fence = text.Substring(j, 3);
                    var nl = text.IndexOf('\n', j);
                    var close = nl < 0 ? -1 : text.IndexOf("\n" + fence, nl, StringComparison.Ordinal);
                    // An unclosed fence runs to the end (CommonMark).
                    var end = text.Length;
                    if (close >= 0)
                    {
                        var after = text.IndexOf('\n', close + 1);
                        end = after < 0 ? text.Length : after;
                    }
                    ranges.Add((i, end));
                    i = end;
                    continue;
                }
            }
            if (text[i] == '`')
            {
                var n = 0;
                while (i + n < text.Length && text[i + n] == '`') n++;
                var search = i + n;
                var closeAt = -1;
                while (search < text.Length)
                {
                    var k = text.IndexOf('`', search);
                    if (k < 0) break;
                    var m = 0;
                    while (k + m < text.Length && text[k + m] == '`') m++;
                    if (m == n) { closeAt = k; break; }
                    search = k + m;
                }
                if (closeAt >= 0)
                {
                    ranges.Add((i, closeAt + n));
                    i = closeAt + n;
                    continue;
                }
                i += n;
                continue;
            }
            i++;
        }
        return ranges;
    }

    /// <summary>
    /// <c>[text](https://…)</c> outside code becomes something the chip renderer can see (the HTML sanitizer drops
    /// every href): when the text is itself a URL / empty, only the REAL target remains (a misleading text URL is never
    /// shown as a link); otherwise "text (target)". Images (<c>![…](…)</c>) are untouched.
    /// </summary>
    internal static string RewriteMarkdownLinks(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("](", StringComparison.Ordinal)) return markdown ?? string.Empty;
        var code = CodeRanges(markdown);
        return MarkdownLink.Replace(markdown, m =>
        {
            if (m.Index > 0 && markdown[m.Index - 1] == '!') return m.Value;
            if (code.Any(r => m.Index >= r.Start && m.Index < r.End)) return m.Value;
            var target = m.Groups["url"].Value;
            if (Parse(target) is null) return m.Value;
            var label = m.Groups["text"].Value.Trim();
            if (label.Length == 0 || label.Equals(target, StringComparison.OrdinalIgnoreCase) || LooksLikeUrl(label)) return target;
            return label + " (" + target + ")";
        });
    }

    private static readonly Regex MarkdownLink = new(@"\[(?<text>[^\[\]\n]{0,500})\]\((?<url>https?://[^\s()<>]+(?:\([^\s()<>]*\)[^\s()<>]*)*)(?:\s+""[^""\n]*"")?\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static bool LooksLikeUrl(string text)
        => text.Contains("://", StringComparison.Ordinal) || Regex.IsMatch(text, @"^(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)+(/\S*)?$", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------ favicon policy

    /// <summary>
    /// Online favicon only for generic public https sites with the user's opt-in; known sites keep bundled icons and
    /// local/intranet hosts never touch the network.
    /// </summary>
    internal static bool ShouldFetchFavicon(OpenCodeLinkChip chip, bool onlineEnabled)
        => onlineEnabled && chip.Site == OpenCodeLinkSite.Generic && chip.Uri.Scheme == "https"
            && !IsPrivateHost(chip.Host) && !chip.IsConfusable;

    /// <summary>Per-origin cache key ("https://host[:port]"); credentials, path and query never leave this method.</summary>
    internal static string FaviconOrigin(OpenCodeLinkChip chip)
        => "https://" + chip.Host + (chip.Uri.IsDefaultPort ? "" : ":" + chip.Uri.Port.ToString(CultureInfo.InvariantCulture));
}

/// <summary>A composer link chip: <see cref="Display"/> (a label prefixed by an icon gap) stands in the editor text at
/// <see cref="Start"/>; <see cref="Url"/> is what send/copy restore.</summary>
internal sealed record OpenCodeComposerLinkSpan(int Start, string Display, string Url)
{
    /// <summary>Editor prefix that reserves room for the 14 px icon + 4 px gap drawn by the chip layer.</summary>
    internal const string IconGap = "\u2003\u200A";
    internal int End => Start + Display.Length;
    internal bool Matches(string text) => Start >= 0 && End <= text.Length && text.AsSpan(Start, Display.Length).SequenceEqual(Display.AsSpan());
    internal static OpenCodeComposerLinkSpan? Create(int start, string url)
        => OpenCodeLinkChipParser.Parse(url) is { } chip ? new(start, IconGap + chip.Label, url) : null;
    public override string ToString() => "OpenCodeComposerLinkSpan { [redacted] }";
}

/// <summary>A pasted image placeholder: literal TUI text "[Image N]" bound to one attachment.</summary>
internal sealed record OpenCodeComposerImageSpan(int Start, int Number, Guid AttachmentId)
{
    internal string Token => OpenCodeComposerChips.ImageToken(Number);
    internal int End => Start + Token.Length;
    internal bool Matches(string text) => Start >= 0 && End <= text.Length && text.AsSpan(Start, Token.Length).SequenceEqual(Token.AsSpan());
}

/// <summary>Pure composer chip bookkeeping: span shifting, expansion for send/copy, image numbering.</summary>
internal static class OpenCodeComposerChips
{
    internal static string ImageToken(int number) => "[Image " + number.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>Next number: max used in this draft + 1 (deleting never renumbers; a fresh draft restarts at 1).</summary>
    internal static int NextImageNumber(IEnumerable<OpenCodeComposerImageSpan> images, int highWater)
        => Math.Max(highWater, images.Select(i => i.Number).DefaultIfEmpty(0).Max()) + 1;

    /// <summary>The single replacement between two texts as (prefix, oldEnd, delta).</summary>
    internal static (int Prefix, int OldEnd, int Delta) Diff(string before, string after)
    {
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix
            && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        return (prefix, before.Length - suffix, after.Length - before.Length);
    }

    /// <summary>
    /// Moves spans after an edit and drops any span the edit touched (an edit inside a chip turns it back into text —
    /// the chip is gone, its characters are ordinary).
    /// </summary>
    internal static void Shift<T>(List<T> spans, string before, string after, Func<T, int> start, Func<T, int> end,
        Func<T, int, T> move, Func<T, string, bool> matches)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            spans.RemoveAll(s => !matches(s, after));
            return;
        }
        var (prefix, oldEnd, delta) = Diff(before, after);
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var s = spans[i];
            if (start(s) >= oldEnd) spans[i] = s = move(s, start(s) + delta);
            else if (end(s) > prefix) { spans.RemoveAt(i); continue; }
            if (!matches(s, after)) spans.RemoveAt(i);
        }
    }

    /// <summary>Editor text with every live link chip replaced by its URL (what the model receives).</summary>
    internal static string Expand(string text, IEnumerable<OpenCodeComposerLinkSpan> links)
        => ExpandRange(text, 0, text.Length, links);

    /// <summary>
    /// The selection [start, start+length) with chips restored to their URLs (copy/cut). A selection that only partly
    /// covers a chip still yields the whole URL — never half a label.
    /// </summary>
    internal static string ExpandRange(string text, int start, int length, IEnumerable<OpenCodeComposerLinkSpan> links)
    {
        start = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, text.Length);
        var live = links.Where(l => l.Matches(text) && l.End > start && l.Start < end).OrderBy(l => l.Start).ToArray();
        if (live.Length == 0) return text[start..end];
        var sb = new StringBuilder();
        var at = start;
        foreach (var l in live)
        {
            if (l.Start > at) sb.Append(text, at, l.Start - at);
            sb.Append(l.Url);
            at = Math.Max(at, l.End);
        }
        if (at < end) sb.Append(text, at, end - at);
        return sb.ToString();
    }

    /// <summary>Maps an editor offset to the expanded (sent) text offset; mention spans move with their text.</summary>
    internal static int ExpandOffset(string text, int offset, IEnumerable<OpenCodeComposerLinkSpan> links)
    {
        var shift = 0;
        foreach (var l in links)
            if (l.Matches(text) && l.End <= offset) shift += l.Url.Length - l.Display.Length;
        return offset + shift;
    }

    /// <summary>Clipboard text that is exactly one link (surrounding whitespace allowed) → that URL; else null.</summary>
    internal static string? SingleLink(string? clipboard)
    {
        var trimmed = clipboard?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        var found = OpenCodeLinkChipParser.Find(trimmed);
        return found.Count == 1 && found[0].Start == 0 && found[0].Length == trimmed.Length ? trimmed : null;
    }

    /// <summary>
    /// Transcript "[Image N]" / "[PDF N]" tokens backed by an actual file part (N ≤ count of that kind); a typed
    /// "[Image 3]" with no third image stays text.
    /// </summary>
    internal static IReadOnlyList<(int Start, int Length, bool Pdf, int Number)> AttachmentTokens(string? text, int images, int pdfs)
    {
        var result = new List<(int, int, bool, int)>();
        if (string.IsNullOrEmpty(text) || images + pdfs == 0) return result;
        foreach (Match m in AttachmentToken.Matches(text))
        {
            var pdf = m.Groups[1].Value == "PDF";
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) continue;
            if (n <= (pdf ? pdfs : images)) result.Add((m.Index, m.Length, pdf, n));
        }
        return result;
    }

    private static readonly Regex AttachmentToken = new(@"\[(Image|PDF) (\d{1,4})\]", RegexOptions.CultureInvariant);
}
