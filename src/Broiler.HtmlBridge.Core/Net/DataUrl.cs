using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Broiler.HtmlBridge.Net;

/// <summary>
/// The <c>data:</c> URL processor of the Fetch standard (§"data: URLs"), over the URL as the
/// document wrote it: the type a <c>data:</c> URL declares and the bytes it carries.
/// </summary>
/// <remarks>
/// <para>
/// Its base64 bodies are decoded with Infra's forgiving-base64 decode, not
/// <see cref="Convert.FromBase64String"/>. The two disagree exactly where pages differ from what an
/// encoder emits. Forgiving-base64 accepts a body without its <c>=</c> padding, and it strips form
/// feed with the other ASCII whitespace. It rejects a vertical tab, a non-ASCII space and a
/// one-character tail. <see cref="Convert.FromBase64String"/> throws on unpadded input. reCAPTCHA's
/// stylesheet carries an unpadded body, so every load of its frame raised a
/// <see cref="FormatException"/> where a browser decodes the URL.
/// </para>
/// <para>
/// Nothing here throws: a URL that is not a valid <c>data:</c> URL answers <see langword="false"/>,
/// which a loader reports as a failed load, as a browser fails it.
/// </para>
/// <para>
/// The URL is not run through the URL parser first. Only the parser's steps that change what
/// reaches the processor are applied. Leading and trailing C0 controls and spaces are trimmed, and
/// ASCII tabs and newlines are removed throughout. The declared type has its C0 controls and
/// non-ASCII code points percent-encoded, as the parser leaves them, so <c>data:†/†,X</c> declares a
/// type of its own where the bare <c>†</c> would not parse. A URL the parser would reject outright,
/// such as <c>data://h:x/,X</c> with its invalid port, still decodes here.
/// </para>
/// <para>
/// Broiler.HTML, Broiler.Layout and Broiler.HtmlBridge all decode <c>data:</c> URLs, and none of
/// them can reach another's internals, so each carries this class. Keep the copies identical: each
/// repository tests its copy against the same web-platform-tests vectors.
/// </para>
/// </remarks>
internal static class DataUrl
{
    private const string AsciiWhitespace = "\t\n\f\r ";

    private const string HttpWhitespace = "\t\n\r ";

    private const string HexDigits = "0123456789ABCDEF";

    private static readonly SearchValues<char> HttpTokenCodePoints =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// Runs the <c>data:</c> URL processor over <paramref name="url"/>: <see langword="true"/> with
    /// the essence of the MIME type it declares and the decoded body, or <see langword="false"/> when
    /// the URL is not a <c>data:</c> URL, has no comma, or declares a base64 body that does not decode.
    /// </summary>
    /// <remarks>
    /// The essence is the type and subtype in lowercase, such as <c>image/png</c>, without the
    /// parameters. A URL that declares no type, or a type that does not parse, is <c>text/plain</c>,
    /// as the standard has it.
    /// </remarks>
    public static bool TryParse(string? url, out string mimeType, [NotNullWhen(true)] out byte[]? body)
    {
        mimeType = string.Empty;
        body = null;
        if (url is null)
            return false;

        var input = PrepareAsUrlParserWould(url);
        if (!input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        input = input[5..];

        // The fragment is not part of the URL's path, so it is not part of the body either.
        var hash = input.IndexOf('#');
        if (hash >= 0)
            input = input[..hash];

        var comma = input.IndexOf(',');
        if (comma < 0)
            return false;

        var declared = TrimAsciiWhitespace(PercentEncodeAsUrlParserWould(input[..comma]));
        var bytes = PercentDecode(input[(comma + 1)..]);

        if (TryFindBase64Marker(declared, out var marker))
        {
            if (!TryForgivingBase64Decode(Encoding.Latin1.GetString(bytes), out bytes))
                return false;

            declared = declared[..marker];
        }

        if (declared.StartsWith(';'))
            declared = "text/plain" + declared;

        mimeType = Essence(declared) ?? "text/plain";
        body = bytes;
        return true;
    }

    /// <summary>
    /// The Encoding standard's UTF-8 decode of a body: a leading byte order mark is dropped, and a
    /// malformed sequence becomes U+FFFD.
    /// </summary>
    public static string Utf8Decode(byte[] body)
    {
        var start = body.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return Encoding.UTF8.GetString(body, start, body.Length - start);
    }

    /// <summary>
    /// Infra's forgiving-base64 decode: ASCII whitespace anywhere is dropped, <c>=</c> padding is
    /// optional, and a body whose length leaves a single character over, or that holds anything
    /// outside the base64 alphabet, is a failure. Bits left over after the last whole byte are
    /// discarded, whatever their value.
    /// </summary>
    public static bool TryForgivingBase64Decode(string input, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;

        var data = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (!IsAsciiWhitespace(c))
                data.Append(c);
        }

        // Padding is stripped only from a body that is a whole number of quanta; anywhere else an
        // '=' is a character outside the alphabet.
        var length = data.Length;
        if (length % 4 == 0 && length > 0 && data[length - 1] == '=')
        {
            length--;
            if (length > 0 && data[length - 1] == '=')
                length--;
        }

        // One leftover character holds six bits, which encode no byte.
        if (length % 4 == 1)
            return false;

        var output = new byte[length * 3 / 4];
        var written = 0;
        var buffer = 0;
        var bits = 0;
        for (var i = 0; i < length; i++)
        {
            var value = Base64Value(data[i]);
            if (value < 0)
                return false;

            // At most 13 bits are pending before a byte is taken off, so 16 are kept.
            buffer = ((buffer << 6) | value) & 0xFFFF;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output[written++] = (byte)(buffer >> bits);
            }
        }

