using Palladin.Module.Agents.Infrastructure.Pairing;

namespace Palladin.Tests.Unit.Modules.Agents;

public sealed class AgentPairingOptionsTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(29, false)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void PairingLifetimeMustMatchTheThirtyMinuteApprovalContract(int minutes, bool accepted) =>
        Assert.Equal(accepted, new AgentPairingOptions { Lifetime = TimeSpan.FromMinutes(minutes) }.HasSupportedLifetime);

    [Theory]
    [InlineData("https://palladin.io")]
    [InlineData("https://palladin.io/")]
    [InlineData("https://stage.palladin.io")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://[::1]:45173")]
    public void AcceptedApprovalBasesMatchTheNativeRuntimeContract(string value) =>
        Assert.True(AgentPairingOptions.IsValidApprovalUrlBase(value));

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://palladin.io/app")]
    [InlineData("https://palladin.io:443")]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:5173/app")]
    [InlineData("http://127.0.0.1:5173?next=secret")]
    public void OtherApprovalBasesAreRejected(string value) =>
        Assert.False(AgentPairingOptions.IsValidApprovalUrlBase(value));
}
