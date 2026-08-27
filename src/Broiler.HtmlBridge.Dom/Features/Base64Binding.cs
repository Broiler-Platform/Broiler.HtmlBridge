using System.Text;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>btoa</c> and <c>atob</c> — the base64 pair on <c>WindowOrWorkerGlobalScope</c>
/// (HTML §8.3), which the bridge did not provide at all. An unqualified <c>atob(…)</c> was a
/// <c>ReferenceError</c>, and that aborts the whole script rather than the one call.
/// <para>
/// Both work on <em>binary strings</em>, not text: <c>btoa</c> takes a string whose code points are
/// all ≤ U+00FF and treats each as one byte, and <c>atob</c> returns a string built the same way.
/// That is why neither is a UTF-8 round trip and why <c>btoa</c> throws on ordinary non-Latin text
/// — a mistake worth failing loudly rather than mangling, since the alternative silently corrupts
/// the bytes a page is trying to move.
/// </para>
/// </summary>
internal static class Base64Binding
{
    /// <summary>
    /// <c>btoa(data)</c> — base64-encodes a binary string. Throws <c>InvalidCharacterError</c> for a
    /// code point above U+00FF, which cannot be one byte.
    /// </summary>
    internal static JSValue Btoa(JSContext context, in Arguments a)
    {
        var input = a.Length > 0 ? a[0].ToString() : "undefined";
        var bytes = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] > 0xFF)
            {
                DomBridge.ThrowDOMException(context,
                    "The string to be encoded contains characters outside of the Latin1 range.",
                    "InvalidCharacterError");
                return JSUndefined.Value;
            }

            bytes[i] = (byte)input[i];
        }

        return new JSString(Convert.ToBase64String(bytes));
    }

    /// <summary>
    /// <c>atob(data)</c> — decodes base64 to a binary string, by Infra's <em>forgiving-base64
    /// decode</em>. Throws <c>InvalidCharacterError</c> when the input cannot be decoded.
    /// </summary>
    internal static JSValue Atob(JSContext context, in Arguments a)
    {
        var input = a.Length > 0 ? a[0].ToString() : "undefined";
        if (!TryForgivingBase64Decode(input, out var bytes))
        {
            DomBridge.ThrowDOMException(context,
                "The string to be decoded is not correctly encoded.",
                "InvalidCharacterError");
            return JSUndefined.Value;
        }

        // Each byte becomes one code unit, which is what makes the result a binary string rather
        // than decoded text — the inverse of what Btoa consumed.
        return new JSString(string.Create(bytes.Length, bytes, static (chars, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                chars[i] = (char)source[i];
        }));
    }

    /// <summary>
    /// Infra's forgiving-base64 decode. Written out rather than delegated to
    /// <see cref="Convert.FromBase64String"/> because the two disagree at the edges that matter
    /// here: this one strips ASCII whitespace anywhere, accepts unpadded input whose length is not a
    /// multiple of four, and rejects the one-character tail that cannot encode any byte —
    /// distinctions a page relying on the platform's behaviour will hit.
    /// </summary>
    internal static bool TryForgivingBase64Decode(string input, out byte[] bytes)
    {
        bytes = [];

        var builder = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            // The ASCII whitespace set Infra defines — notably not every Unicode space.
            if (c is '\t' or '\n' or '\f' or '\r' or ' ')
                continue;
            builder.Append(c);
        }

        var data = builder.ToString();

        // Padding is stripped only from input that is already a multiple of four; anywhere else an
        // '=' is simply a character outside the alphabet, and so a failure.
        if (data.Length % 4 == 0)
        {
            if (data.EndsWith("==", StringComparison.Ordinal))
                data = data[..^2];
            else if (data.EndsWith('='))
                data = data[..^1];
        }

        // A single leftover character carries six bits: not enough for a byte, and not a tail any
        // encoder produces.
        if (data.Length % 4 == 1)
            return false;

        var output = new byte[data.Length * 3 / 4];
        var written = 0;
        var buffer = 0;
        var bits = 0;
        foreach (var c in data)
        {
            var value = DecodeChar(c);
            if (value < 0)
                return false;

            buffer = (buffer << 6) | value;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output[written++] = (byte)((buffer >> bits) & 0xFF);
            }
        }

        bytes = written == output.Length ? output : output[..written];
        return true;
    }

    private static int DecodeChar(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };
}
