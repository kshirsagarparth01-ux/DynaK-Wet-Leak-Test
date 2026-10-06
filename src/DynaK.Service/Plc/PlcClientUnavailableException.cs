namespace DynaK.Service.Plc;

public sealed class PlcClientUnavailableException : Exception
{
    public PlcClientUnavailableException(string message) : base(message)
    {
    }
}

public sealed class PlcSignalMappingException : Exception
{
    public PlcSignalMappingException(string message) : base(message)
    {
    }
}
