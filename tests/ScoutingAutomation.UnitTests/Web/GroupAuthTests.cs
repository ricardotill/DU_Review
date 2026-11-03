using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ScoutingAutomation.Web.Auth;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;
using ComponentRouteData = Microsoft.AspNetCore.Components.RouteData;

namespace ScoutingAutomation.UnitTests.Web;

/// <summary>Group and beamer logins: what they are, and which pages they may open.</summary>
public class GroupAuthTests
{
    private readonly Guid _groupId = Guid.NewGuid();

    private static ClaimsPrincipal Login(params Claim[] claims) =>
        new(new ClaimsIdentity(claims.Append(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())), AppAuth.Scheme));

    private ClaimsPrincipal Group() => Login(new Claim(ClaimTypes.Role, AppAuth.GroupRole), new Claim(AppAuth.GroupIdClaim, _groupId.ToString()));

    private static ClaimsPrincipal Jury() => Login(new Claim(ClaimTypes.Role, AppAuth.JuryRole));

    private static ClaimsPrincipal Admin() => Login(new Claim(ClaimTypes.Role, AppAuth.AdminRole), new Claim(ClaimTypes.Role, AppAuth.JuryRole));

    private static ClaimsPrincipal Beamer() => Login(new Claim(ClaimTypes.Role, AppAuth.BeamerRole));

    [Fact]
    public void Group_ShouldBeAGroupWithItsGroupAndNeverJuryOrAdmin()
    {
        var group = Group();

        AppAuth.IsGroup(group).Should().BeTrue();
        AppAuth.GroupId(group).Should().Be(_groupId);
        AppAuth.IsBeamer(group).Should().BeFalse();
        group.IsInRole(AppAuth.JuryRole).Should().BeFalse();
    }

    [Fact]
    public void JuryAdminAndBeamer_ShouldHaveNoGroup()
    {
        AppAuth.GroupId(Jury()).Should().BeNull();
        AppAuth.GroupId(Admin()).Should().BeNull();
        AppAuth.GroupId(Beamer()).Should().BeNull();
        AppAuth.IsBeamer(Beamer()).Should().BeTrue();
        AppAuth.IsBeamer(Jury()).Should().BeFalse();
    }

    [Fact]
    public void GroupClaim_WithoutTheGroupRole_ShouldNotMakeAGroup()
    {
        var forged = Login(new Claim(ClaimTypes.Role, AppAuth.JuryRole), new Claim(AppAuth.GroupIdClaim, _groupId.ToString()));

        AppAuth.GroupId(forged).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/groups", "/groups")]
    [InlineData("/submissions?group=1", "/submissions?group=1")]
    [InlineData("groups", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    public void SafeReturnUrl_ShouldOnlyAllowLocalPaths(string? returnUrl, string expected) =>
        AppAuth.SafeReturnUrl(returnUrl).Should().Be(expected);

    [Fact]
    public void StartPage_ShouldSendGroupsHomeAndTheBeamerToTheScoreboard()
    {
        AppAuth.StartPage(Jury(), "/queue").Should().Be("/queue");
        AppAuth.StartPage(Group(), "/queue").Should().Be("/", "a jury page would only say no access");
        AppAuth.StartPage(Beamer(), "/queue").Should().Be("/scoreboard");
    }

    public static TheoryData<Type, bool, bool> Pages => new()
    {
        // Page, groups allowed, beamer allowed
        { typeof(Home), true, false },
        { typeof(Submissions), true, false },
        { typeof(SubmissionDetail), true, false },
        { typeof(AccessDenied), true, true },
        { typeof(Account), true, true },
        { typeof(GroupDetail), false, false },
        { typeof(Groups), false, false },
        { typeof(Queue), false, false },
        { typeof(Assignments), false, false },
        { typeof(AssignmentDetail), false, false },
        { typeof(Messages), false, false },
        { typeof(Scoreboard), false, true },
        { typeof(Slideshow), false, true },
        { typeof(Standing), false, false },
        { typeof(Events), false, false },
        { typeof(Configuration), false, false },
        { typeof(Admin), false, false },
        { typeof(SlackSimulator), false, false }
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Handler_ShouldLetGroupsAndTheBeamerOnlyOpenMarkedPages(Type page, bool groupsAllowed, bool beamerAllowed)
    {
        var route = new ComponentRouteData(page, new Dictionary<string, object?>());

        (await AuthorizeAsync(Group(), route)).Should().Be(groupsAllowed);
        (await AuthorizeAsync(Beamer(), route)).Should().Be(beamerAllowed);
        (await AuthorizeAsync(Jury(), route)).Should().BeTrue();
    }

    [Fact]
    public async Task Handler_ShouldCheckTheEndpointOfARequest()
    {
        var marked = Request("/api/files/x", new GroupAccessAttribute());
        var unmarked = Request("/api/export/scores.csv");
        var hub = Request("/_blazor/negotiate");

        (await AuthorizeAsync(Group(), marked)).Should().BeTrue();
        (await AuthorizeAsync(Group(), unmarked)).Should().BeFalse();
        (await AuthorizeAsync(Group(), hub)).Should().BeTrue();
        (await AuthorizeAsync(Group(), new DefaultHttpContext())).Should().BeFalse("no endpoint, no access");
        (await AuthorizeAsync(Group(), null)).Should().BeFalse("an AuthorizeView without roles is for the jury");
        (await AuthorizeAsync(Beamer(), marked)).Should().BeFalse("marked for groups, not for the beamer");
        (await AuthorizeAsync(Beamer(), Request("/api/files/x", new BeamerAccessAttribute()))).Should().BeTrue();
        (await AuthorizeAsync(Jury(), unmarked)).Should().BeTrue();
    }

    private static HttpContext Request(string path, params object[] metadata)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), path));
        return http;
    }

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user, object? resource)
    {
        var requirement = new StaffOrGroupPageRequirement();
        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await new GroupAccessHandler().HandleAsync(context);
        return context.HasSucceeded;
    }
}
