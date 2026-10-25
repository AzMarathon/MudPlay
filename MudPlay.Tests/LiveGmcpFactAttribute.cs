using Xunit;

namespace MudPlay.Tests;

public sealed class LiveGmcpFactAttribute : FactAttribute
{
    public LiveGmcpFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MUDPLAY_GMCP_ACCOUNT")))
            Skip = "Requires the isolated GMCP test host and disposable test accounts.";
    }
}
