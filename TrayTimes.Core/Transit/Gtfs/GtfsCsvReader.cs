using System.Text;

namespace TrayTimes.Transit.Gtfs;

/// <summary>Reads one GTFS table: a header row naming the columns, then one record per line.</summary>
internal sealed class GtfsCsvReader
{
    private readonly TextReader _reader;
    private readonly Dictionary<string, int> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _table;
    private string[] _fields = [];

    public GtfsCsvReader(TextReader reader, string table)
    {
        _reader = reader;
        _table = table;
        string[] header = Split(reader.ReadLine() ?? string.Empty);
        for (int i = 0; i < header.Length; i++)
        {
            _columns.TryAdd(header[i].Trim(), i);
        }
    }

    /// <summary>The index of a column, or -1 when the table doesn't have it.</summary>
    public int Column(string name) => _columns.GetValueOrDefault(name, -1);

    /// <summary>The index of a column the GTFS spec requires.</summary>
    public int RequiredColumn(string name) => Column(name) is var index and >= 0
        ? index
        : throw new InvalidDataException($"The schedule's {_table} has no {name} column.");

    /// <summary>The current record's value in a column, trimmed; empty when missing.</summary>
    public string this[int column] => column >= 0 && column < _fields.Length ? _fields[column].Trim() : string.Empty;

    /// <summary>Moves to the next record, skipping blank lines.</summary>
    public bool Read()
    {
        string? line;
        do
        {
            line = _reader.ReadLine();
            if (line is null)
            {
                return false;
            }
        }
        while (line.Length == 0);

        _fields = Split(line);
        return true;
    }

    /// <summary>Splits one CSV line, honoring double-quoted fields with "" escapes.</summary>
    internal static string[] Split(string line)
    {
        if (!line.Contains('"', StringComparison.Ordinal))
        {
            return line.Split(',');
        }

        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }
}
