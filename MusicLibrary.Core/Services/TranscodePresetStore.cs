using System.Collections.Immutable;
using System.Text.Json;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

public interface ITranscodePresetStore
{
    IReadOnlyList<AudioTranscodePreset> Load();

    AudioTranscodePreset Save(AudioTranscodePreset preset);

    bool Delete(Guid id);
}

public sealed class TranscodePresetStore(
    IAppSettings settings) : ITranscodePresetStore
{
    public const string PreferenceKey =
        "manager.workbench.transcode.presets.v1";
    private readonly object _sync = new();

    public IReadOnlyList<AudioTranscodePreset> Load()
    {
        lock (_sync)
            return LoadCore();
    }

    public AudioTranscodePreset Save(
        AudioTranscodePreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preset.Name);
        string name = preset.Name.Trim();
        lock (_sync)
        {
            List<AudioTranscodePreset> presets =
                [.. LoadCore()];
            if (presets.Any(item =>
                    item.Id != preset.Id &&
                    item.Name.Equals(
                        name,
                        StringComparison.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException(
                    "A transcode preset with this name already exists.");
            AudioTranscodePreset saved = preset with
            {
                Name = name,
                ModifiedAtUtc = DateTimeOffset.UtcNow,
            };
            int index = presets.FindIndex(item =>
                item.Id == saved.Id);
            if (index < 0)
                presets.Add(saved);
            else
                presets[index] = saved;
            Persist(presets);
            return saved;
        }
    }

    public bool Delete(Guid id)
    {
        lock (_sync)
        {
            List<AudioTranscodePreset> presets =
                [.. LoadCore()];
            int removed = presets.RemoveAll(item =>
                item.Id == id);
            if (removed > 0)
                Persist(presets);
            return removed > 0;
        }
    }

    private ImmutableArray<AudioTranscodePreset> LoadCore()
    {
        try
        {
            string? json = settings.GetPreference(
                PreferenceKey);
            if (string.IsNullOrWhiteSpace(json))
                return [];
            AudioTranscodePreset[]? values =
                JsonSerializer.Deserialize<
                    AudioTranscodePreset[]>(json);
            return values is null
                ? []
                : [.. values
                    .Where(item =>
                        item.Id != Guid.Empty &&
                        !string.IsNullOrWhiteSpace(item.Name))
                    .OrderBy(item =>
                        item.Name,
                        StringComparer.CurrentCultureIgnoreCase)];
        }
        catch
        {
            return [];
        }
    }

    private void Persist(
        IReadOnlyList<AudioTranscodePreset> presets) =>
        settings.SetPreference(
            PreferenceKey,
            JsonSerializer.Serialize(
                presets.OrderBy(item =>
                    item.Name,
                    StringComparer.CurrentCultureIgnoreCase)));
}
