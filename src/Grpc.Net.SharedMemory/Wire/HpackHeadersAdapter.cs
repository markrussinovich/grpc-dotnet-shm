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
            list.Add((PseudoMethod, Encoding.ASCII.GetBytes("POST")));
            list.Add((PseudoScheme, Encoding.ASCII.GetBytes("http")));
            list.Add((PseudoPath, Encoding.UTF8.GetBytes(headers.Method ?? "/")));
            if (!string.IsNullOrEmpty(headers.Authority))
            {
                list.Add((PseudoAuthority, Encoding.UTF8.GetBytes(headers.Authority!)));
            }
            list.Add((ContentType, Encoding.ASCII.GetBytes(ContentTypeGrpc)));
            list.Add((TeHeader, Encoding.ASCII.GetBytes(TeTrailers)));
            if (headers.DeadlineUnixNano != 0)
            {
                list.Add((GrpcTimeout, Encoding.ASCII.GetBytes(EncodeTimeout(headers.DeadlineUnixNano))));
            }
        }
        else
        {
            // Server-initial: :status only
            list.Add((PseudoStatus, Encoding.ASCII.GetBytes("200")));
            list.Add((ContentType, Encoding.ASCII.GetBytes(ContentTypeGrpc)));
        }

        foreach (var kv in headers.Metadata)
        {
            var lowerName = kv.Key.ToLowerInvariant();
            foreach (var v in kv.Values)
            {
                list.Add((lowerName, v));
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
        list.Add((GrpcStatus, Encoding.ASCII.GetBytes(((int)trailers.GrpcStatusCode).ToString(CultureInfo.InvariantCulture))));
        if (!string.IsNullOrEmpty(trailers.GrpcStatusMessage))
        {
            list.Add((GrpcMessage, Encoding.UTF8.GetBytes(GrpcMessageEncoder.Encode(trailers.GrpcStatusMessage!))));
        }
        foreach (var kv in trailers.Metadata)
        {
            var lowerName = kv.Key.ToLowerInvariant();
            foreach (var v in kv.Values)
            {
                list.Add((lowerName, v));
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
                    bucket.Add(value);
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
                    bucket.Add(value);
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
