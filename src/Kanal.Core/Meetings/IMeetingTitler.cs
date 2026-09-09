using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Kanal.Core.Meetings;

public interface IMeetingTitler
{
    Task<string?> SuggestAsync(IReadOnlyList<string> lines, CancellationToken ct);
}
