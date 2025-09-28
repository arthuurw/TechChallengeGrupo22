using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace VisionaryAnalytics.Api.Options;

public sealed class UploadOptions
{
    private static readonly string[] DefaultExtensions = [".mp4", ".avi"];

    [Required]
    [MinLength(1)]
    public string RootPath { get; set; } = "/data/videos";

    [Range(1, long.MaxValue)]
    public long MaxFileSizeBytes { get; set; } = 524_288_000; // 500 MB

    [MinLength(1)]
    public List<string> AllowedExtensions { get; set; } = new(DefaultExtensions);

    [JsonIgnore]
    public IReadOnlySet<string> AllowedExtensionsSet { get; private set; } = new HashSet<string>(DefaultExtensions, StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(RootPath) ? "/data/videos" : RootPath);

        AllowedExtensionsSet = AllowedExtensions
            .Where(static ext => !string.IsNullOrWhiteSpace(ext))
            .Select(static ext => ext.StartsWith('.') ? ext.ToLowerInvariant() : $".{ext.ToLowerInvariant()}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (AllowedExtensionsSet.Count == 0)
        {
            throw new InvalidOperationException("Pelo menos uma extensão de arquivo deve ser permitida.");
        }

        AllowedExtensions = AllowedExtensionsSet.ToList();
    }
}
