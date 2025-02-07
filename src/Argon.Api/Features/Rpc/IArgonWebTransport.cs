namespace Argon.Services;

public interface IArgonWebTransport
{
    Task HandleTransportRequest(HttpContext ctx, ArgonTransportFeaturePipe conn, ArgonTransportContext scope);
}