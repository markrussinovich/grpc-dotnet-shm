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

namespace Grpc.Net.SharedMemory.Wire;

/// <summary>
/// HTTP/2 frame types (RFC 7540 §11.2).
/// </summary>
internal enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
}

/// <summary>
/// HTTP/2 frame flags (RFC 7540 §6).
/// </summary>
internal static class Http2Flags
{
    // DATA
    public const byte EndStream = 0x1;
    public const byte Padded = 0x8;

    // HEADERS
    // EndStream = 0x1 (shared with DATA)
    public const byte EndHeaders = 0x4;
    // Padded = 0x8 (shared with DATA)
    public const byte Priority = 0x20;

    // SETTINGS
    public const byte Ack = 0x1;

    // PING
    // Ack = 0x1 (shared with SETTINGS)
}

/// <summary>
/// HTTP/2 error codes (RFC 7540 §7).
/// </summary>
internal enum Http2ErrorCode : uint
{
    NoError = 0x0,
    ProtocolError = 0x1,
    InternalError = 0x2,
    FlowControlError = 0x3,
    SettingsTimeout = 0x4,
    StreamClosed = 0x5,
    FrameSizeError = 0x6,
    RefusedStream = 0x7,
    Cancel = 0x8,
    CompressionError = 0x9,
    ConnectError = 0xA,
    EnhanceYourCalm = 0xB,
    InadequateSecurity = 0xC,
    Http11Required = 0xD,
}

/// <summary>
/// HTTP/2 SETTINGS parameter identifiers (RFC 7540 §6.5.2).
/// </summary>
internal enum Http2SettingsParameter : ushort
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6,
}
