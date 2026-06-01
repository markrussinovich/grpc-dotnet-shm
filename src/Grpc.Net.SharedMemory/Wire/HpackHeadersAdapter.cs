#region Copyright notice and license

// Copyright 2026 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System.Buffers;
using System.Globalization;
using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory.Wire.Hpack;

namespace Grpc.Net.SharedMemory.Wire;

/// <summary>
/// Adapter between the SHM-internal <see cref="HeadersV1"/>/<see cref="TrailersV1"/>
/// model and the HPACK header-list representation used on the HTTP/2 wire.
/// </summary>
internal static class HpackHeadersAdapter
{
    private const string PseudoMethod = ":method";
    private const string PseudoScheme = ":scheme";
    private const string PseudoPath = ":path";
    private const string PseudoAuthority = ":authority";
    private const string PseudoStatus = ":status";
    private const string ContentType = "content-type";
    private const string ContentTypeGrpc = "application/grpc";
    private const string TeHeader = "te";
    private const string TeTrailers = "trailers";
    private const string GrpcTimeout = "grpc-timeout";
    private const string GrpcStatus = "grpc-status";
    private const string GrpcMessage = "grpc-message";

    // Round-7 perf: cache the ASCII byte[] representations of every
    // constant HPACK value we emit so encoding HEADERS/TRAILERS frames
    // does not allocate a fresh byte[] per call for static strings
    // (~6-12 throwaway allocs per header frame previously, ~5 header
    // frames per Unary RPC). Sourced from the matching const strings
    // above so the wire bytes stay self-documenting.
    private static readonly byte[] s_methodPostBytes = Encoding.ASCII.GetBytes("POST");
    private static readonly byte[] s_schemeHttpBytes = Encoding.ASCII.GetBytes("http");
    private static readonly byte[] s_contentTypeGrpcBytes = Encoding.ASCII.GetBytes(ContentTypeGrpc);
    private static readonly byte[] s_teTrailersBytes = Encoding.ASCII.GetBytes(TeTrailers);
    private static readonly byte[] s_status200Bytes = Encoding.ASCII.GetBytes("200");

    // gRPC status codes are a fixed enum 0..16; pre-compute the ASCII
    // byte[] for each so EncodeTrailers does not call int.ToString +
    // Encoding.ASCII.GetBytes on every successful RPC (the OK=0 path is
    // the dominant case).
    private static readonly byte[][] s_grpcStatusBytes = BuildGrpcStatusTable();

