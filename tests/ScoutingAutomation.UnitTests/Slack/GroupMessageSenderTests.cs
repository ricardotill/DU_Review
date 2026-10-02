using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class GroupMessageSenderTests
{
    private readonly Group _wolves = new() { Name = "De Wolven", SlackChannelId = "C1" };
    private readonly Group _owls = new() { Name = "De Bosuilen", SlackChannelId = "C2" };
    private readonly Group _noChannel = new() { Name = "Zonder kanaal", SlackChannelId = "" };
    private readonly Mock<IGroupRepository> _groups = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly List<(string Channel, string Text)> _posted = new();

    public GroupMessageSenderTests()
    {
        _groups.Setup(g => g.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { _wolves, _owls, _noChannel });
        _slack.Setup(s => s.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string channel, string text, CancellationToken _) => _posted.Add((channel, text)))
            .ReturnsAsync((string?)null);
    }

    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1 };

    private GroupMessageSender Create() => new(_groups.Object, _slack.Object, NullLogger<GroupMessageSender>.Instance);

    [Fact]
    public async Task Post_ShouldPostOnceInTheChannelOfEachChosenGroup()
    {
        var results = await Create().PostAsync("*Let op* 🎉", new[] { _wolves.Id, _noChannel.Id });

        _posted.Should().Equal(("C1", "*Let op* 🎉"));
        results.Should().ContainSingle().Which.Should().Be(new AnnouncementPostResult("De Wolven", "C1", Posted: true, Skipped: false, Error: null));
    }

    [Fact]
    public async Task Post_ShouldReportSlackErrorsPerGroup()
    {
        _slack.Setup(s => s.PostMessageAsync("C2", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("not_in_channel");

        var results = await Create().PostAsync("Hoi", new[] { _wolves.Id, _owls.Id });

        results.Should().HaveCount(2);
        results.Single(r => r.ChannelId == "C1").Posted.Should().BeTrue();
        results.Single(r => r.ChannelId == "C2").Should().Match<AnnouncementPostResult>(r => !r.Posted && r.Error == "not_in_channel");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public async Task Post_ShouldRefuseAnEmptyMessage(string text)
    {
        GroupMessageSender.Validate(text).Should().NotBeNull();

        await Create().Invoking(s => s.PostAsync(text, new[] { _wolves.Id })).Should().ThrowAsync<ArgumentException>();
        _posted.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldRefuseATooLongMessage()
    {
        GroupMessageSender.Validate(new string('a', GroupMessageSender.MaxLength)).Should().BeNull();
        GroupMessageSender.Validate(new string('a', GroupMessageSender.MaxLength + 1)).Should().Contain("te lang");
    }

    [Fact]
    public async Task Picture_ShouldBeSentWithTheTextInEachChannel()
    {
        var shared = new List<(string Channel, string FileName, string? Text)>();
        _slack.Setup(s => s.PostFileAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string channel, byte[] _, string name, string? text, CancellationToken _) => shared.Add((channel, name, text)))
            .ReturnsAsync((string?)null);

        var results = await Create().PostAsync("Kijk!", new[] { _wolves.Id, _owls.Id }, new MessagePicture("vuur.jpg", Jpeg));

        results.Should().HaveCount(2).And.OnlyContain(r => r.Posted);
        shared.Should().Equal(("C1", "vuur.jpg", "Kijk!"), ("C2", "vuur.jpg", "Kijk!"));
        _posted.Should().BeEmpty();
    }

    [Fact]
    public void Validate_PictureWithoutText_ShouldBeAllowed()
    {
        GroupMessageSender.Validate("", new MessagePicture("vuur.jpg", Jpeg)).Should().BeNull();
        GroupMessageSender.Validate("", new MessagePicture("leeg.jpg", Array.Empty<byte>())).Should().Contain("leeg");
        GroupMessageSender.Validate("", new MessagePicture("groot.jpg", new byte[GroupMessageSender.MaxPictureBytes + 1])).Should().Contain("te groot");
    }

    [Fact]
    public void Validate_ShouldRefuseBytesThatAreNoPicture()
    {
        GroupMessageSender.Validate("", new MessagePicture("vuur.jpg", "<svg onload=alert(1)>"u8.ToArray())).Should().Be("Kies een afbeelding.");
        GroupMessageSender.Validate("", new MessagePicture("vuur.jpg", new byte[] { 1, 2, 3 })).Should().Be("Kies een afbeelding.");
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 })]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 })]
    public void IsPicture_ShouldKnowCommonPictureFormats(byte[] content) =>
        GroupMessageSender.IsPicture(content).Should().BeTrue();

    [Theory]
    [InlineData("vuur.jpg", "vuur.jpg")]
    [InlineData("../../etc/vuur.jpg", "vuur.jpg")]
    [InlineData("C:\\Users\\jury\\vuur.jpg", "vuur.jpg")]
    [InlineData("vu\u0000ur\n.jpg", "vuur.jpg")]
    [InlineData(".jpg", "foto.jpg")]
    [InlineData("", "foto")]
    [InlineData(null, "foto")]
    public void SafeFileName_ShouldKeepOnlyAPlainName(string? name, string expected) =>
        GroupMessageSender.SafeFileName(name).Should().Be(expected);

    [Fact]
    public void SafeFileName_ShouldCutALongNameButKeepTheExtension()
    {
        var name = GroupMessageSender.SafeFileName(new string('a', 300) + ".jpeg");

        name.Should().HaveLength(GroupMessageSender.MaxFileNameLength).And.EndWith(".jpeg");
    }

    [Fact]
    public async Task Picture_ShouldBeSentWithASafeName()
    {
        string? sentName = null;
        _slack.Setup(s => s.PostFileAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, byte[] _, string name, string? _, CancellationToken _) => sentName = name)
            .ReturnsAsync((string?)null);

        await Create().PostAsync("", new[] { _wolves.Id }, new MessagePicture("../geheim/vuur.jpg", Jpeg));

        sentName.Should().Be("vuur.jpg");
    }
}
