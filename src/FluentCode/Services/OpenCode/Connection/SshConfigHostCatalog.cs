using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentCode.Services.OpenCode.Connection;

/// <summary>只提供 Host 建议；连接时不替代 OpenSSH 自己的配置解析。</summary>
public static class SshConfigHostCatalog
{
    public static IReadOnlyList<string> Read(string configPath, string? sshDirectory = null)
    {
        sshDirectory = Path.GetFullPath(sshDirectory ?? Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        var hosts = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(Path.GetFullPath(configPath), 0);
        return hosts;

        void Visit(string path, int depth)
        {
            if (depth > 32 || !visited.Add(path) || !File.Exists(path)) return;
            foreach (string line in File.ReadLines(path))
            {
                var words = Tokens(line);
                if (words.Count < 2) continue;
                if (words[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string name in words.Skip(1))
                        if (name.IndexOfAny(new[] { '*', '?', '!', '[', ']' }) < 0
                            && SshConnectionTarget.TryParse(name, out var target) && target!.User is null && target.Port is null
                            && names.Add(name)) hosts.Add(name);
                }
                else if (words[0].Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string include in words.Skip(1))
                        foreach (string file in Expand(include, sshDirectory)) Visit(file, depth + 1);
                }
            }
        }
    }

    public static IReadOnlyList<string> ReadDefault() => Read(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config"));

    private static IEnumerable<string> Expand(string pattern, string sshDirectory)
    {
        if (pattern.StartsWith("~/", StringComparison.Ordinal) || pattern.StartsWith("~\\", StringComparison.Ordinal))
            pattern = Path.Combine(Path.GetDirectoryName(sshDirectory)!, pattern[2..]);
        pattern = Regex.Replace(pattern, @"\$\{([^}]+)\}", match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
        if (!Path.IsPathRooted(pattern)) pattern = Path.Combine(sshDirectory, pattern);
        pattern = Path.GetFullPath(pattern.Replace('/', Path.DirectorySeparatorChar));
        string root = Path.GetPathRoot(pattern)!;
        var candidates = new List<string> { root };
        foreach (string part in pattern[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = new List<string>();
            foreach (string directory in candidates)
            {
                if (!Directory.Exists(directory)) continue;
                if (part.IndexOfAny(new[] { '*', '?', '[' }) >= 0)
                {
                    var glob = Glob(part);
                    next.AddRange(Directory.EnumerateFileSystemEntries(directory).Where(file => glob.IsMatch(Path.GetFileName(file))).Order(StringComparer.OrdinalIgnoreCase));
                }
                else next.Add(Path.Combine(directory, part));
            }
            candidates = next;
        }
        return candidates.Where(File.Exists);
    }

    private static Regex Glob(string pattern)
    {
        var expression = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            if (ch == '*') expression.Append(".*");
            else if (ch == '?') expression.Append('.');
            else if (ch == '[' && pattern.IndexOf(']', i + 1) is int end && end > i + 1)
            {
                string group = pattern[(i + 1)..end];
                expression.Append('[');
                if (group.StartsWith('!')) { expression.Append('^'); group = group[1..]; }
                expression.Append(group.Replace("\\", "\\\\", StringComparison.Ordinal)).Append(']');
                i = end;
            }
            else expression.Append(Regex.Escape(ch.ToString()));
        }
        return new(expression.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // SSH config allows quoted names/paths and key=value. Preserve Windows path backslashes.
    private static List<string> Tokens(string line)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '\\' && i + 1 < line.Length && (line[i + 1] == quote && quote != '\0' || line[i + 1] is '"' or '#'))
            { word.Append(line[++i]); continue; }
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0'; else word.Append(ch);
            }
            else if (ch == '#') break;
            else if (ch is '\'' or '"') quote = ch;
            else if (char.IsWhiteSpace(ch) || ch == '=' && result.Count == 0)
            { if (word.Length > 0) { result.Add(word.ToString()); word.Clear(); } }
            else word.Append(ch);
        }
        if (word.Length > 0) result.Add(word.ToString());
        return result;
    }
}