    private static byte[][] BuildGrpcStatusTable()
    {
        // StatusCode enum spans 0..16 (OK through Unauthenticated).
        var table = new byte[17][];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture));
        }
        return table;
    }

    private static byte[] GetGrpcStatusBytes(StatusCode code)
    {
        var i = (int)code;
        if ((uint)i < (uint)s_grpcStatusBytes.Length)
        {
            return s_grpcStatusBytes[i];
        }
        // Defensive fallback for future enum extensions / out-of-range values.
        return Encoding.ASCII.GetBytes(i.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Returns a lowercase rendering of <paramref name="key"/> without
    /// allocating when the string is already entirely lowercase. gRPC
    /// metadata key validation in <c>Grpc.Core.Metadata.Entry</c> already
    /// enforces lowercase, so in practice this fast-path hits ~100 % and
    /// avoids one string alloc per metadata entry per header frame.
    /// </summary>
    private static string ToHeaderName(string key)
    {
        for (int i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (c >= 'A' && c <= 'Z')
            {
                return key.ToLowerInvariant();
            }
        }
        return key;
    }

    /// <summary>
    /// Returns true if a gRPC header name marks a binary metadata header
    /// (gRFC G2 / gRPC-over-HTTP/2 spec): keys ending in <c>-bin</c> carry
    /// arbitrary <see cref="byte"/>[] values.
    /// </summary>
    /// <remarks>
    /// On the wire (HTTP/2 / HPACK) such values MUST be base64-encoded
    /// (without padding is permitted; standard padding is what we emit
    /// for clarity). Our internal <see cref="HeadersV1"/>/
    /// <see cref="TrailersV1"/> models always carry the RAW bytes;
    /// base64 conversion happens exactly at the HPACK adapter boundary.
    /// <para>
    /// H2 wire MUST match the spec for cross-implementation interop and
    /// to avoid confusing tooling (Wireshark, gRPC tracing, debug proxies).
    /// </para>
    /// </remarks>
    private static bool IsBinaryHeader(string name)
        => name.EndsWith("-bin", StringComparison.Ordinal);

    /// <summary>
    /// Adds a raw metadata value to <paramref name="list"/>, base64-encoding
    /// when <paramref name="name"/> is a binary header per
    /// <see cref="IsBinaryHeader"/>.
    /// </summary>
    private static void AddMetadataValue(
        List<(string Name, byte[] Value)> list, string name, byte[] rawValue)
    {
        if (IsBinaryHeader(name))
        {
            // Emit standard base64 (with padding). RFC 7541 HPACK accepts
            // arbitrary bytes; gRFC G2 requires base64 specifically — the
            // peer will base64-decode on receive.
            var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(rawValue));
            list.Add((name, encoded));
        }
        else
        {
            list.Add((name, rawValue));
        }
    }

    /// <summary>
    /// Materialises a metadata value from a decoded HPACK header value,
    /// base64-decoding when <paramref name="name"/> is a binary header.
    /// </summary>
    /// <remarks>
    /// Tolerates malformed base64 by treating the value as raw bytes and
    /// surfacing it to the upper layer; this matches grpc-go's lenient
    /// behaviour (a strict <see cref="FormatException"/> here would tear
    /// down the connection on a single bad header).
    /// </remarks>
    private static byte[] DecodeMetadataValue(string name, byte[] hpackValue)
    {
        if (!IsBinaryHeader(name))
        {
            return hpackValue;
        }
        try
        {
            // gRFC G2 also permits base64 WITHOUT padding (URL-safe variant
            // is NOT used; standard alphabet only). Pad if needed before
            // decoding so peers that omitted padding still parse.
            var asciiText = Encoding.ASCII.GetString(hpackValue);
            var padded = asciiText.Length % 4 == 0
                ? asciiText
                : asciiText + new string('=', 4 - (asciiText.Length % 4));
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            // Malformed base64 — return raw bytes; upper layer can
            // observe corruption and reject if it cares.
            return hpackValue;
        }
    }

    /// <summary>
    /// Encodes a <see cref="HeadersV1"/> instance into an HPACK header block.
    /// Returns a pooled buffer that the caller must return to <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public static (byte[] Buffer, int Length) EncodeHeaders(HeadersV1 headers)
    {
        var list = new List<(string Name, byte[] Value)>(8 + headers.Metadata.Count);

        if (headers.HeaderType == 0)
        {
            // Client-initial: pseudo-headers required by gRPC over HTTP/2 (A4)
            list.Add((PseudoMethod, s_methodPostBytes));
            list.Add((PseudoScheme, s_schemeHttpBytes));
            list.Add((PseudoPath, Encoding.UTF8.GetBytes(headers.Method ?? "/")));
            if (!string.IsNullOrEmpty(headers.Authority))
            {
                list.Add((PseudoAuthority, Encoding.UTF8.GetBytes(headers.Authority!)));
            }
            list.Add((ContentType, s_contentTypeGrpcBytes));
            list.Add((TeHeader, s_teTrailersBytes));
            if (headers.DeadlineUnixNano != 0)
            {
                list.Add((GrpcTimeout, Encoding.ASCII.GetBytes(EncodeTimeout(headers.DeadlineUnixNano))));
            }
        }
        else
        {
            // Server-initial: :status only
            list.Add((PseudoStatus, s_status200Bytes));
            list.Add((ContentType, s_contentTypeGrpcBytes));
        }

        foreach (var kv in headers.Metadata)
        {
            var lowerName = ToHeaderName(kv.Key);
            foreach (var v in kv.Values)
            {
                AddMetadataValue(list, lowerName, v);
            }
        }

        return HpackEncoder.Encode(list);
    }

    /// <summary>
    /// Encodes a <see cref="TrailersV1"/> into an HPACK header block.
    /// </summary>
    public static (byte[] Buffer, int Length) EncodeTrailers(TrailersV1 trailers)
    {
        var list = new List<(string Name, byte[] Value)>(2 + trailers.Metadata.Count);
        list.Add((GrpcStatus, GetGrpcStatusBytes(trailers.GrpcStatusCode)));
        if (!string.IsNullOrEmpty(trailers.GrpcStatusMessage))
        {
            list.Add((GrpcMessage, Encoding.UTF8.GetBytes(GrpcMessageEncoder.Encode(trailers.GrpcStatusMessage!))));
        }
        foreach (var kv in trailers.Metadata)
        {
            var lowerName = ToHeaderName(kv.Key);
            foreach (var v in kv.Values)
            {
                AddMetadataValue(list, lowerName, v);
            }
        }

        return HpackEncoder.Encode(list);
    }

    /// <summary>
    /// Decodes an HPACK header block as a <see cref="HeadersV1"/> instance.
    /// </summary>
    public static HeadersV1 DecodeHeaders(ReadOnlySpan<byte> hpackBlock)
    {
        var headers = HpackDecoder.Decode(hpackBlock);
        byte headerType = 1; // server-initial unless we see :method or :path
        string? method = null;
        string? authority = null;
        ulong deadlineNs = 0;
        var metadata = new List<MetadataKV>();
        var grouped = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        foreach (var (name, value) in headers)
        {
            switch (name)
            {
                case PseudoMethod:
                    headerType = 0;
                    break;
                case PseudoScheme:
                    headerType = 0;
                    break;
                case PseudoPath:
                    headerType = 0;
                    method = Encoding.UTF8.GetString(value);
                    break;
                case PseudoAuthority:
                    authority = Encoding.UTF8.GetString(value);
                    break;
                case PseudoStatus:
                    // Implicit server-initial; no extra state needed
                    break;
                case ContentType:
                case TeHeader:
                    // Filter out HTTP/2 transport-only headers; the upper layers
                    // do not need to see them.
                    break;
                case GrpcTimeout:
                    deadlineNs = DecodeTimeout(Encoding.ASCII.GetString(value));
                    break;
                default:
                    if (name.StartsWith(":", StringComparison.Ordinal))
                    {
                        // Unknown pseudo-header — ignore for forward compatibility
                        break;
                    }
                    if (!grouped.TryGetValue(name, out var bucket))
                    {
                        bucket = new List<byte[]>();
                        grouped[name] = bucket;
                    }
                    bucket.Add(DecodeMetadataValue(name, value));
                    break;
            }
        }

        foreach (var (k, v) in grouped)
        {
            metadata.Add(new MetadataKV { Key = k, Values = v });
        }

        return new HeadersV1
        {
            Version = 1,
            HeaderType = headerType,
            Method = method,
            Authority = authority,
            DeadlineUnixNano = deadlineNs,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Decodes an HPACK header block as a <see cref="TrailersV1"/> instance.
    /// </summary>
    public static TrailersV1 DecodeTrailers(ReadOnlySpan<byte> hpackBlock)
    {
        var headers = HpackDecoder.Decode(hpackBlock);
        var status = StatusCode.OK;
        string? msg = null;
        var metadata = new List<MetadataKV>();
        var grouped = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        foreach (var (name, value) in headers)
        {
            switch (name)
            {
                case GrpcStatus:
                    {
                        var s = Encoding.ASCII.GetString(value);
                        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                        {
                            status = (StatusCode)code;
                        }
                        break;
                    }
                case GrpcMessage:
                    msg = GrpcMessageEncoder.Decode(Encoding.UTF8.GetString(value));
                    break;
                case PseudoStatus:
                case ContentType:
                    break;
                default:
                    if (name.StartsWith(":", StringComparison.Ordinal))
                    {
                        break;
                    }
                    if (!grouped.TryGetValue(name, out var bucket))
                    {
                        bucket = new List<byte[]>();
                        grouped[name] = bucket;
                    }
                    bucket.Add(DecodeMetadataValue(name, value));
                    break;
            }
        }

        foreach (var (k, v) in grouped)
        {
            metadata.Add(new MetadataKV { Key = k, Values = v });
        }

        return new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = status,
            GrpcStatusMessage = msg,
            Metadata = metadata,
        };
    }

    /// <summary>
    /// Decodes an HPACK header block that arrived as a "trailers-only" HEADERS
    /// frame (single frame carrying both response status pseudo-headers and
    /// gRPC trailing fields, with H2 END_STREAM set on first HEADERS — the
    /// canonical wire form for status-only gRPC responses such as NotFound).
    /// </summary>
    /// <remarks>
    /// gRFC G3 §"Trailers-only" defines this single-frame form as the way a
    /// server returns a non-OK status (or any status without a response body)
    /// over HTTP/2. The receiving codec MUST surface the same logical pair —
    /// initial-headers followed by trailers — that the upper layer would see
    /// for a multi-frame response, otherwise the client's response-handling
    /// state machine never observes a Trailers frame and the call hangs.
    /// <para>
    /// This method decodes the HPACK block once and partitions the fields:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Initial headers</b>: <c>:status</c>,
    ///     <c>content-type</c>, <c>grpc-encoding</c>, <c>grpc-accept-encoding</c>,
    ///     <c>te</c> — i.e., transport-/encoding-level fields the client
    ///     handler expects to see before any application data.</description></item>
    ///   <item><description><b>Trailers</b>: <c>grpc-status</c>,
    ///     <c>grpc-message</c>, <c>grpc-status-details-bin</c>, and any
    ///     custom application metadata. By gRFC convention, custom metadata
    ///     in a trailers-only block belongs in the trailers half (the only
    ///     half a status-only response semantically owns).</description></item>
    /// </list>
    /// </remarks>
    public static (HeadersV1 Headers, TrailersV1 Trailers) DecodeTrailersOnly(ReadOnlySpan<byte> hpackBlock)
    {
        var fields = HpackDecoder.Decode(hpackBlock);

        // Initial-headers half: server-initial style HeadersV1 (HeaderType = 1).
        // Custom application metadata goes to trailers per the partition rule
        // above, so the metadata list here stays empty.
        var headers = new HeadersV1
        {
            Version = 1,
            HeaderType = 1,
            Metadata = Array.Empty<MetadataKV>(),
        };

        // Trailers half: built up below.
        var status = StatusCode.OK;
        string? msg = null;
        var grouped = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case PseudoStatus:
                case ContentType:
                case TeHeader:
                    // Belong in initial headers; the upper layer reconstructs
                    // them from HeadersV1's transport fields when present, so
                    // we don't surface them as metadata.
                    break;
                case GrpcStatus:
                    {
                        var s = Encoding.ASCII.GetString(value);
                        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                        {
                            status = (StatusCode)code;
                        }
                        break;
                    }
                case GrpcMessage:
                    msg = GrpcMessageEncoder.Decode(Encoding.UTF8.GetString(value));
                    break;
                default:
                    if (name.StartsWith(":", StringComparison.Ordinal))
                    {
                        // Unknown pseudo-header — ignore for forward
                        // compatibility (mirrors DecodeHeaders).
                        break;
                    }
                    if (!grouped.TryGetValue(name, out var bucket))
                    {
                        bucket = new List<byte[]>();
                        grouped[name] = bucket;
                    }
                    bucket.Add(DecodeMetadataValue(name, value));
                    break;
            }
        }

        var trailerMeta = new List<MetadataKV>(grouped.Count);
        foreach (var (k, v) in grouped)
        {
            trailerMeta.Add(new MetadataKV { Key = k, Values = v });
        }

        var trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = status,
            GrpcStatusMessage = msg,
            Metadata = trailerMeta,
        };
        return (headers, trailers);
    }

    /// <summary>
    /// Encodes a deadline (in Unix nanoseconds) as a gRPC <c>grpc-timeout</c> value.
    /// Picks the smallest unit so the integer fits the 8-digit limit (gRFC A4).
    /// </summary>
    private static string EncodeTimeout(ulong unixNanos)
    {
        // We don't have "now"; the caller stored the deadline as Unix nanoseconds.
        // Convert to a positive remaining duration relative to now.
        var nowNs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
        long remainingNs = unixNanos > nowNs ? (long)(unixNanos - nowNs) : 0;
        if (remainingNs <= 0)
        {
            return "1n"; // 1 nanosecond — effectively expired
        }

        // Pick smallest unit with numeric value < 100_000_000 (8 digits per A4)
        if (remainingNs < 100_000_000) return remainingNs.ToString(CultureInfo.InvariantCulture) + "n";
        var us = remainingNs / 1_000;
        if (us < 100_000_000) return us.ToString(CultureInfo.InvariantCulture) + "u";
        var ms = remainingNs / 1_000_000;
        if (ms < 100_000_000) return ms.ToString(CultureInfo.InvariantCulture) + "m";
        var s = remainingNs / 1_000_000_000L;
        if (s < 100_000_000) return s.ToString(CultureInfo.InvariantCulture) + "S";
        var min = s / 60;
        if (min < 100_000_000) return min.ToString(CultureInfo.InvariantCulture) + "M";
        var hour = min / 60;
        return hour.ToString(CultureInfo.InvariantCulture) + "H";
    }

    /// <summary>
    /// Decodes a gRPC <c>grpc-timeout</c> value back to a Unix-nanosecond deadline.
    /// </summary>
    private static ulong DecodeTimeout(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var unit = value[^1];
        if (!long.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }
        long durationNs = unit switch
        {
            'n' => n,
            'u' => n * 1_000L,
            'm' => n * 1_000_000L,
            'S' => n * 1_000_000_000L,
            'M' => n * 60L * 1_000_000_000L,
            'H' => n * 3600L * 1_000_000_000L,
            _ => 0L,
        };
        if (durationNs <= 0) return 0;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (ulong)(nowMs * 1_000_000L + durationNs);
    }
}

/// <summary>
/// Percent-encoder for <c>grpc-message</c> per gRFC G-1 (HTTP/2 header value
/// must be ASCII; non-printable / non-ASCII bytes are percent-escaped).
/// </summary>
internal static class GrpcMessageEncoder
{
    public static string Encode(string message)
    {
        var sb = new StringBuilder(message.Length);
        var bytes = Encoding.UTF8.GetBytes(message);
        foreach (var b in bytes)
        {
            if (b < 0x20 || b >= 0x7F || b == '%')
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append((char)b);
            }
        }
        return sb.ToString();
    }

    public static string Decode(string encoded)
    {
        var bytes = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '%' && i + 2 < encoded.Length)
            {
                if (byte.TryParse(encoded.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    bytes.Add(b);
                    i += 2;
                    continue;
                }
            }
            bytes.Add((byte)c);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
