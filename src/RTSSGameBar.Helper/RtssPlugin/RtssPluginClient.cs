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
        public RtssPluginState ReadState(int timeoutMilliseconds = 1000)
        {
            return ParseState(Send("GET_STATE", timeoutMilliseconds));
        }

        public RtssPluginState SetFrameLimit(int fps, int timeoutMilliseconds = 2200)
        {
            if (fps < 0 || fps > 1000)
                throw new ArgumentOutOfRangeException(nameof(fps));
            return ParseState(Send("SET_FRAME_LIMIT|value=" + fps.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public RtssPluginState SetLimiterType(RtssLimiterType limiterType, int timeoutMilliseconds = 2200)
        {
            var value = (int)limiterType;
            if (value < 0 || value > 3)
                throw new ArgumentOutOfRangeException(nameof(limiterType));
            return ParseState(Send("SET_SYNC_LIMITER|value=" + value.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public RtssPluginState SetLimiterEnabled(bool enabled, int timeoutMilliseconds = 1500)
        {
            return ParseState(Send("SET_LIMITER_ENABLED|value=" + (enabled ? "1" : "0"), timeoutMilliseconds));
        }

        public RtssPluginState SetOverlayVisible(bool visible, int timeoutMilliseconds = 1500)
        {
            return ParseState(Send("SET_OSD_VISIBLE|value=" + (visible ? "1" : "0"), timeoutMilliseconds));
        }

        public RtssPluginState SetOsdZoom(int zoom, int timeoutMilliseconds = 2200)
        {
            if (zoom < 1 || zoom > 8)
                throw new ArgumentOutOfRangeException(nameof(zoom));
            return ParseState(Send("SET_OSD_ZOOM|value=" + zoom.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public RtssPluginState SetOsdPosition(RtssOsdPosition position, int timeoutMilliseconds = 2200)
        {
            var value = (int)position;
            if (value < 0 || value > 7)
                throw new ArgumentOutOfRangeException(nameof(position));
            return ParseState(Send("SET_OSD_POSITION|value=" + value.ToString(CultureInfo.InvariantCulture), timeoutMilliseconds));
        }

        public void CloseRtss(int timeoutMilliseconds = 1000)
        {
            var response = Send("CLOSE_RTSS", timeoutMilliseconds);
            if (!response.StartsWith("OK|", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected RTSS close response: " + response);
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
                PluginVersion = GetValue(fields, "pluginVersion")
            };

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

                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                    {
                        writer.WriteLine(command);
                        var response = reader.ReadLine();

                        if (string.IsNullOrWhiteSpace(response))
                            throw new InvalidOperationException("RTSS plugin returned an empty response.");
                        if (response.StartsWith("ERROR|", StringComparison.Ordinal))
                            throw new RtssPluginException(response);

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
    }

    internal class RtssPluginException : InvalidOperationException
    {
        public RtssPluginException(string message) : base(message) { }
        public RtssPluginException(string message, Exception inner) : base(message, inner) { }
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
