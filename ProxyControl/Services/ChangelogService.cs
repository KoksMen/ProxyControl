using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ProxyControl.Models;

namespace ProxyControl.Services
{
    public class ChangelogService
    {
        public static List<ChangelogCategory> GetReleaseNotes(string version)
        {
            var categories = new List<ChangelogCategory>();

            var uiCategory = new ChangelogCategory { CategoryName = "🪟 Верхняя панель и заголовок окна" };
            uiCategory.Items.Add(new ChangelogEntry
            {
                Icon = "🪟",
                Title = "Кастомный заголовок окна (TitleBar)",
                Description = "Современная темная панель с нативным перетягиванием, изменением размеров (рамка 6px), кнопками Свернуть / Развернуть / Закрыть и защитой от перекрытия панели задач при максимизации.",
                Tag = "New",
                TagColor = "#10B981"
            });
            uiCategory.Items.Add(new ChangelogEntry
            {
                Icon = "📌",
                Title = "Закрепление поверх окон (Pin on Top)",
                Description = "Возможность закрепить окно поверх остальных приложений одной кнопкой на панели заголовка для удобного контроля во время игр или работы.",
                Tag = "New",
                TagColor = "#3B82F6"
            });
            uiCategory.Items.Add(new ChangelogEntry
            {
                Icon = "⚡",
                Title = "Быстрые действия (Ping All и To Tray)",
                Description = "Проверка задержки и доступности всех прокси в один клик и мгновенное сворачивание в трей прямо из шапки программы.",
                Tag = "New",
                TagColor = "#6366F1"
            });

            var monitorCategory = new ChangelogCategory { CategoryName = "📊 Монитор сетевого трафика и процессов" };
            monitorCategory.Items.Add(new ChangelogEntry
            {
                Icon = "🚀",
                Title = "Активная скорость процессов",
                Description = "Отображение текущей скорости отдачи и скачивания (↓ / ↑) с высокоточным замером времени каждые 500 мс.",
                Tag = "Feature",
                TagColor = "#8B5CF6"
            });
            monitorCategory.Items.Add(new ChangelogEntry
            {
                Icon = "📦",
                Title = "Суммарный потраченный трафик",
                Description = "Детальный подсчет и форматирование общего переданного объема данных (в МБ/ГБ) для каждого процесса и сессии.",
                Tag = "Feature",
                TagColor = "#8B5CF6"
            });
            monitorCategory.Items.Add(new ChangelogEntry
            {
                Icon = "🎨",
                Title = "Исправление наложения иконок",
                Description = "Устранено просвечивание фоновых заглушек под прозрачными иконками приложений и веб-сайтов.",
                Tag = "Fixed",
                TagColor = "#F59E0B"
            });

            var headerCategory = new ChangelogCategory { CategoryName = "🧭 Панель управления и карточки режима" };
            headerCategory.Items.Add(new ChangelogEntry
            {
                Icon = "📐",
                Title = "Гармонизация и порядок карточек",
                Description = "Единая высота всех карточек (68px) и последовательность: 1: Routing Mode, 2: Traffic Routing, 3: Proxy Service, 4: Live Network, 5: Routing Behavior.",
                Tag = "Improved",
                TagColor = "#06B6D4"
            });
            headerCategory.Items.Add(new ChangelogEntry
            {
                Icon = "◀",
                Title = "Понятная иконка сворачивания сайдбара",
                Description = "Заменен нечитаемый символ на аккуратные стрелки сворачивания и разворачивания панели прокси.",
                Tag = "Fixed",
                TagColor = "#F59E0B"
            });

            var hotkeysCategory = new ChangelogCategory { CategoryName = "⌨️ Горячие клавиши и управление" };
            hotkeysCategory.Items.Add(new ChangelogEntry
            {
                Icon = "⌨️",
                Title = "Глобальные клавиатурные сокращения",
                Description = "Ctrl+1..4 для перехода по разделам, Ctrl+Tab для переключения вкладок, Esc для отмены/закрытия модалок, Enter для подтверждения, Ctrl+N для добавления правила, F5 для обновления.",
                Tag = "New",
                TagColor = "#10B981"
            });

            var settingsCategory = new ChangelogCategory { CategoryName = "⚙️ Настройки и обновления" };
            settingsCategory.Items.Add(new ChangelogEntry
            {
                Icon = "⚙️",
                Title = "Адаптивный экран настроек",
                Description = "Оптимальная ширина и центрирование блоков для комфортной работы на мониторах любого разрешения.",
                Tag = "Design",
                TagColor = "#EC4899"
            });
            settingsCategory.Items.Add(new ChangelogEntry
            {
                Icon = "🔔",
                Title = "Информативные экраны обновлений",
                Description = "Отображение списка изменений при нахождении новой версии, а также при первом запуске после обновления.",
                Tag = "New",
                TagColor = "#10B981"
            });

            categories.Add(uiCategory);
            categories.Add(monitorCategory);
            categories.Add(headerCategory);
            categories.Add(hotkeysCategory);
            categories.Add(settingsCategory);

            return categories;
        }

        public static List<ChangelogEntry> ParseMarkdownChangelog(string markdown)
        {
            var entries = new List<ChangelogEntry>();
            if (string.IsNullOrWhiteSpace(markdown)) return entries;

            var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith("-") || line.StartsWith("*") || line.StartsWith("•"))
                {
                    line = line.TrimStart('-', '*', '•', ' ').Trim();

                    string tag = "New";
                    string tagColor = "#3B82F6";
                    string icon = "✨";

                    if (line.StartsWith("[New]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("New:", StringComparison.OrdinalIgnoreCase))
                    {
                        tag = "New";
                        tagColor = "#10B981";
                        icon = "✨";
                        line = Regex.Replace(line, @"^\[?New\]?:?\s*", "", RegexOptions.IgnoreCase);
                    }
                    else if (line.StartsWith("[Fix]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("[Fixed]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase))
                    {
                        tag = "Fixed";
                        tagColor = "#F59E0B";
                        icon = "🛠️";
                        line = Regex.Replace(line, @"^\[?Fixed?\]?:?\s*", "", RegexOptions.IgnoreCase);
                    }
                    else if (line.StartsWith("[Improvement]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("[Improved]", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Improved:", StringComparison.OrdinalIgnoreCase))
                    {
                        tag = "Improved";
                        tagColor = "#06B6D4";
                        icon = "⚡";
                        line = Regex.Replace(line, @"^\[?Improved?\]?:?\s*", "", RegexOptions.IgnoreCase);
                    }

                    string title = line;
                    string desc = string.Empty;
                    int colonIndex = line.IndexOf(':');
                    if (colonIndex > 0 && colonIndex < 40)
                    {
                        title = line.Substring(0, colonIndex).Trim();
                        desc = line.Substring(colonIndex + 1).Trim();
                    }

                    entries.Add(new ChangelogEntry
                    {
                        Icon = icon,
                        Title = title,
                        Description = desc,
                        Tag = tag,
                        TagColor = tagColor
                    });
                }
            }

            return entries;
        }
    }
}
