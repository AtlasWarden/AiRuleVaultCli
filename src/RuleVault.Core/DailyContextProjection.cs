using System.Text.RegularExpressions;

namespace RuleVault.Core;

public static class DailyContextProjection
{
    // Unknown layouts stay intact. Only a recognized trailing session log is omitted;
    // all current handoff text and any other top-level sections remain authoritative.
    public static string Render(string content, bool includeSessions)
    {
        if (includeSessions) { return content; }
        var headings = new List<(string Text, int Offset)>();
        char fence = '\0';
        var fenceLength = 0;
        var offset = 0;
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').TrimStart(' ');
            var marker = Regex.Match(trimmed, @"^(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant);
            if (marker.Success)
            {
                var run = marker.Groups[1].Value;
                if (fence == '\0') { fence = run[0]; fenceLength = run.Length; }
                else if (run[0] == fence && run.Length >= fenceLength && string.IsNullOrWhiteSpace(marker.Groups[2].Value)) { fence = '\0'; }
            }
            else if (fence == '\0' && Regex.IsMatch(trimmed, @"^#{1,2} ", RegexOptions.CultureInvariant))
            {
                headings.Add((trimmed.TrimEnd(), offset));
            }
            offset += line.Length + 1;
        }
        if (fence != '\0' || headings.Count == 0 || headings[^1].Text != "## Sessions" ||
            headings.Count(item => item.Text == "## Sessions") != 1 ||
            headings.Count(item => item.Text == "## Current Handoff") != 1)
        {
            return content;
        }
        return content[..headings[^1].Offset].TrimEnd() +
            "\n\n[Trailing session history omitted. Request subject daily or --include-history true for the full active note.]\n";
    }
}