        bytes = output;
        return true;
    }

    /// <summary>
    /// Finds a declared MIME type's trailing <c>;base64</c> marker: a <c>;</c>, any spaces, and
    /// <c>base64</c> in any case, at the very end. Only the last parameter can mark the body as
    /// base64, so <c>image/png;base64;charset=x</c> carries its body as written.
    /// </summary>
    private static bool TryFindBase64Marker(string declared, out int marker)
    {
        marker = -1;
        const string Base64 = "base64";
        if (!declared.EndsWith(Base64, StringComparison.OrdinalIgnoreCase))
            return false;

        var position = declared.Length - Base64.Length - 1;
        while (position >= 0 && declared[position] == ' ')
            position--;

        if (position < 0 || declared[position] != ';')
            return false;

        marker = position;
        return true;
    }

    /// <summary>
    /// The essence of a MIME type, by MIME Sniffing's "parse a MIME type" as far as the essence
    /// goes: a type and a subtype of HTTP token code points either side of a <c>/</c>, in
    /// lowercase, or <see langword="null"/> when <paramref name="mimeType"/> does not parse.
    /// </summary>
    private static string? Essence(string mimeType)
    {
        var input = mimeType.AsSpan().Trim(HttpWhitespace);
        var slash = input.IndexOf('/');
        if (slash <= 0)
            return null;

        var type = input[..slash];
        var subtype = input[(slash + 1)..];
        var semicolon = subtype.IndexOf(';');
        if (semicolon >= 0)
            subtype = subtype[..semicolon];

        subtype = subtype.TrimEnd(HttpWhitespace);
        if (subtype.IsEmpty || type.ContainsAnyExcept(HttpTokenCodePoints) || subtype.ContainsAnyExcept(HttpTokenCodePoints))
            return null;

        return string.Concat(type, "/", subtype).ToLowerInvariant();
    }

    /// <summary>
    /// The URL parser's two input steps that reach the processor whole; see the remarks on
    /// <see cref="DataUrl"/>.
    /// </summary>
    private static string PrepareAsUrlParserWould(string url)
    {
        var start = 0;
        var end = url.Length;
        while (start < end && url[start] <= ' ')
            start++;
        while (end > start && url[end - 1] <= ' ')
            end--;

        var trimmed = url[start..end];
        if (trimmed.AsSpan().IndexOfAny('\t', '\n', '\r') < 0)
            return trimmed;

        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (c is not ('\t' or '\n' or '\r'))
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The declared type as the URL parser leaves it in the URL: C0 controls and code points above
    /// U+007E percent-encoded as their UTF-8 bytes, and the rest as written.
    /// </summary>
    private static string PercentEncodeAsUrlParserWould(string declared)
    {
        if (!declared.AsSpan().ContainsAnyExceptInRange(' ', '~'))
            return declared;

        var builder = new StringBuilder(declared.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(declared))
        {
            if (b is >= (byte)' ' and <= (byte)'~')
                builder.Append((char)b);
            else
                builder.Append('%').Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The URL standard's percent-decode of a string: its UTF-8 bytes, with every <c>%</c> followed
    /// by two hex digits replaced by the byte they name. Any other <c>%</c> stays as it is.
    /// </summary>
    private static byte[] PercentDecode(string input)
    {
        var encoded = Encoding.UTF8.GetBytes(input);
        if (Array.IndexOf(encoded, (byte)'%') < 0)
            return encoded;

        var output = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length
                && HexValue(encoded[i + 1]) is var high and >= 0
                && HexValue(encoded[i + 2]) is var low and >= 0)
            {
                output.Add((byte)((high << 4) | low));
                i += 2;
            }
            else
            {
                output.Add(encoded[i]);
            }
        }

        return [.. output];
    }

    private static string TrimAsciiWhitespace(string value) => value.AsSpan().Trim(AsciiWhitespace).ToString();

    /// <summary>Infra's ASCII whitespace: tab, newline, form feed, carriage return and space — not a vertical tab, and no other Unicode space.</summary>
    private static bool IsAsciiWhitespace(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';

    private static int Base64Value(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
