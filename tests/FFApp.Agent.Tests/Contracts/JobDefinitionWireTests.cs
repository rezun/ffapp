using System.Text.Json;

using FFApp.Core.Models;
using FFApp.Remote.Contracts;
using FluentAssertions;

namespace FFApp.Agent.Tests.Contracts;

/// <summary>
/// The app sends <see cref="JobDefinition"/> and <see cref="SourceMediaInfo"/> to the agent, which
/// builds the FFmpeg commands from them. A property that is silently lost would encode with
/// different settings on the server than locally.
/// </summary>
public sealed class JobDefinitionWireTests
{
    public static TheoryData<VideoEncoderOptions, QualityOptions> EncoderVariants => WireSamples.EncoderVariants;

    [Theory]
    [MemberData(nameof(EncoderVariants))]
    public void JobDefinition_RoundTrip_KeepsEveryProperty(VideoEncoderOptions encoderOptions, QualityOptions quality)
    {
        var job = WireSamples.Job(encoderOptions, quality);
        PopulationCheck.DefaultValuedProperties(job).Should().BeEmpty("the sample must exercise every property");

        var json = JsonSerializer.Serialize(job, RemoteJsonContext.Default.JobDefinition);
        var restored = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.JobDefinition);

        restored.Should().BeEquivalentTo(job, options => options.ComparingRecordsByMembers().PreferringRuntimeMemberTypes());
        restored!.VideoEncoderOptions.Should().BeOfType(encoderOptions.GetType());
        restored.TargetQuality.Should().BeOfType(quality.GetType());
        JsonSerializer.Serialize(restored, RemoteJsonContext.Default.JobDefinition).Should().Be(json);
    }

    [Fact]
    public void SourceMediaInfo_RoundTrip_KeepsEveryProperty()
    {
        var media = WireSamples.Media();
        PopulationCheck.DefaultValuedProperties(media).Should().BeEmpty("the sample must exercise every property");

        var json = JsonSerializer.Serialize(media, RemoteJsonContext.Default.SourceMediaInfo);
        var restored = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.SourceMediaInfo);

        restored.Should().BeEquivalentTo(media, options => options.ComparingRecordsByMembers());
        restored!.VideoStreams[0].Details.Tags.Should().ContainKey("handler_name", "tag lookups are case-insensitive");
        JsonSerializer.Serialize(restored, RemoteJsonContext.Default.SourceMediaInfo).Should().Be(json);
    }

    [Fact]
    public void JobDefinition_UnknownEnumName_IsRejected()
    {
        var json = JsonSerializer.Serialize(
            WireSamples.Job(new Av1EncoderOptions(), new Av1QualityOptions()), RemoteJsonContext.Default.JobDefinition)
            .Replace("\"mkv\"", "\"avi\"", StringComparison.Ordinal);

        var act = () => JsonSerializer.Deserialize(json, RemoteJsonContext.Default.JobDefinition);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void SparseMediaInfo_IsNormalizedToUsableDefaults()
    {
        var sparse = JsonSerializer.Deserialize("""{"sourcePath":"/a.mkv","videoStreams":[{"index":0}]}""",
            RemoteJsonContext.Default.SourceMediaInfo)!;

        var media = WireNormalization.Normalize(sparse);

        media.AudioStreams.IsDefault.Should().BeFalse();
        media.Chapters.IsDefault.Should().BeFalse();
        media.VideoStreams[0].MetadataKeys.IsDefault.Should().BeFalse();
        media.VideoStreams[0].Details.Tags.Should().BeEmpty();
    }
}
