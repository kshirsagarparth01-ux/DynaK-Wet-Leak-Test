namespace DynaK.Service.Plc;

public enum PlcServiceStatus
{
    NotConfigured,
    Connecting,
    Connected,
    Disconnected,
    Reconnecting,
    Error,
    ConfigurationError,
    ServiceError
}
