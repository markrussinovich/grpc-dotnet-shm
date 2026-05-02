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

namespace Grpc.Net.SharedMemory.Wire.Hpack;

/// <summary>
/// HPACK static table (RFC 7541 Appendix A).
/// 1-based indices; index 0 is invalid.
/// </summary>
internal static class HpackStaticTable
{
    /// <summary>Total entries in the static table (RFC 7541 has 61).</summary>
    public const int Count = 61;

    /// <summary>Static table entries. Index 0 is unused (HPACK indices are 1-based).</summary>
    public static readonly (string Name, string Value)[] Entries = new (string, string)[]
    {
        ("",                              ""),                 // 0 (placeholder)
        (":authority",                    ""),                 // 1
        (":method",                       "GET"),              // 2
        (":method",                       "POST"),             // 3
        (":path",                         "/"),                // 4
        (":path",                         "/index.html"),      // 5
        (":scheme",                       "http"),             // 6
        (":scheme",                       "https"),            // 7
        (":status",                       "200"),              // 8
        (":status",                       "204"),              // 9
        (":status",                       "206"),              // 10
        (":status",                       "304"),              // 11
        (":status",                       "400"),              // 12
        (":status",                       "404"),              // 13
        (":status",                       "500"),              // 14
        ("accept-charset",                ""),                 // 15
        ("accept-encoding",               "gzip, deflate"),    // 16
        ("accept-language",               ""),                 // 17
        ("accept-ranges",                 ""),                 // 18
        ("accept",                        ""),                 // 19
        ("access-control-allow-origin",   ""),                 // 20
        ("age",                           ""),                 // 21
        ("allow",                         ""),                 // 22
        ("authorization",                 ""),                 // 23
        ("cache-control",                 ""),                 // 24
        ("content-disposition",           ""),                 // 25
        ("content-encoding",              ""),                 // 26
        ("content-language",              ""),                 // 27
        ("content-length",                ""),                 // 28
        ("content-location",              ""),                 // 29
        ("content-range",                 ""),                 // 30
        ("content-type",                  ""),                 // 31
        ("cookie",                        ""),                 // 32
        ("date",                          ""),                 // 33
        ("etag",                          ""),                 // 34
        ("expect",                        ""),                 // 35
        ("expires",                       ""),                 // 36
        ("from",                          ""),                 // 37
        ("host",                          ""),                 // 38
        ("if-match",                      ""),                 // 39
        ("if-modified-since",             ""),                 // 40
        ("if-none-match",                 ""),                 // 41
        ("if-range",                      ""),                 // 42
        ("if-unmodified-since",           ""),                 // 43
        ("last-modified",                 ""),                 // 44
        ("link",                          ""),                 // 45
        ("location",                      ""),                 // 46
        ("max-forwards",                  ""),                 // 47
        ("proxy-authenticate",            ""),                 // 48
        ("proxy-authorization",           ""),                 // 49
        ("range",                         ""),                 // 50
        ("referer",                       ""),                 // 51
        ("refresh",                       ""),                 // 52
        ("retry-after",                   ""),                 // 53
        ("server",                        ""),                 // 54
        ("set-cookie",                    ""),                 // 55
        ("strict-transport-security",     ""),                 // 56
        ("transfer-encoding",             ""),                 // 57
        ("user-agent",                    ""),                 // 58
        ("vary",                          ""),                 // 59
        ("via",                           ""),                 // 60
        ("www-authenticate",              ""),                 // 61
    };

    // Pre-built name-only and exact-match indexes for fast encoder lookup.
    // Computed lazily on first use.
    private static Dictionary<string, int>? s_nameOnlyIndex;
    private static Dictionary<(string Name, string Value), int>? s_exactIndex;

    private static Dictionary<string, int> NameOnlyIndex
    {
        get
        {
            if (s_nameOnlyIndex == null)
            {
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 1; i <= Count; i++)
                {
                    var name = Entries[i].Name;
                    if (!d.ContainsKey(name))
                    {
                        d[name] = i;
                    }
                }
                s_nameOnlyIndex = d;
            }
            return s_nameOnlyIndex;
        }
    }

    private static Dictionary<(string, string), int> ExactIndex
    {
        get
        {
            if (s_exactIndex == null)
            {
                var d = new Dictionary<(string, string), int>();
                for (var i = 1; i <= Count; i++)
                {
                    d[(Entries[i].Name, Entries[i].Value)] = i;
                }
                s_exactIndex = d;
            }
            return s_exactIndex;
        }
    }

    /// <summary>Returns the static-table index for an exact name+value match, or 0.</summary>
    public static int FindExact(string name, string value)
    {
        return ExactIndex.TryGetValue((name, value), out var idx) ? idx : 0;
    }

    /// <summary>Returns the static-table index for any entry with the given name, or 0.</summary>
    public static int FindName(string name)
    {
        return NameOnlyIndex.TryGetValue(name, out var idx) ? idx : 0;
    }
}
