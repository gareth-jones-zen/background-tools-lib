using System.Text.Json;

namespace EdgeDevTools.Tests;

public class MicrosoftSignInTests
{
    private const string Login = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?x=1";

    private static SignInProbe Probe(string href = Login, bool password = false, bool username = false, bool code = false,
        bool approve = false, bool hello = false, bool picker = false, bool tile = false) =>
        new(href, "Sign in", null, tile ? (1, 2) : null, password, username, code, approve, hello, picker);

    [Theory]
    [InlineData("https://login.microsoftonline.com/x", true)]
    [InlineData("https://LOGIN.MICROSOFTONLINE.COM/x", true)]
    [InlineData("https://device.login.microsoftonline.com/x", true)]
    [InlineData("https://login.live.com/", true)]
    [InlineData("https://zen.people-first.com/login", false)]
    [InlineData("https://notlogin.microsoftonline.com.evil.example/", false)]
    [InlineData("about:blank", false)]
    public void Microsoft_hosts(string href, bool microsoft) =>
        Assert.Equal(microsoft, MicrosoftSignIn.IsMicrosoftHost(Probe(href).Host));

    [Fact]
    public void Pages_are_classified_with_a_password_outranking_everything()
    {
        Assert.Equal(SignInPage.NotMicrosoft, MicrosoftSignIn.Classify(Probe("https://zen.people-first.com/", password: true)));
        Assert.Equal(SignInPage.Password, MicrosoftSignIn.Classify(Probe(password: true, picker: true, tile: true)));
        Assert.Equal(SignInPage.Verification, MicrosoftSignIn.Classify(Probe(approve: true)));
        Assert.Equal(SignInPage.Verification, MicrosoftSignIn.Classify(Probe(code: true, hello: true)));
        Assert.Equal(SignInPage.WindowsHello, MicrosoftSignIn.Classify(Probe(hello: true, picker: true)));
        Assert.Equal(SignInPage.AccountPicker, MicrosoftSignIn.Classify(Probe(picker: true)));
        Assert.Equal(SignInPage.AccountPicker, MicrosoftSignIn.Classify(Probe(tile: true)));
        Assert.Equal(SignInPage.Username, MicrosoftSignIn.Classify(Probe(username: true)));
        Assert.Equal(SignInPage.Other, MicrosoftSignIn.Classify(Probe()));
        foreach (var kind in new[] { SignInPage.Password, SignInPage.Username, SignInPage.Verification, SignInPage.WindowsHello })
            Assert.True(MicrosoftSignIn.NeedsAPerson(kind));
        foreach (var kind in new[] { SignInPage.NotMicrosoft, SignInPage.AccountPicker, SignInPage.Other })
            Assert.False(MicrosoftSignIn.NeedsAPerson(kind));
    }

    [Fact]
    public void A_probe_result_is_read_and_junk_is_not()
    {
        using var document = JsonDocument.Parse("""
            {"href":"https://login.microsoftonline.com/","title":"Sign in","button":null,"tile":[10.5,20],
             "password":false,"username":false,"code":false,"approve":true,"hello":false,"picker":true}
            """);
        var probe = MicrosoftSignIn.Parse(document.RootElement)!;
        Assert.Equal((10.5, 20.0), probe.Tile);
        Assert.Null(probe.Button);
        Assert.True(probe.Approve);
        Assert.Equal(SignInPage.Verification, MicrosoftSignIn.Classify(probe));
        Assert.Null(MicrosoftSignIn.Parse(default));
    }

    [Fact]
    public void Values_go_into_scripts_as_string_literals()
    {
        var script = MicrosoftSignIn.ProbeScript("Organisation account", "o'brien\"</script>@example.com");
        Assert.StartsWith(MicrosoftSignIn.ProbeMarker, script);
        Assert.Contains("\"Organisation account\"", script);
        Assert.Contains(PageScripts.JsonString("o'brien\"</script>@example.com"), script);
        Assert.EndsWith("(\"Organisation account\", " + PageScripts.JsonString("o'brien\"</script>@example.com") + ")", script);
        Assert.Contains("(null, null)", MicrosoftSignIn.ProbeScript(null, null));
    }
}
