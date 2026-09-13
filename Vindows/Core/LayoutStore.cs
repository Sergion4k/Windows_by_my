using System.IO;
using System.Text.Json;

namespace Vindows.Core;

/// <summary>Сохранение и загрузка раскладки в JSON (%APPDATA%\Vindows\layout.json).</summary>
public static class LayoutStore
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Vindows", "layout.json");

    /// <summary>Резервная копия и временный файл для атомарной записи.</summary>
    private static readonly string BackupPath = FilePath + ".bak";
    private static readonly string TempPath = FilePath + ".tmp";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static LayoutFile Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var layout = TryParse(File.ReadAllText(FilePath));
                if (layout != null) return layout;

                // Основной файл повреждён (например, запись оборвалась) — пробуем резервную копию.
                DebugLog.Line("LayoutStore.Load: layout.json повреждён — пробую резервную копию");
                if (File.Exists(BackupPath))
                {
                    var backup = TryParse(File.ReadAllText(BackupPath));
                    if (backup != null) return backup;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Line("LayoutStore.Load: " + ex.Message);
        }
        return new LayoutFile();
    }

    /// <summary>
    /// Атомарно сохраняет раскладку: пишем во временный файл и заменяем основной, чтобы сбой
    /// посреди записи не оставлял повреждённый layout.json (предыдущая версия уходит в .bak).
    /// Возвращает false, если сохранить не удалось (например, нет прав на запись).
    /// </summary>
    public static bool Save(LayoutFile layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(TempPath, JsonSerializer.Serialize(layout, Options));

            if (File.Exists(FilePath))
                File.Replace(TempPath, FilePath, BackupPath);
            else
                File.Move(TempPath, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            // Нет прав на запись — молча пропускаем, раскладка остаётся в памяти.
            DebugLog.Line("LayoutStore.Save: " + ex.Message);
            TryDeleteTemp();
            return false;
        }
    }

    private static void TryDeleteTemp()
    {
        try
        {
            if (File.Exists(TempPath)) File.Delete(TempPath);
        }
        catch
        {
        }
    }

    private static LayoutFile? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LayoutFile>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
