using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LawWatcher.BuildingBlocks.Ports;

namespace LawWatcher.AiEnrichment.Infrastructure;

public sealed class ContainerizedDocumentOcrService(IDocumentStore documentStore) : IOcrService
{
    public async Task<OcrResult> ExtractAsync(StoredDocumentReference document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        await using var content = await documentStore.OpenReadAsync(document, cancellationToken);
        if (IsPlainText(document))
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var extractedText = await reader.ReadToEndAsync(cancellationToken);
            return new OcrResult(extractedText.Trim(), []);
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "lawwatcher-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var sourcePath = Path.Combine(tempDirectory, CreateTemporarySourceFileName(document.ObjectKey));
            await using (var destination = new FileStream(
                sourcePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }

            return await ExtractFromFileAsync(sourcePath, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static bool IsPlainText(StoredDocumentReference document)
    {
        var contentType = document.ContentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(document.ObjectKey), ".txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(document.ObjectKey), ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(document.ObjectKey), ".html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(document.ObjectKey), ".json", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateTemporarySourceFileName(string objectKey)
    {
        var extension = Path.GetExtension(objectKey);
        return extension.Length == 0
            ? "source.bin"
            : "source" + extension.ToLowerInvariant();
    }

    private static async Task<OcrResult> ExtractFromFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath);
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => await ExtractFromPdfAsync(sourcePath, cancellationToken),
            ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".webp"
                => await ExtractFromImageAsync(sourcePath, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported OCR source extension '{extension}'.")
        };
    }

    private static async Task<OcrResult> ExtractFromPdfAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var directTextResult = await RunToolAsync(
            "pdftotext",
            ["-layout", sourcePath, "-"],
            cancellationToken);
        var directText = NormalizeExtractedText(directTextResult.StandardOutput);
        if (directText.Length != 0)
        {
            warnings.Add("pdf-text-extraction");
            return new OcrResult(directText, warnings);
        }

        warnings.Add("pdf-text-empty-falling-back-to-ocr");

        var renderedPagePrefix = Path.Combine(Path.GetDirectoryName(sourcePath)!, "page");
        await RunToolAsync(
            "pdftoppm",
            ["-png", sourcePath, renderedPagePrefix],
            cancellationToken);

        var renderedPages = Directory.EnumerateFiles(
                Path.GetDirectoryName(sourcePath)!,
                "page-*.png",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (renderedPages.Length == 0)
        {
            return new OcrResult(string.Empty, warnings);
        }

        var pageOutputs = new List<string>(renderedPages.Length);
        foreach (var renderedPage in renderedPages)
        {
            var pageResult = await RunToolAsync(
                "tesseract",
                [renderedPage, "stdout", "--dpi", "300"],
                cancellationToken);
            pageOutputs.Add(pageResult.StandardOutput);
        }

        warnings.Add("pdf-ocr");
        return new OcrResult(NormalizeExtractedText(string.Join(Environment.NewLine, pageOutputs)), warnings);
    }

    private static async Task<OcrResult> ExtractFromImageAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var toolResult = await RunToolAsync(
            "tesseract",
            [sourcePath, "stdout", "--dpi", "300"],
            cancellationToken);

        return new OcrResult(
            NormalizeExtractedText(toolResult.StandardOutput),
            ["image-ocr"]);
    }

    private static string NormalizeExtractedText(string extractedText) =>
        extractedText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();

    private static async Task<ToolExecutionResult> RunToolAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"OCR runtime tool '{fileName}' is unavailable in the current environment.",
                ex);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"OCR runtime tool '{fileName}' failed with exit code {process.ExitCode}: {standardError.Trim()}");
        }

        return new ToolExecutionResult(standardOutput, standardError);
    }

    private sealed record ToolExecutionResult(
        string StandardOutput,
        string StandardError);
}
