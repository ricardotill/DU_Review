using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Access;
using ScoutingAutomation.Web.Auth;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;
using ComponentRouteData = Microsoft.AspNetCore.Components.RouteData;

namespace ScoutingAutomation.UnitTests.Web;

/// <summary>Group logins: what they are, when they stop working, and which pages they may open.</summary>
public class GroupAuthTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AccessKeyService _keys;
    private readonly Guid _groupId = Guid.NewGuid();

    public GroupAuthTests()
    {
        _keys = new AccessKeyService(Options.Create(new ScoutingSettings { KeysFile = Path.Combine(_temp.Path, "keys.json") }), NullLogger<AccessKeyService>.Instance);
        _keys.EnsureGroupKeys(["C1", "C2"]);
    }

    public void Dispose() => _temp.Dispose();

    private ClaimsPrincipal Group() => KeyAuth.CreateGroupPrincipal(_keys, _groupId, "C1", "De Wolven");

    [Fact]
    public void GroupPrincipal_ShouldBeAGroupAndNeverJuryOrAdmin()
    {
        var group = Group();

        KeyAuth.IsGroup(group).Should().BeTrue();
        KeyAuth.GroupId(group).Should().Be(_groupId);
        group.IsInRole(KeyAuth.JuryRole).Should().BeFalse();
        group.IsInRole(KeyAuth.AdminRole).Should().BeFalse();
        group.Identity!.Name.Should().Be("De Wolven");
    }

    [Fact]
    public void JuryAndAdmin_ShouldHaveTheJuryRoleAndNoGroup()
    {
        var jury = KeyAuth.CreatePrincipal(_keys, admin: false);
        var admin = KeyAuth.CreatePrincipal(_keys, admin: true);

        jury.IsInRole(KeyAuth.JuryRole).Should().BeTrue();
        admin.IsInRole(KeyAuth.JuryRole).Should().BeTrue();
        KeyAuth.GroupId(jury).Should().BeNull();
        KeyAuth.GroupId(admin).Should().BeNull();
    }

    [Fact]
    public void Revalidate_ShouldEndAGroupLoginOnlyWhenItsOwnKeyChanges()
    {
        var group = Group();

        KeyAuth.Revalidate(group, _keys).Should().BeSameAs(group);

        _keys.RegenerateAccessKey();
        _keys.RegenerateAdminKey();
        _keys.RegenerateGroupKey("C2");
        KeyAuth.Revalidate(group, _keys).Should().BeSameAs(group);

        _keys.RegenerateGroupKey("C1");
        KeyAuth.Revalidate(group, _keys).Should().BeNull();
    }

    [Fact]
    public void Revalidate_ShouldEndAGroupLoginWhenItsKeyIsRemoved()
    {
        var group = Group();

        _keys.RemoveGroupKey("C1");

        KeyAuth.Revalidate(group, _keys).Should().BeNull();
    }

    [Fact]
    public void Revalidate_ShouldGiveAJuryLoginFromBeforeTheJuryRoleThatRole()
    {
        var old = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Jury"), new Claim("du:access", _keys.AccessStamp)], KeyAuth.Scheme));

        var validated = KeyAuth.Revalidate(old, _keys);

        validated!.IsInRole(KeyAuth.JuryRole).Should().BeTrue();
        validated.IsInRole(KeyAuth.AdminRole).Should().BeFalse();
    }

    [Fact]
    public void Revalidate_ShouldRefuseAForgedGroupClaimOnAJuryLogin()
    {
        // A group role without a matching group stamp is no login at all
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, KeyAuth.GroupRole), new Claim(KeyAuth.GroupIdClaim, _groupId.ToString())], KeyAuth.Scheme));

        KeyAuth.Revalidate(forged, _keys).Should().BeNull();
    }

    public static TheoryData<Type, bool> Pages => new()
    {
        { typeof(Home), true },
        { typeof(Submissions), true },
        { typeof(SubmissionDetail), true },
        { typeof(AccessDenied), true },
        { typeof(GroupDetail), false },
        { typeof(Groups), false },
        { typeof(Queue), false },
        { typeof(Assignments), false },
        { typeof(AssignmentDetail), false },
        { typeof(Messages), false },
        { typeof(Scoreboard), false },
        { typeof(Slideshow), false },
        { typeof(Standing), false },
        { typeof(Events), false },
        { typeof(Configuration), false },
        { typeof(Admin), false },
        { typeof(SlackSimulator), false }
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Handler_ShouldLetGroupsOnlyOpenMarkedPages(Type page, bool groupsAllowed)
    {
        var route = new ComponentRouteData(page, new Dictionary<string, object?>());

        (await AuthorizeAsync(Group(), route)).Should().Be(groupsAllowed);
        (await AuthorizeAsync(KeyAuth.CreatePrincipal(_keys, admin: false), route)).Should().BeTrue();
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
        (await AuthorizeAsync(KeyAuth.CreatePrincipal(_keys, admin: false), unmarked)).Should().BeTrue();
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
