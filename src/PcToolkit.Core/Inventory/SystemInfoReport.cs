using System.Text;

namespace PcToolkit.Core.Inventory;

public sealed record InfoItem(string Label, string Value);

public sealed record InfoSection(string Title, IReadOnlyList<InfoItem> Items);

/// <summary>Сведения о компьютере для экрана «Система» и для экспорта в текстовый файл.</summary>
public sealed record SystemInfoReport(DateTime CreatedAt, IReadOnlyList<InfoSection> Sections)
{
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine("PC Toolkit — отчёт о системе");
        text.AppendLine($"Создан: {CreatedAt:dd.MM.yyyy HH:mm:ss}");

        foreach (InfoSection section in Sections)
        {
            text.AppendLine();
            text.AppendLine($"== {section.Title} ==");
            foreach (InfoItem item in section.Items)
                text.AppendLine($"{item.Label}: {item.Value}");
        }

        return text.ToString();
    }
}
