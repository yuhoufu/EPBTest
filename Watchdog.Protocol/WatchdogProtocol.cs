using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Integrity envelope for the newline-delimited named-pipe transport.
    /// New peers emit a length + SHA-256 frame; legacy JSON lines remain
    /// readable during a rolling upgrade.  A partial EOF or checksum mismatch
    /// is never passed to the JSON parser as if it were a complete message.
    /// </summary>
    public static class WatchdogWireFrame
    {
        private const string Prefix = "WDG4|";
        public const int MaximumPayloadBytes = 4 * 1024 * 1024;

        public static string Encode(string payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var bytes = new UTF8Encoding(false).GetBytes(payload);
            if (bytes.Length <= 0 || bytes.Length > MaximumPayloadBytes)
                throw new InvalidOperationException("WatchdogFramePayloadLengthInvalid");
            return Prefix + bytes.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                   ComputeSha256(bytes) + "|" + Convert.ToBase64String(bytes);
        }

        public static bool TryDecode(
            string wire,
            out string payload,
            out string failure)
        {
            payload = null;
            failure = null;
            if (string.IsNullOrWhiteSpace(wire))
            {
                failure = "FrameEmpty";
                return false;
            }
            if (!wire.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // Backward-compatible v4 JSON line.  Non-JSON text is rejected
                // here rather than being ambiguously classified downstream.
                if (wire.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    payload = wire;
                    return true;
                }
                failure = "FramePrefixMissing";
                return false;
            }

            var lengthEnd = wire.IndexOf('|', Prefix.Length);
            var hashEnd = lengthEnd < 0 ? -1 : wire.IndexOf('|', lengthEnd + 1);
            if (lengthEnd < 0 || hashEnd < 0)
            {
                failure = "FrameHeaderIncomplete";
                return false;
            }
            if (!int.TryParse(
                    wire.Substring(Prefix.Length, lengthEnd - Prefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var expectedLength) ||
                expectedLength <= 0 || expectedLength > MaximumPayloadBytes)
            {
                failure = "FrameLengthInvalid";
                return false;
            }
            var expectedHash = wire.Substring(lengthEnd + 1, hashEnd - lengthEnd - 1);
            if (expectedHash.Length != 64)
            {
                failure = "FrameHashInvalid";
                return false;
            }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(wire.Substring(hashEnd + 1)); }
            catch (FormatException)
            {
                failure = "FramePayloadIncomplete";
                return false;
            }
            if (bytes.Length != expectedLength)
            {
                failure = "FrameLengthMismatch";
                return false;
            }
            if (!FixedTimeEquals(expectedHash, ComputeSha256(bytes)))
            {
                failure = "FrameChecksumMismatch";
                return false;
            }
            payload = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++)
                difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }

    public static class WatchdogProtocol
    {
        // V4 adds durable pause/closing evidence and bounded safety handoff.
        // Safety messages are exact-version contracts: a peer must not infer
        // missing v4 fields from an older payload.
        public const int Version = 7;
        public const int MinimumCompatibleVersion = 7;
        public static string Serialize(WatchdogMessage message)
        {
            var json = new JavaScriptSerializer();
            if (message != null && string.Equals(message.Type, WatchdogMessageType.RecoveryAttemptFailedReceipt, StringComparison.Ordinal))
            {
                // Failure receipts have an intentionally tiny whitelist.  A
                // recovery receipt is not an Attached/session/launch message;
                // serializing the whole WatchdogMessage would leak unrelated
                // payload and permit identity fields.
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ProtocolVersion"] = message.ProtocolVersion,
                    ["Type"] = message.Type,
                    ["SessionId"] = message.SessionId,
                    ["CorrelationId"] = message.CorrelationId,
                    ["RecoveryFailureReceipt"] = message.RecoveryFailureReceipt?.Clone()
                };
                return json.Serialize(values);
            }
            if (message != null && string.Equals(message.Type, WatchdogMessageType.RecoveryAttemptFailed, StringComparison.Ordinal))
            {
                // This request is durable authority input.  Serialize only
                // its exact v6 shape so the host can hash the accepted raw
                // bytes and a retry can reproduce the same operation.
                var values = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["ProtocolVersion"] = message.ProtocolVersion,
                    ["Type"] = message.Type,
                    ["SessionId"] = message.SessionId ?? string.Empty,
                    ["CorrelationId"] = message.CorrelationId ?? string.Empty,
                    ["Reason"] = message.Reason ?? string.Empty,
                    ["RecoveryFailureCode"] = message.RecoveryFailureCode ?? string.Empty,
                    ["RecoveryFailurePermanent"] = message.RecoveryFailurePermanent,
                    ["RecoveryFailureDetail"] = message.RecoveryFailureDetail ?? string.Empty,
                    ["RecoveryFailureContextSha256"] = message.RecoveryFailureContextSha256 ?? string.Empty,
                    ["RecoveryFailureOwner"] = message.RecoveryFailureOwner ?? string.Empty,
                    ["RecoveryFailureCorrelationId"] = message.RecoveryFailureCorrelationId ?? string.Empty,
                    ["RootCode"] = message.RootCode ?? string.Empty,
                    ["DeviceOrChannelGroup"] = message.DeviceOrChannelGroup ?? string.Empty,
                    ["RunId"] = message.RunId ?? string.Empty,
                    ["RecoveryStage"] = message.RecoveryStage ?? string.Empty,
                    ["RecoveryProgressToken"] = message.RecoveryProgressToken ?? string.Empty,
                    ["RecoveryProcessSource"] = message.RecoveryProcessSource ?? string.Empty,
                    ["RecoveryFailureFingerprint"] = message.RecoveryFailureFingerprint ?? string.Empty
                };
                return json.Serialize(values);
            }
            return json.Serialize(message);
        }
        public static WatchdogMessage Deserialize(string value)
        {
            if (!WatchdogWireFrame.TryDecode(value, out var payload, out var failure))
                throw new InvalidOperationException("WatchdogFrameRejected:" + failure);
            return new JavaScriptSerializer().Deserialize<WatchdogMessage>(payload);
        }

        public static string ComputeWireSha256(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(
                        sha.ComputeHash(new UTF8Encoding(false).GetBytes(value)))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
        }

        public static bool TryPeekWireMessageType(string json, out string messageType)
        {
            messageType = null;
            if (string.IsNullOrWhiteSpace(json)) return false;
            if (!WatchdogWireFrame.TryDecode(json, out json, out _)) return false;
            try
            {
                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = Math.Min(16 * 1024 * 1024,
                        Math.Max(1024, json.Length + 16))
                };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                return TryString(root, "Type", out messageType);
            }
            catch { return false; }
        }

        /// <summary>
        /// Exact v4 parser for the only client message which can mutate the
        /// durable relaunch authority.  Unknown keys, missing fields and
        /// launch/session payloads are rejected before DTO deserialization.
        /// The returned hash covers the exact accepted UTF-8 line.
        /// </summary>
        public static bool TryParseRecoveryFailureRequestWire(
            string json,
            string expectedSessionId,
            out WatchdogMessage message,
            out string requestPayloadSha256,
            out string reason)
        {
            message = null;
            requestPayloadSha256 = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(json)) { reason = "JsonMissing"; return false; }
            try
            {
                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = Math.Min(16 * 1024 * 1024,
                        Math.Max(1024, json.Length + 16))
                };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) { reason = "JsonRoot"; return false; }
                var allowed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "ProtocolVersion", "Type", "SessionId", "CorrelationId",
                    "Reason", "RecoveryFailureCode", "RecoveryFailurePermanent",
                    "RecoveryFailureDetail", "RecoveryFailureContextSha256",
                    "RecoveryFailureOwner", "RecoveryFailureCorrelationId",
                    "RootCode", "DeviceOrChannelGroup", "RunId", "RecoveryStage",
                    "RecoveryProgressToken", "RecoveryProcessSource",
                    "RecoveryFailureFingerprint"
                };
                if (root.Count != allowed.Count || root.Keys.Any(key => !allowed.Contains(key)))
                { reason = "RequestShape"; return false; }

                int protocol;
                bool permanent;
                string type, session, correlation, prose, code, detail, contextSha,
                    owner, takeoverCorrelation, rootCode, group, runId, stage,
                    progress, source, fingerprint;
                if (!TryInt(root, "ProtocolVersion", out protocol) ||
                    !TryString(root, "Type", out type) ||
                    !TryString(root, "SessionId", out session) ||
                    !TryString(root, "CorrelationId", out correlation) ||
                    !TryString(root, "Reason", out prose) ||
                    !TryString(root, "RecoveryFailureCode", out code) ||
                    !TryBool(root, "RecoveryFailurePermanent", out permanent) ||
                    !TryString(root, "RecoveryFailureDetail", out detail) ||
                    !TryString(root, "RecoveryFailureContextSha256", out contextSha) ||
                    !TryString(root, "RecoveryFailureOwner", out owner) ||
                    !TryString(root, "RecoveryFailureCorrelationId", out takeoverCorrelation) ||
                    !TryString(root, "RootCode", out rootCode) ||
                    !TryString(root, "DeviceOrChannelGroup", out group) ||
                    !TryString(root, "RunId", out runId) ||
                    !TryString(root, "RecoveryStage", out stage) ||
                    !TryString(root, "RecoveryProgressToken", out progress) ||
                    !TryString(root, "RecoveryProcessSource", out source) ||
                    !TryString(root, "RecoveryFailureFingerprint", out fingerprint))
                { reason = "RequestFieldType"; return false; }
                if (!IsSupportedVersion(protocol) ||
                    !string.Equals(type, WatchdogMessageType.RecoveryAttemptFailed, StringComparison.Ordinal))
                { reason = "RequestProtocolOrType"; return false; }
                if (!IsCanonicalGuidN(session) ||
                    (!string.IsNullOrEmpty(expectedSessionId) &&
                     !string.Equals(session, expectedSessionId, StringComparison.Ordinal)) ||
                    !IsCanonicalGuidN(correlation))
                { reason = "RequestIdentity"; return false; }
                if (!IsRequiredToken(code) || !IsRequiredToken(rootCode) ||
                    !IsRequiredToken(group) || !IsRequiredToken(runId) ||
                    !IsRequiredToken(stage) || !IsRequiredToken(progress) ||
                    !IsRequiredToken(source) || !IsRequiredToken(fingerprint))
                { reason = "RequestEvidence"; return false; }
                if ((!string.IsNullOrEmpty(contextSha) && !RecoveryFailureReceipt.IsSha256(contextSha)) ||
                    !IsDiagnosticText(prose) || !IsDiagnosticText(detail))
                { reason = "RequestDiagnostic"; return false; }
                var takeover = string.Equals(owner, "WatchdogTakeover", StringComparison.Ordinal);
                if (takeover != IsCanonicalGuidN(takeoverCorrelation) ||
                    (!takeover && (!string.IsNullOrEmpty(owner) ||
                                   !string.IsNullOrEmpty(takeoverCorrelation))))
                { reason = "RequestTakeoverIdentity"; return false; }

                message = new WatchdogMessage
                {
                    ProtocolVersion = protocol,
                    Type = type,
                    SessionId = session,
                    CorrelationId = correlation,
                    Reason = prose,
                    RecoveryFailureCode = code,
                    RecoveryFailurePermanent = permanent,
                    RecoveryFailureDetail = detail,
                    RecoveryFailureContextSha256 = contextSha,
                    RecoveryFailureOwner = owner,
                    RecoveryFailureCorrelationId = takeoverCorrelation,
                    RootCode = rootCode,
                    DeviceOrChannelGroup = group,
                    RunId = runId,
                    RecoveryStage = stage,
                    RecoveryProgressToken = progress,
                    RecoveryProcessSource = source,
                    RecoveryFailureFingerprint = fingerprint
                };
                requestPayloadSha256 = ComputeWireSha256(json);
                return true;
            }
            catch (Exception ex)
            {
                message = null;
                requestPayloadSha256 = null;
                reason = "JsonParse:" + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// The sole future wire entry point for a durable failure receipt.
        /// It enumerates the raw JSON object and validates key names/types
        /// before any permissive DTO deserialization can occur.  Unknown
        /// fields, launch-only fields, and default-valued extras are rejected
        /// rather than silently ignored.
        /// </summary>
        public static bool TryParseRecoveryFailureReceiptWire(
            string json,
            string expectedSessionId,
            string expectedCorrelationId,
            string expectedRequestPayloadSha256,
            out WatchdogMessage message,
            out RecoveryFailureReceipt receipt,
            out string reason)
        {
            message = null;
            receipt = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(json)) { reason = "JsonMissing"; return false; }
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = Math.Min(16 * 1024 * 1024, Math.Max(1024, json.Length + 16)) };
                var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) { reason = "JsonRoot"; return false; }
                var allowed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "ProtocolVersion", "Type", "SessionId", "CorrelationId", "RecoveryFailureReceipt"
                };
                if (root.Keys.Any(key => !allowed.Contains(key))) { reason = "UnknownTopLevelKey"; return false; }
                int protocol;
                string type, session, correlation;
                Dictionary<string, object> nested;
                if (!TryInt(root, "ProtocolVersion", out protocol) ||
                    !TryString(root, "Type", out type) ||
                    !TryString(root, "SessionId", out session) ||
                    !TryString(root, "CorrelationId", out correlation) ||
                    !TryObject(root, "RecoveryFailureReceipt", out nested))
                { reason = "TopLevelType"; return false; }
                var nestedAllowed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "RequestCorrelationId", "RequestPayloadSha256", "FailureCode", "FailureFingerprint",
                    "Disposition", "Durable", "PermitClosed", "FailureRegistered", "CircuitOpen",
                    "ConsecutiveCount", "RelaunchPermitGeneration", "DecisionSequence", "DecisionUtcTicks",
                    "DetailCode"
                };
                if (nested.Keys.Any(key => !nestedAllowed.Contains(key))) { reason = "UnknownReceiptKey"; return false; }
                string requestCorrelationId;
                string requestPayloadSha256;
                string failureCode;
                string failureFingerprint;
                string disposition;
                string detailCode;
                bool durable;
                bool permitClosed;
                bool failureRegistered;
                bool circuitOpen;
                int consecutiveCount;
                long relaunchPermitGeneration;
                long decisionSequence;
                long decisionUtcTicks;
                if (!TryString(nested, "RequestCorrelationId", out requestCorrelationId) ||
                    !TryString(nested, "RequestPayloadSha256", out requestPayloadSha256) ||
                    !TryString(nested, "FailureCode", out failureCode) ||
                    !TryString(nested, "FailureFingerprint", out failureFingerprint) ||
                    !TryString(nested, "Disposition", out disposition) ||
                    !TryBool(nested, "Durable", out durable) ||
                    !TryBool(nested, "PermitClosed", out permitClosed) ||
                    !TryBool(nested, "FailureRegistered", out failureRegistered) ||
                    !TryBool(nested, "CircuitOpen", out circuitOpen) ||
                    !TryInt(nested, "ConsecutiveCount", out consecutiveCount) ||
                    !TryLong(nested, "RelaunchPermitGeneration", out relaunchPermitGeneration) ||
                    !TryLong(nested, "DecisionSequence", out decisionSequence) ||
                    !TryLong(nested, "DecisionUtcTicks", out decisionUtcTicks) ||
                    !TryString(nested, "DetailCode", out detailCode))
                { reason = "ReceiptType"; return false; }
                var parsed = new RecoveryFailureReceipt
                {
                    RequestCorrelationId = requestCorrelationId,
                    RequestPayloadSha256 = requestPayloadSha256,
                    FailureCode = failureCode,
                    FailureFingerprint = failureFingerprint,
                    Disposition = disposition,
                    Durable = durable,
                    PermitClosed = permitClosed,
                    FailureRegistered = failureRegistered,
                    CircuitOpen = circuitOpen,
                    ConsecutiveCount = consecutiveCount,
                    RelaunchPermitGeneration = relaunchPermitGeneration,
                    DecisionSequence = decisionSequence,
                    DecisionUtcTicks = decisionUtcTicks,
                    DetailCode = detailCode
                };
                message = new WatchdogMessage
                {
                    ProtocolVersion = protocol, Type = type, SessionId = session,
                    CorrelationId = correlation, RecoveryFailureReceipt = parsed
                };
                var valid = expectedSessionId == null && expectedCorrelationId == null && expectedRequestPayloadSha256 == null
                    ? TryValidateRecoveryFailureReceipt(message, out receipt, out reason)
                    : TryValidateRecoveryFailureReceipt(message, expectedSessionId, expectedCorrelationId,
                        expectedRequestPayloadSha256, out receipt, out reason);
                if (!valid)
                {
                    message = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                message = null; receipt = null; reason = "JsonParse:" + ex.GetType().Name;
                return false;
            }
        }

        public static bool TryParseRecoveryFailureReceiptWire(
            string json,
            out WatchdogMessage message,
            out RecoveryFailureReceipt receipt,
            out string reason)
        {
            return TryParseRecoveryFailureReceiptWire(json, null, null, null,
                out message, out receipt, out reason);
        }

        private static bool TryObject(Dictionary<string, object> map, string key, out Dictionary<string, object> value)
        {
            value = null;
            return map != null && map.ContainsKey(key) && map[key] is Dictionary<string, object> &&
                   (value = (Dictionary<string, object>)map[key]) != null;
        }

        private static bool TryString(Dictionary<string, object> map, string key, out string value)
        {
            value = null;
            if (map == null || !map.ContainsKey(key) || !(map[key] is string)) return false;
            value = (string)map[key];
            return true;
        }

        private static bool TryBool(Dictionary<string, object> map, string key, out bool value)
        {
            value = false;
            if (map == null || !map.ContainsKey(key) || !(map[key] is bool)) return false;
            value = (bool)map[key];
            return true;
        }

        private static bool TryInt(Dictionary<string, object> map, string key, out int value)
        {
            value = 0;
            if (map == null || !map.ContainsKey(key)) return false;
            if (map[key] is int) { value = (int)map[key]; return true; }
            if (map[key] is long && (long)map[key] >= int.MinValue && (long)map[key] <= int.MaxValue)
            { value = (int)(long)map[key]; return true; }
            return false;
        }

        private static bool TryLong(Dictionary<string, object> map, string key, out long value)
        {
            value = 0;
            if (map == null || !map.ContainsKey(key)) return false;
            if (map[key] is long) { value = (long)map[key]; return true; }
            if (map[key] is int) { value = (int)map[key]; return true; }
            return false;
        }

        /// <summary>
        /// Validates the structured failure receipt at the protocol boundary.
        /// The outer session/correlation are authoritative; the receipt never
        /// carries or reconstructs a permit nonce.
        /// </summary>
        public static bool TryValidateRecoveryFailureReceipt(
            WatchdogMessage message,
            out RecoveryFailureReceipt receipt,
            out string reason)
        {
            receipt = message?.RecoveryFailureReceipt?.Clone();
            reason = null;
            if (message == null) { reason = "MessageMissing"; return false; }
            if (!IsSupportedVersion(message.ProtocolVersion)) { reason = "ProtocolVersion"; return false; }
            if (!string.Equals(message.Type, WatchdogMessageType.RecoveryAttemptFailedReceipt, StringComparison.Ordinal))
            { reason = "MessageType"; return false; }
            if (!IsCanonicalGuidN(message.SessionId)) { reason = "SessionMissing"; return false; }
            if (!IsCanonicalGuidN(message.CorrelationId)) { reason = "CorrelationMissing"; return false; }
            if (receipt == null) { reason = "ReceiptMissing"; return false; }
            if (message.Session != null || message.Heartbeat != null || message.StopSummary != null ||
                message.CheckpointMirror != null || message.BatchStartFailure != null ||
                message.SafetyHandoff != null || message.ApplicationExit != null ||
                !string.IsNullOrEmpty(message.Reason) ||
                !string.IsNullOrEmpty(message.RecoveryFailureCode) || message.RecoveryFailurePermanent ||
                !string.IsNullOrEmpty(message.RecoveryFailureDetail) ||
                !string.IsNullOrEmpty(message.RecoveryFailureContextSha256) ||
                !string.IsNullOrEmpty(message.RecoveryFailureOwner) ||
                !string.IsNullOrEmpty(message.RecoveryFailureCorrelationId) ||
                !string.IsNullOrEmpty(message.RootCode) || !string.IsNullOrEmpty(message.RunId) ||
                !string.IsNullOrEmpty(message.RecoveryStage) || !string.IsNullOrEmpty(message.RecoveryProgressToken) ||
                !string.IsNullOrEmpty(message.RecoveryProcessSource) ||
                !string.IsNullOrEmpty(message.DeviceOrChannelGroup) ||
                !string.IsNullOrEmpty(message.RecoveryFailureFingerprint) ||
                !string.IsNullOrEmpty(message.SidecarAuthoritySessionId) || !string.IsNullOrEmpty(message.SidecarSessionId) ||
                message.SidecarProcessId != 0 || message.SidecarProcessStartUtcTicks != 0 ||
                message.SidecarStartUtcTicks != 0 || message.StartUtcTicks != 0 ||
                !string.IsNullOrEmpty(message.SidecarInstanceNonce) || !string.IsNullOrEmpty(message.InstanceNonce) ||
                message.RelaunchPermitGeneration != 0 || !string.IsNullOrEmpty(message.RelaunchPermitId) ||
                !string.IsNullOrEmpty(message.RelaunchPermitNonce) || message.AckSequence != 0 ||
                message.RecoveryCommitGeneration != 0)
            { reason = "UnrelatedPayloadPresent"; return false; }
            if (!RecoveryFailureReceipt.IsValidCorrelation(receipt.RequestCorrelationId) ||
                !RecoveryFailureReceipt.IsValidCorrelation(message.CorrelationId) ||
                !string.Equals(message.CorrelationId, receipt.RequestCorrelationId, StringComparison.Ordinal))
            { reason = "CorrelationMismatch"; return false; }
            if (!receipt.IsDecisionValid()) { reason = "ReceiptInvalid"; return false; }
            return true;
        }

        /// <summary>
        /// Strict authority validator.  The expected session/correlation are
        /// supplied by the already validated connection and compared ordinally;
        /// launch-only permit fields are forbidden on this message shape.
        /// </summary>
        public static bool TryValidateRecoveryFailureReceipt(
            WatchdogMessage message,
            string expectedSessionId,
            string expectedCorrelationId,
            out RecoveryFailureReceipt receipt,
            out string reason)
        {
            return TryValidateRecoveryFailureReceipt(message, expectedSessionId, expectedCorrelationId, null, out receipt, out reason);
        }

        public static bool TryValidateRecoveryFailureReceipt(
            WatchdogMessage message,
            string expectedSessionId,
            string expectedCorrelationId,
            string expectedRequestPayloadSha256,
            out RecoveryFailureReceipt receipt,
            out string reason)
        {
            if (!TryValidateRecoveryFailureReceipt(message, out receipt, out reason)) return false;
            Guid sessionGuid;
            if (!Guid.TryParseExact(expectedSessionId ?? string.Empty, "N", out sessionGuid) ||
                !string.Equals(expectedSessionId, sessionGuid.ToString("N"), StringComparison.Ordinal) ||
                !string.Equals(message.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                !Guid.TryParseExact(expectedCorrelationId ?? string.Empty, "N", out var expectedCorr) ||
                !string.Equals(expectedCorrelationId, expectedCorr.ToString("N"), StringComparison.Ordinal) ||
                !string.Equals(message.CorrelationId, expectedCorrelationId, StringComparison.Ordinal))
            {
                reason = "ExpectedIdentityMismatch";
                return false;
            }
            if (!string.IsNullOrEmpty(expectedRequestPayloadSha256) &&
                !string.Equals(receipt.RequestPayloadSha256, expectedRequestPayloadSha256, StringComparison.Ordinal))
            {
                reason = "RequestPayloadMismatch";
                return false;
            }
            return true;
        }

        /// <summary>
        /// V3 fields are safety evidence, so a peer must use the exact
        /// contract version.  Treating a newer/older payload as compatible
        /// would allow a missing identity or deadline to be interpreted as a
        /// valid handshake.
        /// </summary>
        public static bool IsSupportedVersion(int protocolVersion) =>
            protocolVersion >= MinimumCompatibleVersion &&
            protocolVersion <= Version &&
            protocolVersion == Version;

        private static bool IsCanonicalGuidN(string value)
        {
            Guid parsed;
            return !string.IsNullOrEmpty(value) && Guid.TryParseExact(value, "N", out parsed) &&
                   string.Equals(parsed.ToString("N"), value, StringComparison.Ordinal);
        }

        private static bool IsRequiredToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 &&
                   value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;
        }

        private static bool IsDiagnosticText(string value)
        {
            return value != null && value.Length <= 256 * 1024 &&
                   value.IndexOf('\0') < 0;
        }
    }

    public static class WatchdogMessageType
    {
        public const string Attach = "Attach";
        public const string Attached = "Attached";
        public const string MainUiReady = "MainUiReady";
        public const string Heartbeat = "Heartbeat";
        public const string HeartbeatAck = "HeartbeatAck";
        public const string Ping = "Ping";
        public const string Pong = "Pong";
        public const string ExternalRecoveryRequired = "ExternalRecoveryRequired";
        public const string RecoveryCheckpointValidated = "RecoveryCheckpointValidated";
        public const string SafetyPreflightPassed = "SafetyPreflightPassed";
        public const string RecoveryBatchCommitted = "RecoveryBatchCommitted";
        public const string RecoveryAttemptFailed = "RecoveryAttemptFailed";
        public const string RecoveryAttemptFailedReceipt = "RecoveryAttemptFailedReceipt";
        public const string BatchStartFailed = "BatchStartFailed";
        public const string RequestStopAll = "RequestStopAll";
        public const string StopCompleted = "StopCompleted";
        public const string ManualStopRequested = "ManualStopRequested";
        public const string ManualStopIntent = "ManualStopIntent";
        public const string PhysicalStopConfirmed = "PhysicalStopConfirmed";
        public const string RunStopped = "RunStopped";
        public const string RunCompleted = "RunCompleted";
        public const string ApplicationClosing = "ApplicationClosing";
        public const string ApplicationExitRequested = "ApplicationExitRequested";
        public const string ShutdownExpected = "ShutdownExpected";
        public const string WatchdogTakeoverExit = "WatchdogTakeoverExit";
        public const string SafetyHandoffRequested = "SafetyHandoffRequested";
        public const string SafetyHandoffAccepted = "SafetyHandoffAccepted";
        public const string SafetyHandoffCompleted = "SafetyHandoffCompleted";
    }

    public sealed class WatchdogMessage
    {
        public int ProtocolVersion { get; set; } = WatchdogProtocol.Version;
        public string Type { get; set; }
        public string SessionId { get; set; }
        public string CorrelationId { get; set; }
        /// <summary>Attached response authority identity; prevents a client from
        /// treating a duplicate singleton launch as the active sidecar.</summary>
        public int SidecarProcessId { get; set; }
        public long SidecarProcessStartUtcTicks { get; set; }
        /// <summary>Short canonical alias used by Attached handshake fixtures.</summary>
        public long SidecarStartUtcTicks { get; set; }
        /// <summary>
        /// Compact v3 alias retained for peers which name the frozen process
        /// start field simply <c>StartUtcTicks</c>.
        /// </summary>
        public long StartUtcTicks { get; set; }
        public string SidecarAuthoritySessionId { get; set; }
        public string SidecarSessionId { get; set; }
        /// <summary>每个 Sidecar 进程启动时冻结的随机身份。</summary>
        public string SidecarInstanceNonce { get; set; }
        /// <summary>Canonical v3 alias for SidecarInstanceNonce.</summary>
        public string InstanceNonce { get; set; }
        public string Reason { get; set; }
        /// <summary>机器可判定的恢复失败码；旧客户端缺失时由 sidecar 兼容分类。</summary>
        public string RecoveryFailureCode { get; set; }
        /// <summary>确定性配置/包/检查点不变量错误不得通过重启同一进程重试。</summary>
        public bool RecoveryFailurePermanent { get; set; }
        public string RecoveryFailureDetail { get; set; }
        public string RecoveryFailureContextSha256 { get; set; }
        /// <summary>恢复失败的直接触发方；用于区分业务失败与 Watchdog 自己发起的取消。</summary>
        public string RecoveryFailureOwner { get; set; }
        /// <summary>若失败由 RequestStopAll 引起，回传该请求的关联号。</summary>
        public string RecoveryFailureCorrelationId { get; set; }
        /// <summary>
        /// V3 structured recovery-failure evidence.  These fields are the
        /// durable report identity; RecoveryFailureDetail/Reason remain
        /// diagnostic prose only.
        /// </summary>
        public string RootCode { get; set; }
        public string DeviceOrChannelGroup { get; set; }
        public string RunId { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryProgressToken { get; set; }
        public string RecoveryProcessSource { get; set; }
        public string RecoveryFailureFingerprint { get; set; }
        /// <summary>Schema4 relaunch permit identity/evidence carried by a recovery attach.</summary>
        public long RelaunchPermitGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
        public string RelaunchPermitNonce { get; set; }
        /// <summary>
        /// Heartbeat sequence acknowledged by the sidecar.  It is deliberately
        /// additive so old binaries can continue to deserialize the protocol.
        /// </summary>
        public long AckSequence { get; set; }
        public long RecoveryCommitGeneration { get; set; }
        public WatchdogRunSession Session { get; set; }
        public WatchdogHeartbeat Heartbeat { get; set; }
        public WatchdogStopSummary StopSummary { get; set; }
        public WatchdogCheckpointMirror CheckpointMirror { get; set; }
        /// <summary>启动失败时是否具备跨进程恢复资格；缺失表示旧客户端或证据不足。</summary>
        public WatchdogBatchStartFailureContext BatchStartFailure { get; set; }
        /// <summary>Structured durable failure decision (v4 exact wire shape).</summary>
        public RecoveryFailureReceipt RecoveryFailureReceipt { get; set; }
        public WatchdogSafetyHandoff SafetyHandoff { get; set; }
        public WatchdogApplicationExitReceipt ApplicationExit { get; set; }
    }

    public sealed class WatchdogSafetyHandoff
    {
        public string SessionId { get; set; }
        public long SessionGeneration { get; set; }
        public long SessionLease { get; set; }
        public string HandoffId { get; set; }
        public string Nonce { get; set; }
        public string StopSafetyTransactionId { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int SidecarProcessId { get; set; }
        public long SidecarProcessStartUtcTicks { get; set; }
        public int WorkerProcessId { get; set; }
        public long WorkerProcessStartUtcTicks { get; set; }
        public WatchdogSafetyStage Stage { get; set; }
        public bool MotorsOff { get; set; }
        public bool PowerOff { get; set; }
        public bool PressureSafe { get; set; }
        public bool PersistenceDrained { get; set; }
        public bool LogicalQuiescent { get; set; }
        public bool HardwareResourcesReleased { get; set; }
        public bool ExecutionAuthorizationRevoked { get; set; }
        public bool CallbacksIsolated { get; set; }
        public string ConfigSnapshotManifestSha256 { get; set; }
        public WatchdogRelaunchDisposition RelaunchDisposition { get; set; }
        public long RelaunchPermitGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
        public string RelaunchPermitNonceSha256 { get; set; }
        public string FailureCode { get; set; }
        public RecoveryFailureDomain FailureDomain { get; set; }
        public long TimestampUtcTicks { get; set; }
    }

    public sealed class WatchdogBatchStartFailureContext
    {
        public string RunId { get; set; }
        public string CheckpointRunId { get; set; }
        public long RunEpoch { get; set; }
        public bool FormalRunCommitted { get; set; }
        public bool CheckpointArmed { get; set; }
        public bool RecoveryProcess { get; set; }
    }

    public sealed class WatchdogRunSession
    {
        public string SessionId { get; set; }
        public string PipeName { get; set; }
        public string ExecutablePath { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        /// <summary>Local validated attachment epoch; never reused across Attach.</summary>
        public long AttachEpoch { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int RecoveryAttempt { get; set; }
        /// <summary>每次创建恢复进程时递增，恢复成功后也不回退，用于审计。</summary>
        public long RelaunchGeneration { get; set; }
        public string RelaunchPermitId { get; set; }
        public string RelaunchPermitNonce { get; set; }
        public bool RecoveryProcess { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
    }

    public sealed class WatchdogHeartbeat
    {
        public long Sequence { get; set; }
        /// <summary>Dedicated transport-thread pulse; independent of semantic capture.</summary>
        public long PulseSequence { get; set; }
        /// <summary>Revision of the atomically published semantic snapshot.</summary>
        public long SnapshotRevision { get; set; }
        public long SnapshotCapturedUtcTicks { get; set; }
        public double SnapshotAgeMs { get; set; }
        public string UiLifecycle { get; set; }
        public long ControlProgressVersion { get; set; }
        public string TypedExitTransactionId { get; set; }
        public long GcTotalMemoryBytes { get; set; }
        public int GcCollectionCount0 { get; set; }
        public int GcCollectionCount1 { get; set; }
        public int GcCollectionCount2 { get; set; }
        public int ThreadPoolAvailableWorkerThreads { get; set; }
        public int ThreadPoolAvailableIoThreads { get; set; }
        public double DaqDev1SampleAgeMs { get; set; }
        public int DaqDev1BufferedSamples { get; set; }
        public string DaqDev1ReaderLagState { get; set; }
        public long DaqDev1DroppedFromSequence { get; set; }
        public long DaqDev1DroppedToSequence { get; set; }
        public double DaqDev2SampleAgeMs { get; set; }
        public int DaqDev2BufferedSamples { get; set; }
        public string DaqDev2ReaderLagState { get; set; }
        public long DaqDev2DroppedFromSequence { get; set; }
        public long DaqDev2DroppedToSequence { get; set; }
        public string SessionId { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        /// <summary>Attachment ordering domain stamped by the validated host.</summary>
        public long AttachEpoch { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string Phase { get; set; }
        public int[] EnabledChannels { get; set; } = Array.Empty<int>();
        public int[] EligibleChannels { get; set; } = Array.Empty<int>();
        public int[] CompletedChannels { get; set; } = Array.Empty<int>();
        public int[] AlarmedChannels { get; set; } = Array.Empty<int>();
        /// <summary>结构化硬件锁存通道；Watchdog只排除这一集合。</summary>
        public int[] PermanentAlarmedChannels { get; set; } = Array.Empty<int>();
        /// <summary>主程序已完成资格筛选、允许恢复的通道。</summary>
        public int[] RecoveryEligibleChannels { get; set; } = Array.Empty<int>();
        public int[] ManuallyDisabledChannels { get; set; } = Array.Empty<int>();
        public bool RecoveryActive { get; set; }
        /// <summary>
        /// Operator-requested graceful pause is an intentional quiescent mode,
        /// not an external recovery stage.  The sidecar must never infer a
        /// stalled recovery from this state while heartbeats remain healthy.
        /// </summary>
        public bool ManualPauseActive { get; set; }
        public bool ManualPausePending { get; set; }
        /// <summary>控制器发布的人工暂停权威阶段。</summary>
        public string ManualPauseStage { get; set; }
        /// <summary>阶段或仍带电通道集合发生收敛时单调递增。</summary>
        public long ManualPauseProgressVersion { get; set; }
        /// <summary>人工暂停阶段开始的 UTC DateTime ticks。</summary>
        public long ManualPauseStageStartedUtc { get; set; }
        /// <summary>控制器按周期和控制硬时限计算的暂停硬截止 UTC ticks。</summary>
        public long ManualPauseHardDeadlineUtc { get; set; }
        /// <summary>控制器明确判定人工暂停不能安全收口。</summary>
        public bool ManualPauseSafetyFault { get; set; }
        public string ManualPauseSafetyFaultReason { get; set; }
        public int[] ManualPauseEnergizedChannels { get; set; } = Array.Empty<int>();
        public string RecoveryCode { get; set; }
        public string RecoveryStage { get; set; }
        public string RecoveryIncident { get; set; }
        public string RecoveryContext { get; set; }
        /// <summary>当前发送心跳的进程类型，便于恢复失败审计。</summary>
        public string RecoveryProcessSource { get; set; }
        public int StageOrdinal { get; set; }
        public bool OrphanPaused { get; set; }
        public bool PowerDisablePending { get; set; }
        /// <summary>安全切断阶段已获得全部受影响电源组 OFF 的权威确认。</summary>
        public bool OutputsConfirmedOff { get; set; }
        /// <summary>仍在安全切断阶段且至少一个受影响电源组 OFF 未确认。</summary>
        public bool PowerOffUnconfirmed { get; set; }
        /// <summary>
        /// UTC DateTime ticks.  A process-local Stopwatch value cannot be
        /// compared across the main process and the sidecar.
        /// </summary>
        public long PauseSince { get; set; }
        public long PowerDisableSince { get; set; }
        public long RecoveryProgressVersion { get; set; }
        /// <summary>
        /// Monotonic identity of the controller's immutable aggregate recovery
        /// snapshot.  This is an evidence version, not material recovery
        /// progress; the sidecar must not use it to reset a recovery deadline.
        /// </summary>
        public long RecoveryAggregateSnapshotVersion { get; set; }
        /// <summary>Logical/channel-progress source revision, independent of aggregate revision.</summary>
        public long LogicalSourceVersion { get; set; }
        /// <summary>UTC DateTime ticks at the logical source commit point.</summary>
        public long LogicalCapturedUtcTicks { get; set; }
        /// <summary>控制器恢复流水线硬截止 UTC DateTime ticks。</summary>
        public long RecoveryHardDeadlineUtc { get; set; }
        /// <summary>恢复批次真正提交后递增，并在后续心跳重复发送直到 Sidecar 观察到。</summary>
        public long RecoveryBatchCommitGeneration { get; set; }
        public long StageStartedMonotonic { get; set; }
        public long Dev1CallbackGapCount { get; set; }
        public long Dev2CallbackGapCount { get; set; }
        public int Dev1Generation { get; set; }
        public int Dev2Generation { get; set; }
        public Dictionary<string, long> FrozenBoundary { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Persisted { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> Head { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, long> InFlight { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, int> QueueDepth { get; set; } = new Dictionary<string, int>();
        public int DaqRecoveryCount { get; set; }
        /// <summary>正常活动机械圈，仅用于残留/运行诊断，不参与 RecoveryActive。</summary>
        public int ActiveCycleCount { get; set; }
        public int SoftwareRecoveryCount { get; set; }
        public int RecoveryOwnerCount { get; set; }
        public bool StopAllActive { get; set; }
        public string StopStage { get; set; }
        public long StopStartedUtc { get; set; }
        /// <summary>整个 StopAll 事务的 Controller 逃逸截止（UTC ticks）。</summary>
        public long StopHardDeadlineUtc { get; set; }
        public long StopStageStartedUtc { get; set; }
        public long StopProgressVersion { get; set; }
        /// <summary>当前 StopAll 阶段的硬截止，不使用全局固定五秒替代。</summary>
        public long StopStageHardDeadlineUtc { get; set; }
        /// <summary>当前 StopAll 阶段硬截止后的无材料进展宽限。</summary>
        public int StopStageNoProgressGraceMs { get; set; }
        /// <summary>Controller 最近一次压力/DAQ/持久化材料进展 UTC ticks。</summary>
        public long StopLastMaterialProgressUtc { get; set; }
        /// <summary>
        /// Canonical stop-safety decision emitted by the Controller ledger.
        /// These fields are deliberately separate from StopAllActive: a
        /// terminal/takeover decision remains actionable even after the
        /// Controller has marked the stop transaction inactive.
        /// </summary>
        public bool StopTakeoverRequired { get; set; }
        public bool StopTimedOut { get; set; }
        public string StopTerminalReason { get; set; }
        public string StopTransactionId { get; set; }
        public long StopGeneration { get; set; }
        // Canonical names retained alongside the prefixed aliases so v3 peers can
        // consume the same evidence without reconstructing it from UI text.
        public long StageHardDeadlineUtc { get; set; }
        public int StageNoProgressGraceMs { get; set; }
        public long LastMaterialProgressUtc { get; set; }
        public bool StopPhysicalSafe { get; set; }
        public int TimerCount { get; set; }
        public int RunnerCount { get; set; }
        public int EnergizedChannelCount { get; set; }
        public long CompletedCycleCount { get; set; }
        public int ExpectedCyclePeriodMs { get; set; }
        public int StopCtsCount { get; set; }
        public int CyclePauseCtsCount { get; set; }
        public WatchdogChannelProgress[] ChannelProgress { get; set; } =
            Array.Empty<WatchdogChannelProgress>();
        public string MotorState { get; set; }
        public string PowerState { get; set; }
        public string PressureState { get; set; }
        public string RawState { get; set; }
        public string PersistenceState { get; set; }
        public string ContinuityState { get; set; }
        public string LogicalState { get; set; }
        public bool ManualStopRequested { get; set; }
        /// <summary>恢复进程保持断能并在原进程低频探测硬件，不允许学习。</summary>
        public bool HardwareUnavailable { get; set; }
        public string HardwareFailureFingerprint { get; set; }
        public string HardwareFailureDetail { get; set; }
        public int HardwareProbeAttempt { get; set; }
        public long HardwareNextProbeUtc { get; set; }
        /// <summary>项目日志接收序号；旧端缺省为 0。</summary>
        public long DiagnosticSinkAcceptedVersion { get; set; }
        /// <summary>项目日志已耐久化序号；旧端缺省为 0。</summary>
        public long DiagnosticSinkFlushedVersion { get; set; }
        public long DiagnosticSinkLastSuccessUtcTicks { get; set; }
        public bool DiagnosticSinkStalled { get; set; }
        public bool DiagnosticSinkEmergencySpool { get; set; }
        public int DiagnosticSinkQueueDepth { get; set; }
        public long DiagnosticSinkDroppedRecords { get; set; }
        public string DiagnosticSinkFailure { get; set; }
        public bool RunActive { get; set; }
    }

    public sealed class WatchdogChannelProgress
    {
        public int Channel { get; set; }
        public string State { get; set; }
        /// <summary>通道生命周期阶段；与 State 分开发送，便于协议审计。</summary>
        public string LifecyclePhase { get; set; }
        /// <summary>运行资源契约版本；0 表示旧客户端未发布显式契约。</summary>
        public int RuntimeContractRevision { get; set; }
        public bool MechanicalProgressExpected { get; set; }
        public bool TimerRequired { get; set; }
        public bool RunnerRequired { get; set; }
        public bool ResourcesMustBeInactive { get; set; }
        public bool ManualPauseOwned { get; set; }
        public bool RecoveryOwned { get; set; }
        public string RecoveryOwnerKind { get; set; }
        public string RecoveryOwnerId { get; set; }
        public long RecoveryOwnerGeneration { get; set; }
        public string RecoveryTargetPhase { get; set; }
        public long SourceStateRevision { get; set; }
        public bool WarningActive { get; set; }
        public string WarningCode { get; set; }
        public long WarningRevision { get; set; }
        public long PhaseHardDeadlineUtc { get; set; }
        public long StateRevision { get; set; }
        public long StateSinceUtcTicks { get; set; }
        public bool TimerActive { get; set; }
        public bool RunnerActive { get; set; }
        public bool Energized { get; set; }
        public long ProgressVersion { get; set; }
        public long LastProgressUtcTicks { get; set; }
        public string ProgressKind { get; set; }
        public long LastMechanicalCompletedUtcTicks { get; set; }
        public long MechanicalCompletedCount { get; set; }
        public int ConsecutiveSoftwareAbortCount { get; set; }
        public long DoCommandSequence { get; set; }
        public long PeakCutoffGeneration { get; set; }
        public long PeakCutoffSequence { get; set; }
    }

    public sealed class WatchdogStopSummary
    {
        public bool MotorOffConfirmed { get; set; }
        public bool PowerOffConfirmed { get; set; }
        public bool PressureSafeConfirmed { get; set; }
        public bool RawDrained { get; set; }
        public bool PersistenceConfirmed { get; set; }
        public bool ContinuityConfirmed { get; set; }
        public bool LogicalQuiescenceConfirmed { get; set; }
        public bool RequiresProcessRestart { get; set; }
        public bool TimedOut { get; set; }
        public string Outcome { get; set; }
        public string LastStage { get; set; }
        public string Detail { get; set; }
    }

    public sealed class WatchdogCheckpointMirror
    {
        public int SchemaVersion { get; set; }
        public long Revision { get; set; }
        public bool Armed { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string SessionId { get; set; }
        public string StoreDir { get; set; }
        public string TestName { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public Dictionary<string, int> RemainingFormalCycles { get; set; } =
            new Dictionary<string, int>();
        public string SourcePath { get; set; }
        public string Sha256 { get; set; }
        public string UpdatedUtc { get; set; }
    }
}
