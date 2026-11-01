using System.Text;

namespace TapeDrop.Probe;

/// <summary>Writes to the console and keeps a copy so it can be saved to a file to paste back.</summary>
public sealed class ProbeReport
{
    private readonly StringBuilder _text = new();

    public void Line(string text = "")
    {
        Console.WriteLine(text);
        _text.AppendLine(text);
    }

    public void Section(string title)
    {
        Line();
        Line("== " + title + " ==");
    }

    public override string ToString() => _text.ToString();

    /// <summary>Saves next to where the tool was run, or in the temp folder if that is not writable.</summary>
    public string Save(string prefix)
    {
        var name = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        foreach (var folder in new[] { Directory.GetCurrentDirectory(), Path.GetTempPath() })
        {
            try
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, _text.ToString());
                return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next folder
            }
        }
        return "(could not save the report file)";
    }
}
