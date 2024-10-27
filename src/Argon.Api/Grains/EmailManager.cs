namespace Argon.Api.Grains;

using Interfaces;using Orleans.EventSourcing;

public class EmailState
{
    
}

public class EmailEvent
{
    
}



public class EmailManager : JournaledGrain<EmailState, EmailEvent>, IEmailManager
{
    public Task SendEmailAsync(string email, string subject, string message, string layout = "base")
    {
        throw new NotImplementedException();
    }
}