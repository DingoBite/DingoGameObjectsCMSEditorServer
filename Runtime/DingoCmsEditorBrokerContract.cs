using System;

namespace DingoGameObjectsCMSEditorServer.Runtime
{
    public static class DingoCmsEditorBrokerContract
    {
        public const int DefaultPort = 17844;
        public const string TokenEnvironmentVariable =
            "DINGO_CMS_EDITOR_TOKEN";
        public const string InstanceTokenEnvironmentVariable =
            "DINGO_CMS_EDITOR_INSTANCE_TOKEN";
        public const string BrokerFingerprintEnvironmentVariable =
            "DINGO_CMS_EDITOR_BROKER_FINGERPRINT";

        public static int RequirePort(int port)
        {
            if (port < 1024 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(port),
                    port,
                    "The DingoCMS broker port must be between 1024 and 65535.");
            }

            return port;
        }
    }
}
