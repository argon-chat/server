namespace AppHost;

public class ClickhouseBuilderExtension : ContainerResource, IResourceWithConnectionString, IResourceWithEnvironment
{
    internal const string PrimaryEndpointName = "http";
    internal const string ClientEndpointName = "tcp";
    private const string DefaultUserName = "guest";

    public ClickhouseBuilderExtension(string name, IResourceBuilder<ParameterResource>? userName,
        IResourceBuilder<ParameterResource>? password) : base(name)
    {
        PrimaryEndpoint = new EndpointReference(this, PrimaryEndpointName);
        ClientEndpoint = new EndpointReference(this, ClientEndpointName);
        UserNameParameter = userName?.Resource;
        PasswordParameter = password?.Resource;
    }

    public EndpointReference PrimaryEndpoint { get; init; }
    public EndpointReference ClientEndpoint { get; init; }
    public ParameterResource? UserNameParameter { get; init; }
    public ParameterResource? PasswordParameter { get; init; }

    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create(
            $"http://{UserNameParameter}:{PasswordParameter}@{PrimaryEndpoint.Property(EndpointProperty.Host)}:{PrimaryEndpoint.Property(EndpointProperty.Port)}"
        );
}