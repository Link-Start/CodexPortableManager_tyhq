using System;
using System.Collections.Generic;
using System.Text;

namespace CodexPortableManager
{
    internal static partial class ModelCatalogCompatibility
    {
        internal static string DiagnoseBoundedAnalysisForTest(string executablePath)
        {
            List<string> lines = new List<string>();
            using (AsarSession session = AsarSession.Open(AsarSession.GetAsarPath(executablePath)))
            {
                SemanticModelSourceIndex index = BuildSemanticSourceIndex(session);
                lines.Add("索引来源=" + index.Sources.Count);
                foreach (SemanticModelSource source in index.Sources)
                {
                    string text = Encoding.UTF8.GetString(source.Data);
                    List<SemanticModelCandidate> candidates;
                    string reason;
                    bool bounded = TryFindBoundedSemanticCandidates(
                        source.Entry,
                        source.Data,
                        text,
                        out candidates,
                        out reason);
                    lines.Add(
                        source.Entry.Path +
                        "；大小=" + source.Data.Length +
                        "；有界=" + bounded +
                        "；" + reason);
                }
            }
            return string.Join(Environment.NewLine, lines.ToArray());
        }
    }
}
