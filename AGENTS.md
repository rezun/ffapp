@~/code/csharp-rules.md

**Always** use the avalonia-ux skill

## Rules

- All new features require unit tests. Do not consider a feature complete without them.

## Encoder parameter changes

Adding or modifying video encoder parameters touches multiple layers. All of the following must be updated:

1. **Models** (`Core/Models/EncodingOptions.cs`) — add fields to the codec-specific `VideoEncoderOptions` and/or `QualityOptions` records (e.g. `Av1EncoderOptions`, `Av1QualityOptions`)
2. **Constants** (`Core/Validation/CompatibilityData.cs`) — add valid value lists or range constants
3. **ViewModel** (`ViewModels/QualityViewModel.cs`) — add `[ObservableProperty]` backing fields, available-options lists, and reset defaults in `UpdateForCodec`
4. **ViewModel wiring** (`ViewModels/OutputSettingsViewModel.cs`) — update `BuildVideoEncoderOptions`, `BuildQualityOptions`, `ApplyQualitySettings`, and `ApplyEncoderOptions` to pass the new fields through
5. **Command generation** (`Core/CommandBuilder.cs`) — emit the correct FFmpeg arguments in `QualityArguments`, `VideoEncoderTuningArguments`, or codec-specific helpers like `BuildSvtAv1Params`
6. **Serialization** (`Infrastructure/Serialization/SettingsSerializer.cs`) — update both serialize and deserialize for the affected encoder/quality options so presets and session restore include the new fields. Use backward-compatible defaults so existing saved data loads without errors.
7. **View** (`Views/OutputSettingsView.axaml`) — add UI controls in the appropriate codec tuning section. Use `ItemTemplate` with converters from `Converters/EnumLabelConverters.cs` for friendly display names.
8. **Test builder** (`Tests/Builders/TestOutputSettingsBuilder.cs`) — add builder helpers for the new parameters
9. **Tests** — add CommandBuilder tests (correct args emitted, edge cases) and SettingsSerializer round-trip tests (including backward compat with missing fields)

For parameters that live on `OutputSettings`/`JobDefinition` directly (e.g. `KeyframeInterval`), also update `FormatHelpers.BuildJobDefinition` to pass them through.
