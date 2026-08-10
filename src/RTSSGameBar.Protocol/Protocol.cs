using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RTSSGameBar.Protocol
{
    public static class ProtocolConstants
    {
        public const int Version = 19;
        public const string PipeName = "RTSSGameBar.v19";
        public const string RtssPluginPipeName = "RTSSGameBar.RTSSPlugin.v6";
        public const int RtssPluginProtocolVersion = 6;
        public const string BundledPluginVersion = "1.0.0";
        public const string GlobalProfile = "";
    }

    [DataContract]
    public enum RtssCommand
    {
        [EnumMember] Ping = 0,
        [EnumMember] GetStatus = 1,
        [EnumMember] StartRtss = 2,
        [EnumMember] StopRtss = 3,
        [EnumMember] SetFrameLimit = 4,
        [EnumMember] SetLimiterEnabled = 5,
        [EnumMember] SetOverlayVisible = 6,
        [EnumMember] SetLimiterType = 7,
        [EnumMember] SetOsdZoom = 8,
        [EnumMember] InstallIntegration = 10,
        [EnumMember] UpdateIntegration = 11,
        [EnumMember] RemoveIntegration = 12,
        [EnumMember] SetOsdPosition = 14
    }

    [DataContract]
    public enum RtssLimiterType
    {
        [EnumMember] Async = 0,
        [EnumMember] FrontEdgeSync = 1,
        [EnumMember] BackEdgeSync = 2,
        [EnumMember] NvidiaReflex = 3
    }

    [DataContract]
    public enum RtssOsdPosition
    {
        [EnumMember] TopLeft = 0,
        [EnumMember] TopCenter = 1,
        [EnumMember] TopRight = 2,
        [EnumMember] BottomLeft = 3,
        [EnumMember] BottomCenter = 4,
        [EnumMember] BottomRight = 5,
        [EnumMember] MiddleLeft = 6,
        [EnumMember] MiddleRight = 7
    }

    [DataContract]
    public enum RtssIntegrationState
    {
        [EnumMember] Unknown = 0,
        [EnumMember] RtssNotInstalled = 1,
        [EnumMember] NotInstalled = 2,
        [EnumMember] UpdateRequired = 3,
        [EnumMember] RtssStopped = 4,
        [EnumMember] Disabled = 5,
        [EnumMember] Incompatible = 6,
        [EnumMember] Connected = 7,
        [EnumMember] Error = 8
    }

    [DataContract]
    public sealed class RtssRequest
    {
        [DataMember(Order = 1)] public int ProtocolVersion { get; set; } = ProtocolConstants.Version;
        [DataMember(Order = 2)] public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember(Order = 3)] public RtssCommand Command { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Profile { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public int? IntValue { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public bool? BoolValue { get; set; }
    }

    [DataContract]
    public sealed class RtssResponse
    {
        [DataMember(Order = 1)] public int ProtocolVersion { get; set; } = ProtocolConstants.Version;
        [DataMember(Order = 2)] public string RequestId { get; set; }
        [DataMember(Order = 3)] public bool Success { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public string ErrorCode { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public string ErrorMessage { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public RtssStatus Status { get; set; }

        public static RtssResponse Ok(RtssRequest request)
        {
            return new RtssResponse { RequestId = request?.RequestId, Success = true };
        }

        public static RtssResponse Fail(RtssRequest request, string code, string message)
        {
            return new RtssResponse
            {
                RequestId = request?.RequestId,
                Success = false,
                ErrorCode = code,
                ErrorMessage = message
            };
        }
    }

    [DataContract]
    public sealed class RtssStatus
    {
        [DataMember(Order = 1)] public bool Installed { get; set; }
        [DataMember(Order = 2)] public bool Running { get; set; }
        [DataMember(Order = 3, EmitDefaultValue = false)] public int? FrameLimit { get; set; }
        [DataMember(Order = 4, EmitDefaultValue = false)] public bool? LimiterEnabled { get; set; }
        [DataMember(Order = 5, EmitDefaultValue = false)] public bool? OverlayVisible { get; set; }
        [DataMember(Order = 6, EmitDefaultValue = false)] public string Detail { get; set; }
        [DataMember(Order = 7, EmitDefaultValue = false)] public RtssLimiterType? LimiterType { get; set; }
        [DataMember(Order = 8)] public bool PluginInstalled { get; set; }
        [DataMember(Order = 9)] public bool PluginConnected { get; set; }
        [DataMember(Order = 10, EmitDefaultValue = false)] public int? OsdZoom { get; set; }
        [DataMember(Order = 11)] public bool PluginUpdateAvailable { get; set; }
        [DataMember(Order = 12)] public RtssIntegrationState IntegrationState { get; set; }
        [DataMember(Order = 13, EmitDefaultValue = false)] public string PluginVersion { get; set; }
        [DataMember(Order = 14, EmitDefaultValue = false)] public string BundledPluginVersion { get; set; }
        [DataMember(Order = 15, EmitDefaultValue = false)] public RtssOsdPosition? OsdPosition { get; set; }
    }

    public static class ProtocolJson
    {
        public static string Serialize<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new SerializationException("Empty IPC payload.");

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }
    }
}
