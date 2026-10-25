using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using RTSSGameBar.Protocol;

namespace RTSSGameBar.Helper.RtssPlugin
{
    internal sealed class RtssPluginClient
    {
        public RtssPluginState ReadState(string profile, string foregroundHint, int timeoutMilliseconds = 1500)
        {
            var command = new StringBuilder("GET_STATE");
            AppendProfile(command, profile);
            if (!string.IsNullOrEmpty(foregroundHint))
                command.Append("|hint=").Append(Uri.EscapeDataString(foregroundHint));
            return ParseState(Send(command.ToString(), timeoutMilliseconds));
        }

        public RtssPluginState SetFrameLimit(int fps, string profile, int timeoutMilliseconds = 2200)
        {
            if (fps < 0 || fps > 1000)
                throw new ArgumentOutOfRangeException(nameof(fps));
            return ParseState(Send(WithProfile("SET_FRAME_LIMIT", profile) + "|value=" + fps.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public RtssPluginState SetLimiterType(RtssLimiterType limiterType, string profile, int timeoutMilliseconds = 2200)
        {
            var value = (int)limiterType;
            if (value < 0 || value > 3)
                throw new ArgumentOutOfRangeException(nameof(limiterType));
            return ParseState(Send(WithProfile("SET_SYNC_LIMITER", profile) + "|value=" + value.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public RtssPluginState SetLimiterEnabled(bool enabled, int timeoutMilliseconds = 1500)
        {
            // RTSS exposes the limiter enable/disable bit as a global runtime flag, not as a
            // per-profile property. It intentionally stays profile-independent.
            return ParseState(Send("SET_LIMITER_ENABLED|value=" + (enabled ? "1" : "0"), timeoutMilliseconds));
        }

        public RtssPluginState SetOverlayVisible(bool visible, int timeoutMilliseconds = 1500)
        {
            // Same as the limiter bit: OSD visibility is a global RTSS runtime flag.
            return ParseState(Send("SET_OSD_VISIBLE|value=" + (visible ? "1" : "0"), timeoutMilliseconds));
        }

        public RtssPluginState SetOsdZoom(int zoom, string profile, int timeoutMilliseconds = 2200)
        {
            if (zoom < 1 || zoom > 8)
                throw new ArgumentOutOfRangeException(nameof(zoom));
            return ParseState(Send(WithProfile("SET_OSD_ZOOM", profile) + "|value=" + zoom.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public RtssPluginState SetOsdPosition(RtssOsdPosition position, string profile, int timeoutMilliseconds = 2200)
        {
            var value = (int)position;
            if (value < 0 || value > 7)
                throw new ArgumentOutOfRangeException(nameof(position));
            return ParseState(Send(WithProfile("SET_OSD_POSITION", profile) + "|value=" + value.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public void CloseRtss(int timeoutMilliseconds = 1000)
        {
            var response = Send("CLOSE_RTSS", timeoutMilliseconds);
            if (!response.StartsWith("OK|", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected RTSS close response: " + response);
        }

        private static string WithProfile(string command, string profile)
        {
            var builder = new StringBuilder(command);
            AppendProfile(builder, profile);
            return builder.ToString();
        }

        private static void AppendProfile(StringBuilder builder, string profile)
        {
            if (!string.IsNullOrEmpty(profile))
                builder.Append("|profile=").Append(Uri.EscapeDataString(profile));
        }

        private static RtssPluginState ParseState(string response)
        {
            if (!response.StartsWith("STATE|", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected RTSS plugin state response: " + response);

            var fields = ParseFields(response);
            ValidateProtocol(fields);
            var frameLimit = GetRequiredInt(fields, "frameLimit");
            var syncLimiter = GetRequiredInt(fields, "syncLimiter");
            var zoomRatio = GetRequiredInt(fields, "zoomRatio");
            var positionPreset = GetRequiredInt(fields, "positionPreset");

            var state = new RtssPluginState
            {
                FrameLimit = frameLimit,
                SyncLimiter = syncLimiter,
                OsdZoom = zoomRatio,
                OsdPosition = positionPreset >= 0 && positionPreset <= 7 ? (RtssOsdPosition?)positionPreset : null,
                PluginVersion = GetValue(fields, "pluginVersion"),
                Profile = DecodeValue(GetValue(fields, "profile")),
                ProfileMissing = DecodeValue(GetValue(fields, "profileMissing")),
                ActiveProfileHint = DecodeValue(GetValue(fields, "activeHint")),
                ProfilesTruncated = GetValue(fields, "truncated") == "1"
            };

            var count = GetRequiredInt(fields, "profileCount");
            if (count > 0)
            {
                var profiles = new List<string>(count);
                for (var i = 0; i < count; i++)
                {
                    string encoded;
                    if (!fields.TryGetValue("p" + i.ToString(CultureInfo.InvariantCulture), out encoded))
                        throw new InvalidOperationException("RTSS plugin profile list is missing entry " + i + ".");
                    profiles.Add(DecodeValue(encoded));
                }
                state.AvailableProfiles = profiles;
            }
            else
            {
                state.AvailableProfiles = new List<string>();
            }

            uint flags;
            if (TryGetUInt(fields, "flags", out flags))
            {
                state.OverlayVisible = (flags & 1u) != 0;
                state.LimiterEnabled = (flags & 4u) == 0;
            }

            return state;
        }

        private static string Send(string command, int timeoutMilliseconds)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(
                    ".",
                    ProtocolConstants.RtssPluginPipeName,
                    PipeDirection.InOut,
                    PipeOptions.None))
                {
                    pipe.Connect(timeoutMilliseconds);
                    pipe.ReadMode = PipeTransmissionMode.Byte;

                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                    {
                        writer.WriteLine(command);
                        var response = reader.ReadLine();

                        if (string.IsNullOrWhiteSpace(response))
                            throw new InvalidOperationException("RTSS plugin returned an empty response.");
                        if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                            throw RtssPluginException.FromErrorPayload(response);

                        return response;
                    }
                }
            }
            catch (TimeoutException ex)
            {
                throw new RtssPluginUnavailableException(
                    "RTSS Game Bar integration did not answer. RTSS may be stopped, the plugin may be disabled, or an update may be required.", ex);
            }
            catch (IOException ex)
            {
                throw new RtssPluginUnavailableException("Could not communicate with RTSSGameBarPlugin.dll: " + ex.Message, ex);
            }
        }

        private static void ValidateProtocol(IDictionary<string, string> fields)
        {
            var protocol = GetRequiredInt(fields, "protocol");
            if (protocol != ProtocolConstants.RtssPluginProtocolVersion)
                throw new RtssPluginProtocolException(
                    "RTSS plugin protocol mismatch. Expected " + ProtocolConstants.RtssPluginProtocolVersion + ", received " + protocol + ".");
        }

        private static Dictionary<string, string> ParseFields(string response)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = response.Split('|');
            for (var i = 1; i < parts.Length; i++)
            {
                var separator = parts[i].IndexOf('=');
                if (separator <= 0)
                    continue;
                result[parts[i].Substring(0, separator)] = parts[i].Substring(separator + 1);
            }
            return result;
        }

        private static string DecodeValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch
            {
                return value;
            }
        }

        private static int GetRequiredInt(IDictionary<string, string> fields, string key)
        {
            int value;
            if (!TryGetInt(fields, key, out value))
                throw new InvalidOperationException("RTSS plugin response is missing or has invalid " + key + ".");
            return value;
        }

        private static string GetValue(IDictionary<string, string> fields, string key)
        {
            string value;
            return fields.TryGetValue(key, out value) ? value : null;
        }

        private static bool TryGetInt(IDictionary<string, string> fields, string key, out int value)
        {
            value = 0;
            string text;
            return fields.TryGetValue(key, out text)
                && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetUInt(IDictionary<string, string> fields, string key, out uint value)
        {
            value = 0;
            string text;
            return fields.TryGetValue(key, out text)
                && uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    internal sealed class RtssPluginState
    {
        public int FrameLimit { get; set; }
        public int SyncLimiter { get; set; }
        public int OsdZoom { get; set; }
        public RtssOsdPosition? OsdPosition { get; set; }
        public bool? LimiterEnabled { get; set; }
        public bool? OverlayVisible { get; set; }
        public string PluginVersion { get; set; }
        public string Profile { get; set; }
        public string ProfileMissing { get; set; }
        public string ActiveProfileHint { get; set; }
        public List<string> AvailableProfiles { get; set; }
        public bool ProfilesTruncated { get; set; }
    }

    internal class RtssPluginException : InvalidOperationException
    {
        public RtssPluginException(string message) : base(message) { }
        public RtssPluginException(string message, Exception inner) : base(message, inner) { }

        /// <summary>Plugin error code, for example "profile_not_found". May be null.</summary>
        public string Code { get; private set; }

        /// <summary>Raw plugin error text, kept for diagnostics. Never shown to the user.</summary>
        public string PluginMessage { get; private set; }

        /// <summary>
        /// Builds an exception from a plugin error payload shaped as
        /// "ERROR|code=&lt;code&gt;|message=&lt;text&gt;". The payload itself is technical, so the
        /// exception message is a readable description and the raw parts stay in properties.
        /// </summary>
        public static RtssPluginException FromErrorPayload(string payload)
        {
            string code = null;
            string message = null;
            if (!string.IsNullOrEmpty(payload))
            {
                foreach (var field in payload.Split('|'))
                {
                    if (field.StartsWith("code=", StringComparison.Ordinal))
                        code = field.Substring(5);
                    else if (field.StartsWith("message=", StringComparison.Ordinal))
                        message = field.Substring(8);
                }
            }

            return new RtssPluginException(DescribeError(code, message))
            {
                Code = code,
                PluginMessage = message
            };
        }

        private static string DescribeError(string code, string message)
        {
            var detail = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
            switch (code)
            {
                case "profile_not_found":
                    return "RTSS profile \"" + detail + "\" was not found. It may have been renamed or removed in RTSS.";
                case "profile_enum_failed":
                    return "RTSS could not list its profiles: " + detail;
                case "api_unavailable":
                    return "The RTSS integration is unavailable: " + detail;
                case "read_state_failed":
                    return "RTSS state could not be read: " + detail;
                case "write_failed":
                    return "RTSS rejected the change: " + detail;
                case "post_write_state_failed":
                    return "RTSS could not confirm the change by reading it back: " + detail;
                case "unsupported_command":
                    return "The RTSS integration does not support this command: " + detail;
                default:
                    var suffix = string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail;
                    return "The RTSS integration reported an error (" + (code ?? "unknown") + ")" + suffix;
            }
        }
    }

    internal sealed class RtssPluginUnavailableException : RtssPluginException
    {
        public RtssPluginUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    internal sealed class RtssPluginProtocolException : RtssPluginException
    {
        public RtssPluginProtocolException(string message) : base(message) { }
    }
}
