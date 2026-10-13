using System.Collections.Generic;
using System.Linq;
using Kanal.Core.Models;
using Kanal.Host.Services;
using Kanal.Providers.LocalAsr;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.ViewModels;

public sealed class ModelRows
{
    private static ModelRows? _shared;

    public ModelRows(ModelDownloadManager downloads)
    {
        Translation = [new ModelItemViewModel(), .. LocalModelCatalog.Models.Select(m => new ModelItemViewModel(m, downloads))];
        Transcription = [.. AsrModelCatalog.Models.Select(m => new ModelItemViewModel(m, downloads))];
    }

    public static ModelRows Shared => _shared ??= new ModelRows(new ModelDownloadManager(SettingsStore.ModelsPath));

    public IReadOnlyList<ModelItemViewModel> Translation { get; }

    public IReadOnlyList<ModelItemViewModel> Transcription { get; }

    public void CancelAll()
    {
        foreach (var row in Translation.Concat(Transcription))
            row.CancelDownload();
    }
}
