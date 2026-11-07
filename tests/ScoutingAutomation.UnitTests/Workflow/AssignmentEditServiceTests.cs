using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Assignments;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Storage;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

public class AssignmentEditServiceTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private readonly TempDirectory _temp = new();
    private readonly Infrastructure.Data.ScoutingDbContext _db = TestDb.Create();
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();
    private readonly ScoutingSettings _settings;

    public AssignmentEditServiceTests()
    {
        _settings = new ScoutingSettings
        {
            StorageRoot = Path.Combine(_temp.Path, "storage"),
            AssignmentPointsFile = Path.Combine(_temp.Path, "punten.csv")
        };
        File.WriteAllText(_settings.AssignmentPointsFile, "Opdracht,standaard,awesome,titel,beschrijving\n12,1,2,Vlot,Bouw een vlot\n");
        _db.Assignments.Add(new Assignment { Code = "12", StandardPoints = 1, AwesomePoints = 2, Title = "Vlot", Description = "Bouw een vlot" });
        _db.SaveChanges();
    }

    private AssignmentEditService Service()
    {
        var options = Options.Create(_settings);
        return new AssignmentEditService(new AssignmentRepository(_db), new AssignmentImageRepository(_db),
            new LocalFileStorageService(options, NullLogger<LocalFileStorageService>.Instance), _notifier, options, TimeProvider.System,
            NullLogger<AssignmentEditService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Save_ShouldChangeTheAssignmentAndMarkItEdited()
    {
        (await Service().SaveAsync("12", new AssignmentEdit(" Groot vlot ", " ", 3, 6), "Jury")).Should().BeNull();

        var assignment = _db.Assignments.Single();
        (assignment.Title, assignment.Description, assignment.StandardPoints, assignment.AwesomePoints).Should().Be(("Groot vlot", (string?)null, 3, 6));
        assignment.EditedAt.Should().NotBeNull();
        _notifier.Received(1).NotifyChanged();
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(1, 1001)]
    public async Task Save_Invalid_ShouldSayWhyAndChangeNothing(int standard, int awesome)
    {
        (await Service().SaveAsync("12", new AssignmentEdit("x", null, standard, awesome), "Jury")).Should().NotBeNull();

        _db.Assignments.Single().EditedAt.Should().BeNull();
    }

    [Fact]
    public async Task Save_UnknownAssignment_ShouldSayWhy()
    {
        (await Service().SaveAsync("99", new AssignmentEdit("x", null, 1, 2), "Jury")).Should().Contain("bestaat niet");
    }

    [Fact]
    public async Task Reset_ShouldPutThePointsFileBack()
    {
        var service = Service();
        await service.SaveAsync("12", new AssignmentEdit("Anders", null, 5, 9), "Jury");

        (await service.ResetAsync("12")).Should().BeNull();

        var assignment = _db.Assignments.Single();
        (assignment.Title, assignment.Description, assignment.StandardPoints, assignment.AwesomePoints, assignment.EditedAt)
            .Should().Be(("Vlot", "Bouw een vlot", 1, 2, (DateTimeOffset?)null));
    }

    [Fact]
    public async Task AddImage_ShouldStoreItOutsideTheGroupFoldersInOrder()
    {
        var service = Service();

        (await service.AddImageAsync("12", "voorbeeld.png", new MemoryStream(Png))).Should().BeNull();
        (await service.AddImageAsync("12", "tweede.PNG", new MemoryStream(Png))).Should().BeNull();

        var images = await service.GetImagesAsync("12");
        images.Select(i => i.FileName).Should().Equal("voorbeeld.png", "tweede.PNG");
        images.Should().OnlyContain(i => File.Exists(i.StoragePath));
        images[0].StoragePath.Replace('\\', '/').Should().Contain("/_opdrachten/12/");
        AssignmentEditService.ContentTypeOf(images[1].StoragePath).Should().Be("image/png");
    }

    [Theory]
    [InlineData("script.svg")]
    [InlineData("nep.png")]
    public async Task AddImage_NoPicture_ShouldBeRefused(string name)
    {
        var service = Service();

        (await service.AddImageAsync("12", name, new MemoryStream("<svg onload=alert(1)>"u8.ToArray()))).Should().NotBeNull();

        (await service.GetImagesAsync("12")).Should().BeEmpty();
        Directory.Exists(Path.Combine(_settings.StorageRoot, "_opdrachten")).Should().BeFalse();
    }

    [Fact]
    public async Task AddImage_TooLarge_ShouldBeRefused()
    {
        var big = new byte[AssignmentEditService.MaxImageBytes + 1];
        Png.CopyTo(big, 0);

        (await Service().AddImageAsync("12", "groot.png", new MemoryStream(big))).Should().Contain("te groot");
    }

    [Fact]
    public async Task MoveAndDelete_ShouldReorderAndRemoveTheFile()
    {
        var service = Service();
        await service.AddImageAsync("12", "a.png", new MemoryStream(Png));
        await service.AddImageAsync("12", "b.png", new MemoryStream(Png));
        await service.AddImageAsync("12", "c.png", new MemoryStream(Png));
        var images = await service.GetImagesAsync("12");

        await service.MoveImageAsync(images[2].Id, -1);
        (await service.GetImagesAsync("12")).Select(i => i.FileName).Should().Equal("a.png", "c.png", "b.png");

        await service.DeleteImageAsync(images[0].Id);
        (await service.GetImagesAsync("12")).Select(i => i.FileName).Should().Equal("c.png", "b.png");
        File.Exists(images[0].StoragePath).Should().BeFalse();
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, true)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, true)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, false)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, false)]
    public void LooksLikeImage_ShouldReadTheFirstBytes(byte[] data, bool expected)
    {
        AssignmentEditService.LooksLikeImage(data).Should().Be(expected);
    }
}
