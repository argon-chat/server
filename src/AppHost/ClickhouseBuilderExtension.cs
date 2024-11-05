namespace AppHost;

public class ClickhouseBuilderExtension : ContainerResource, IResourceWithConnectionString, IResourceWithEnvironment
{
    internal const string PrimaryEndpointName = "http";
    internal const string ClientEndpointName  = "tcp";
    private const  string DefaultUserName     = "guest";

    public ClickhouseBuilderExtension(string                               name, IResourceBuilder<ParameterResource>? userName,
                                      IResourceBuilder<ParameterResource>? password) : base(name: name)
    {
        PrimaryEndpoint   = new EndpointReference(owner: this, endpointName: PrimaryEndpointName);
        ClientEndpoint    = new EndpointReference(owner: this, endpointName: ClientEndpointName);
        UserNameParameter = userName?.Resource;
        PasswordParameter = password?.Resource;
    }

    public EndpointReference  PrimaryEndpoint   { get; init; }
    public EndpointReference  ClientEndpoint    { get; init; }
    public ParameterResource? UserNameParameter { get; init; }
    public ParameterResource? PasswordParameter { get; init; }

    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create(
                                   handler:
                                   $"http://{UserNameParameter}:{PasswordParameter}@{PrimaryEndpoint.Property(property: EndpointProperty.Host)}:{PrimaryEndpoint.Property(property: EndpointProperty.Port)}"
                                  );
}