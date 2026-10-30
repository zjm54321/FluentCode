using System;
using System.Text;

namespace AIFrontend.Pages.OpenCode;

/// <summary>Streaming UTF-8/VT boundary. OSC 52 cannot mutate the viewer's clipboard.</summary>
internal sealed class OpenCodePersistentPtyVtStream
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _prefix = new();
    private int _state; // 0 normal, 1 ESC, 2 OSC command, 3 pass OSC, 4 drop OSC, 5/6 OSC ESC
    private long _command;

    internal string Decode(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var count = _decoder.GetChars(bytes, chars, false);
        var output = new StringBuilder(count);
        for (var i = 0; i < count; i++)
        {
            var ch = chars[i];
            if (ch == '\0') continue; // VT NUL is a no-op; the C ABI itself is NUL-terminated.
            if (_state == 0)
            {
                if (ch == '\u001b') _state = 1;
                else if (ch == '\u009d') BeginOsc(ch.ToString());
                else output.Append(ch);
            }
            else if (_state == 1)
            {
                if (ch == ']') BeginOsc("\u001b]");
                else { output.Append('\u001b'); if (ch != '\u001b') { output.Append(ch); _state = 0; } }
            }
            else if (_state == 2)
            {
                _prefix.Append(ch);
                if (ch is >= '0' and <= '9' && _prefix.Length <= 32)
                    _command = Math.Min(1000000, _command * 10 + ch - '0');
                else
                {
                    var drop = _command == 52 || _prefix.Length > 32;
                    if (!drop) output.Append(_prefix);
                    _prefix.Clear();
                    _state = ch is '\a' or '\u009c' ? 0 : ch == '\u001b' ? (drop ? 6 : 5) : drop ? 4 : 3;
                }
            }
            else
            {
                var drop = _state is 4 or 6;
                if (!drop) output.Append(ch);
                if (ch is '\a' or '\u009c' || (_state is 5 or 6 && ch == '\\')) _state = 0;
                else if (ch == '\u001b') _state = drop ? 6 : 5;
                else _state = drop ? 4 : 3;
            }
        }
        return output.ToString();
    }

    private void BeginOsc(string introducer)
    { _prefix.Clear(); _prefix.Append(introducer); _command = 0; _state = 2; }
}
