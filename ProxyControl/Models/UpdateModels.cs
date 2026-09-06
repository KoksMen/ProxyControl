using System;
using System.Collections.Generic;

namespace ProxyControl.Models
{
    public class UpdateReleaseInfo
    {
        public string TagName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime? PublishedAt { get; set; }

        public string DisplayVersion => TagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? TagName : $"v{TagName}";
        public string DisplaySize => FileSize > 0 ? $"{(FileSize / 1024d / 1024d):F1} MB" : "Unknown size";
        public string DisplayDate => PublishedAt.HasValue ? PublishedAt.Value.ToString("MMM dd, yyyy") : string.Empty;
    }

    public class ChangelogEntry
    {
        public string Icon { get; set; } = "✨";
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Tag { get; set; } = "New"; // New, Improved, Fixed, Design
        public string TagColor { get; set; } = "#3B82F6";
    }

    public class ChangelogCategory
    {
        public string CategoryName { get; set; } = string.Empty;
        public List<ChangelogEntry> Items { get; set; } = new();
    }
}
