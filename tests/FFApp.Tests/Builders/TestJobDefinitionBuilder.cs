using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Helpers;

namespace FFApp.Tests.Builders;

public class TestJobDefinitionBuilder
{
    private string _sourcePath = "/test/source.mp4";
    private string _outputPath = "/test/output.mp4";
    private OutputSettings _settings = new();

    private ImmutableArray<int>? _selectedAudioStreamIndices;

    public TestJobDefinitionBuilder WithAudioTracks(params int[] indices)
    {
        _selectedAudioStreamIndices = [.. indices];
        return this;
    }

    public JobDefinition Build() =>
        FormatHelpers.BuildJobDefinition(_sourcePath, _outputPath, _settings, selectedAudioStreamIndices: _selectedAudioStreamIndices);

    public TestJobDefinitionBuilder WithSourcePath(string path) { _sourcePath = path; return this; }
    public TestJobDefinitionBuilder WithOutputPath(string path) { _outputPath = path; return this; }

    public TestJobDefinitionBuilder WithOutputSettings(OutputSettings settings) { _settings = settings; return this; }

    public TestJobDefinitionBuilder WithOutputSettings(Action<TestOutputSettingsBuilder> configure)
    {
        var builder = new TestOutputSettingsBuilder();
        configure(builder);
        _settings = builder.Build();
        return this;
    }
}
