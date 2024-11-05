namespace Argon.Api.Http.Tests;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;

public class Root : TestAppBuilder
{
    [Fact]
    public async Task Test1()
    {
        Assert.True(true);
    }
}