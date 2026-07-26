using LeanPlay.Core.Engine;

namespace LeanPlay.Core.Tests;

public sealed class OptimizationPolicyTests
{
    [Fact]
    public void UnapprovedServiceRuleIsRejected()
    {
        var exception = Assert.Throws<ProfileValidationException>(
            () => OptimizationPolicy.Validate(Profiles.Cs2(approved: false)));

        Assert.Contains("not explicitly approved", exception.Message);
    }

    [Theory]
    [InlineData("RpcSs")]
    [InlineData("WinDefend")]
    [InlineData("vgc")]
    public void ProtectedServiceRuleIsRejected(string serviceName)
    {
        var exception = Assert.Throws<ProfileValidationException>(
            () => OptimizationPolicy.Validate(
                Profiles.Cs2(serviceName: serviceName)));

        Assert.Contains("protected", exception.Message);
    }

    [Fact]
    public void ExecutablePathIsRejected()
    {
        var profile = Profiles.Cs2() with
        {
            ExecutableName = @"C:\Games\cs2.exe"
        };

        var exception = Assert.Throws<ProfileValidationException>(
            () => OptimizationPolicy.Validate(profile));

        Assert.Contains("file name", exception.Message);
    }
}
