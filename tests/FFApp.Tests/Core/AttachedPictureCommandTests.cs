using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Validation;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Core;

public sealed class AttachedPictureCommandTests
{
    private readonly DefaultCommandBuilder _sut = new(new ExecutablePathsConfig());
    private static SourceMediaInfo Media() => new TestMediaInfoBuilder().WithAttachedPictures().Build();

    [Theory]
    [InlineData(VideoMode.Copy)]
    [InlineData(VideoMode.Reencode)]
    [InlineData(VideoMode.Remove)]
    public void Mp4_Default_CopiesImagesWithAttachedPictureDisposition(VideoMode mode)
    {
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s.WithVideoMode(mode)).Build();
        var command = _sut.Build(job, Media()).Should().ContainSingle().Subject;
        var args = string.Join(' ', command.Arguments);
        var firstImage = mode == VideoMode.Remove ? 0 : 1;

        args.Should().Contain("-map 0:7").And.Contain("-map 0:8")
            .And.Contain($"-c:v:{firstImage} copy -disposition:v:{firstImage} attached_pic")
            .And.Contain($"-c:v:{firstImage + 1} copy -disposition:v:{firstImage + 1} attached_pic")
            .And.NotContain("-vn");
        if (mode == VideoMode.Reencode)
            args.Should().Contain("-c:v:0 libx264").And.NotContain("-vf ");
    }

    [Fact]
    public void Mp4_Transforms_AffectOnlyPrimaryVideo()
    {
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s
            .WithResolution(ResolutionPreset.P720).WithFrameRate(FrameRatePreset.Fps24)).Build();
        var args = string.Join(' ', _sut.Build(job, Media())[0].Arguments);

        args.Should().Contain("-filter:v:0 ").And.Contain("-r:v:0 ")
            .And.Contain("-pix_fmt:v:0 ").And.Contain("-c:v:1 copy").And.Contain("-c:v:2 copy");
    }

    [Theory]
    [InlineData(OutputContainer.Mkv, VideoMode.Reencode)]
    [InlineData(OutputContainer.Mkv, VideoMode.Copy)]
    [InlineData(OutputContainer.Mka, VideoMode.Remove)]
    public void Matroska_ExtractsImagesAndAttachesOriginalFiles(OutputContainer container, VideoMode mode)
    {
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s.WithContainer(container).WithVideoMode(mode)).Build();
        var commands = _sut.Build(job, Media());

        commands.Should().HaveCount(2);
        commands[0].Protocol.Should().Be(ProcessProtocol.Plain);
        commands[0].TemporaryFiles.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        var extract = string.Join(' ', commands[0].Arguments);
        extract.Should().Contain("-map 0:7 -c:v copy -frames:v 1 -f image2 -update 1")
            .And.Contain("-map 0:8 -c:v copy -frames:v 1 -f image2 -update 1");
        var encode = string.Join(' ', commands[1].Arguments);
        foreach (var path in commands[0].TemporaryFiles)
            encode.Should().Contain($"-attach {path}");
        encode.Should().Contain("-metadata:s:t:0 mimetype=image/png -metadata:s:t:0 filename=release.png")
            .And.Contain("-metadata:s:t:1 mimetype=image/jpeg -metadata:s:t:1 filename=cover.jpg")
            .And.NotContain("-map 0:7").And.NotContain("-map 0:8");
    }

    [Theory]
    [InlineData(OutputContainer.Mkv)]
    [InlineData(OutputContainer.Mp4)]
    public void TwoPass_OnlyFinalPassIncludesPictures(OutputContainer container)
    {
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s.WithContainer(container)
            .WithTargetBitrate(1_000_000, twoPass: true)).Build();
        var commands = _sut.Build(job, Media());
        var passes = commands.Where(command => command.Protocol == ProcessProtocol.Ffmpeg).ToArray();
        passes.Should().HaveCount(2);
        string.Join(' ', passes[0].Arguments).Should().NotContain("-attach ").And.NotContain("-map 0:7")
            .And.NotContain("-map 0:8").And.NotContain("-disposition:");
        string.Join(' ', passes[1].Arguments).Should().Contain(container == OutputContainer.Mkv ? "-attach " : "-map 0:7");
    }

    [Fact]
    public void RemoveImages_MatroskaDoesNotExtractOrAttach()
    {
        var job = new TestJobDefinitionBuilder().WithOutputSettings(s => s.WithContainer(OutputContainer.Mkv).WithRemoveImages()).Build();
        var commands = _sut.Build(job, Media());
        commands.Should().ContainSingle();
        commands[0].TemporaryFiles.Should().BeEmpty();
        string.Join(' ', commands[0].Arguments).Should().NotContain("-attach ").And.NotContain("-map 0:7");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void Matroska_ImageMetadataDoesNotOverwriteOtherAttachments(bool removeData, int imageAttachmentIndex)
    {
        var media = Media() with { DataStreams = [new DataStreamInfo(4, "ttf") { IsAttachment = true }] };
        var settings = new OutputSettings(Container: OutputContainer.Mkv, VideoMode: VideoMode.Copy,
            StreamHandling: new StreamHandlingOptions(RemoveData: removeData));
        var job = new TestJobDefinitionBuilder().WithOutputSettings(settings).Build();
        var args = string.Join(' ', _sut.Build(job, media)[1].Arguments);

        args.Should().Contain($"-metadata:s:t:{imageAttachmentIndex} filename=release.png");
        if (removeData)
            args.Should().Contain("-map -0:t?");
    }

    [Fact]
    public void Mp4_UnsupportedImageCodec_RemovesOnlyUnsupportedImageAndWarns()
    {
        var media = Media() with
        {
            AttachedPictureStreams = [new AttachedPictureStreamInfo(7, "png"), new AttachedPictureStreamInfo(8, "webp")],
        };
        var settings = new OutputSettings(VideoMode: VideoMode.Copy);
        var args = string.Join(' ', _sut.Build(new TestJobDefinitionBuilder().WithOutputSettings(settings).Build(), media)[0].Arguments);

        args.Should().Contain("-map 0:7").And.NotContain("-map 0:8").And.Contain("-c:v:1 copy");
        new ValidationService().Validate(settings, media).Issues.Should().Contain(issue => issue.Code == "image_streams_not_preserved");
    }

    [Theory]
    [InlineData(OutputContainer.Webm)]
    [InlineData(OutputContainer.Ogg)]
    [InlineData(OutputContainer.OpusFile)]
    public void UnsupportedContainer_RemovesImagesWithWarning(OutputContainer container)
    {
        var settings = new TestOutputSettingsBuilder().WithContainer(container).WithVideoMode(VideoMode.Remove).Build();
        var job = new TestJobDefinitionBuilder().WithOutputSettings(settings).Build();
        string.Join(' ', _sut.Build(job, Media())[0].Arguments).Should().NotContain("-map 0:7").And.NotContain("-map 0:8");
        new ValidationService().Validate(settings, Media()).Issues.Should().Contain(issue => issue.Code == "image_streams_not_preserved");
        new ValidationService().Validate(settings with { StreamHandling = new StreamHandlingOptions(RemoveImages: true) }, Media())
            .Issues.Should().NotContain(issue => issue.Code == "image_streams_not_preserved");
    }
}
