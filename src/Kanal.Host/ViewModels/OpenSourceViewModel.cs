using System.Collections.Generic;
using Kanal.Host.Localization;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

public sealed class OpenSourceViewModel : ViewModelBase
{
    public IReadOnlyList<OpenSourceNotice> Notices => OpenSourceNotices.All;

    public string LicenseNote =>
        Localizer.Instance.Format("licenses.note", OpenSourceNotices.OwnLicense);
}
