using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed record LlmModelDiscoveryResult(IReadOnlyList<string> Models, string Detail, bool UsedFallback);
