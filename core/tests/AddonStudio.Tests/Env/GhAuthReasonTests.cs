using AddonStudio.Core.Env;

namespace AddonStudio.Tests.Env;

public sealed class GhAuthReasonTests
{
    [Fact]
    public void PicksTheFailingLineAndMasksTokens()
    {
        const string output = """
            github.com
              X Failed to log in to github.com account old-account (keyring)
              - Active account: true
              - The token in keyring is invalid.
              - Token: gho_abcdefghijklmnopqrstuvwxyz
            """;

        var reason = EnvironmentService.GhAuthReason(output);

        Assert.Contains("Failed to log in to github.com account old-account", reason);
        Assert.Contains("The token in keyring is invalid.", reason);
        Assert.DoesNotContain("abcdefghij", reason);
    }

    [Fact]
    public void NotLoggedInAnywhere()
    {
        Assert.Equal(
            "You are not logged into any GitHub hosts. To log in, run: gh auth login",
            EnvironmentService.GhAuthReason("You are not logged into any GitHub hosts. To log in, run: gh auth login\n"));
    }
}
