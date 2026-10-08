using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PrinterAPP.Services;

public partial class PrintUpdateJobStore
{
    public string? LoadUpdateCursor()
    {
        lock (_gate)
        {
            EnsureCursorLoaded();
            return _updateCursor;
        }
    }

    public bool TryAdvanceUpdateCursor(string? cursor)
    {
        lock (_gate)
        {
            EnsureCursorLoaded();
            if (cursor is not null && string.IsNullOrWhiteSpace(cursor))
                return false;
            try
            {
                var directory = Path.GetDirectoryName(CursorFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var temp = CursorFilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(cursor, JsonOptions));
                File.Move(temp, CursorFilePath, overwrite: true);
                _updateCursor = cursor;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist update-feed cursor");
                return false;
            }
        }
    }

    private void EnsureCursorLoaded()
    {
        if (_cursorLoaded)
            return;

        _cursorLoaded = true;
        try
        {
            if (File.Exists(CursorFilePath))
            {
                _updateCursor = JsonSerializer.Deserialize<string>(
                    File.ReadAllText(CursorFilePath), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update-feed cursor unreadable; starting from the initial boundary");
            _updateCursor = null;
        }
    }
}
