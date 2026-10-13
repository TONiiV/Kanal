using System.Collections.Generic;
using System.Linq;
using Kanal.Core.Models;
using Kanal.Providers.LocalAsr;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.ViewModels;

public sealed class ModelRows
{
    public ModelRows(ModelDownloadManager downloads)
    {
        Translation = [new ModelItemViewModel(), .. LocalModelCatalog.Models.Select(m => new ModelItemViewModel(m, downloads))];
        Transcription = [.. AsrModelCatalog.Models.Select(m => new ModelItemViewModel(m, downloads))];
    }

    public IReadOnlyList<ModelItemViewModel> Translation { get; }

    public IReadOnlyList<ModelItemViewModel> Transcription { get; }

    public void CancelAll()
    {
        foreach (var row in Translation.Concat(Transcription))
            row.CancelDownload();
    }
}
