using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace BambuStripper
{
    public partial class MainWindow : Window
    {
        private string? _selectedFilePath;
        private string? _selectedFolderPath;
        private CancellationTokenSource? _cancellationTokenSource;
        
        // Metadata names to clear
        private static readonly HashSet<string> MetadataNamesToClear = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CopyRight",
            "Copyright",
            "Description",
            "DesignModelId",
            "DesignProfileId",
            "DesignerUserId",
            "License",
            "ProfileDescription",
            "ProfileUserId",
            "ProfileUserName"
        };
        
        // Metadata names to copy (but not clear)
        private static readonly HashSet<string> MetadataNamesToCopy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Title"
        };

        public MainWindow()
        {
            InitializeComponent();
            Log("Ready. Select a .3mf file or folder to begin.");
        }

        private void BrowseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "3MF Files (*.3mf)|*.3mf|All Files (*.*)|*.*",
                Title = "Select a .3mf file"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _selectedFilePath = openFileDialog.FileName;
                _selectedFolderPath = null;
                FilePathTextBox.Text = _selectedFilePath;
                RemoveProjectInfoButton.IsEnabled = true;
                Log($"Selected file: {Path.GetFileName(_selectedFilePath)}");
            }
        }

        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var folderDialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                folderDialog.Description = "Select a folder containing .3mf files";
                folderDialog.ShowNewFolderButton = true;

                if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    _selectedFolderPath = folderDialog.SelectedPath;
                    _selectedFilePath = null;
                    FilePathTextBox.Text = _selectedFolderPath;
                    RemoveProjectInfoButton.IsEnabled = true;
                    Log($"Selected folder: {_selectedFolderPath}");
                }
            }
        }

        private async void RemoveProjectInfoButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetProcessingState(true);
                _cancellationTokenSource = new CancellationTokenSource();
                
                if (!string.IsNullOrEmpty(_selectedFilePath) && File.Exists(_selectedFilePath))
                {
                    await ProcessFileAsync(_selectedFilePath, _cancellationTokenSource.Token);
                }
                else if (!string.IsNullOrEmpty(_selectedFolderPath) && Directory.Exists(_selectedFolderPath))
                {
                    await ProcessFolderAsync(_selectedFolderPath, _cancellationTokenSource.Token);
                }
                else
                {
                    MessageBox.Show("Please select a valid .3mf file or folder.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                Log("Processing cancelled by user.");
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetProcessingState(false);
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
            Log("Cancellation requested...");
        }

        private void SetProcessingState(bool isProcessing)
        {
            Dispatcher.Invoke(() =>
            {
                BrowseFileButton.IsEnabled = !isProcessing;
                BrowseFolderButton.IsEnabled = !isProcessing;
                RemoveProjectInfoButton.IsEnabled = !isProcessing && (!string.IsNullOrEmpty(_selectedFilePath) || !string.IsNullOrEmpty(_selectedFolderPath));
                ClearButton.IsEnabled = !isProcessing;
                
                ProgressBar.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
                CancelButton.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
                CancelButton.IsEnabled = isProcessing;
            });
        }

        private async Task ProcessFolderAsync(string folderPath, CancellationToken cancellationToken)
        {
            Log($"Scanning folder for .3mf files...");
            var files = Directory.GetFiles(folderPath, "*.3mf", SearchOption.TopDirectoryOnly);
            
            if (files.Length == 0)
            {
                MessageBox.Show("No .3mf files found in the selected folder.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Log($"Found {files.Length} .3mf file(s)");
            int processed = 0;
            int errors = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    await ProcessFileAsync(file, cancellationToken);
                    processed++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"Error processing {Path.GetFileName(file)}: {ex.Message}");
                    errors++;
                }
            }

            Log($"Folder processing complete! Processed: {processed}, Errors: {errors}");
            MessageBox.Show(
                $"Folder processing complete!\n\nProcessed: {processed} file(s)\nErrors: {errors} file(s)",
                "Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        private async Task ProcessFileAsync(string filePath, CancellationToken cancellationToken)
        {
            Log($"Starting file processing: {Path.GetFileName(filePath)}");

            // Create backup in Backup folder
            string fileDirectory = Path.GetDirectoryName(filePath) ?? "";
            string backupDirectory = Path.Combine(fileDirectory, "Backup");
            Directory.CreateDirectory(backupDirectory);
            
            string backupFileName = Path.GetFileName(filePath);
            string backupPath = Path.Combine(backupDirectory, backupFileName);
            File.Copy(filePath, backupPath, true);
            Log($"Created backup: {backupPath}");

            // Use temporary file path for output (to avoid write lock on original)
            string tempOutputPath = Path.Combine(
                Path.GetDirectoryName(filePath) ?? "",
                Path.GetFileNameWithoutExtension(filePath) + "_modified.3mf"
            );

            // Store removed metadata
            var removedMetadata = new List<(string Name, string Value)>();

            // Read from original file and write to temporary file
            using (var archive = ZipFile.Open(filePath, ZipArchiveMode.Read))
            {
                using (var outputArchive = new ZipArchive(File.Create(tempOutputPath), ZipArchiveMode.Create))
                {
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string entryPath = entry.FullName.Replace('\\', '/');
                        
                        // Check if this is a 3dmodel.model file
                        if (entryPath.EndsWith("3dmodel.model", StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"Processing: {entryPath}");
                            
                            // Read and modify the XML
                            using (var entryStream = entry.Open())
                            {
                                XDocument doc = XDocument.Load(entryStream);
                                
                                // Find metadata nodes with specific names, capture values, then set them to blank
                                var metadataNodes = doc.Descendants()
                                    .Where(e => e.Name.LocalName == "metadata")
                                    .ToList();
                                
                                foreach (var metadataNode in metadataNodes)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    
                                    var nameAttribute = metadataNode.Attribute("name");
                                    if (nameAttribute != null)
                                    {
                                        // Copy Title but don't clear it
                                        if (MetadataNamesToCopy.Contains(nameAttribute.Value))
                                        {
                                            string value = DecodeXmlEntities(metadataNode.Value);
                                            if (!string.IsNullOrWhiteSpace(value))
                                            {
                                                removedMetadata.Add((nameAttribute.Value, value));
                                            }
                                            // Don't clear - leave it in the file
                                        }
                                        // Clear other metadata
                                        else if (MetadataNamesToClear.Contains(nameAttribute.Value))
                                        {
                                            string value = DecodeXmlEntities(metadataNode.Value);
                                            if (!string.IsNullOrWhiteSpace(value))
                                            {
                                                removedMetadata.Add((nameAttribute.Value, value));
                                            }
                                            metadataNode.Value = "";
                                            Log($"  Cleared metadata node: {nameAttribute.Value}");
                                        }
                                    }
                                }
                                
                                // Write modified XML to output archive
                                var newEntry = outputArchive.CreateEntry(entryPath);
                                using (var newEntryStream = newEntry.Open())
                                {
                                    doc.Save(newEntryStream);
                                }
                            }
                        }
                        else
                        {
                            // Copy other entries as-is
                            var newEntry = outputArchive.CreateEntry(entryPath);
                            using (var entryStream = entry.Open())
                            using (var newEntryStream = newEntry.Open())
                            {
                                entryStream.CopyTo(newEntryStream);
                            }
                        }
                    }
                }
            }

            // Close the read handle before attempting to replace the original file
            // Now replace the original file: delete original, then rename temp file
            cancellationToken.ThrowIfCancellationRequested();
            
            // Delete the original file
            File.Delete(filePath);
            Log($"Deleted original file: {Path.GetFileName(filePath)}");
            
            // Rename the temporary file to the original name
            File.Move(tempOutputPath, filePath);
            Log($"Renamed temporary file to: {Path.GetFileName(filePath)}");

            // Generate HTML file with removed metadata
            if (removedMetadata.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string htmlPath = Path.Combine(
                    fileDirectory,
                    Path.GetFileNameWithoutExtension(filePath) + ".html"
                );
                await GenerateHtmlFileAsync(htmlPath, removedMetadata, filePath, cancellationToken);
                Log($"Created HTML file: {Path.GetFileName(htmlPath)}");
            }

            Log($"Processing complete!");
            Log($"File updated: {Path.GetFileName(filePath)}");
            
            // Only show message box for single file processing (not folder processing)
            if (!string.IsNullOrEmpty(_selectedFilePath))
            {
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"File processed successfully!\n\nFile updated: {Path.GetFileName(filePath)}\nBackup: {Path.GetFileName(backupPath)}",
                        "Success",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                });
            }
        }

        private async Task GenerateHtmlFileAsync(string htmlPath, List<(string Name, string Value)> metadata, string original3mfPath, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                var html = new StringBuilder();
                html.AppendLine("<!DOCTYPE html>");
                html.AppendLine("<html lang=\"en\">");
                html.AppendLine("<head>");
                html.AppendLine("    <meta charset=\"UTF-8\">");
                html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
                html.AppendLine($"    <title>Removed Metadata - {Path.GetFileName(original3mfPath)}</title>");
                html.AppendLine("    <style>");
                html.AppendLine("        body { font-family: Arial, sans-serif; margin: 20px; background-color: #f5f5f5; }");
                html.AppendLine("        .container { max-width: 1200px; margin: 0 auto; background-color: white; padding: 20px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
                html.AppendLine("        h1 { color: #333; border-bottom: 2px solid #4CAF50; padding-bottom: 10px; }");
                html.AppendLine("        .metadata-item { margin: 20px 0; padding: 15px; background-color: #f9f9f9; border-left: 4px solid #4CAF50; }");
                html.AppendLine("        .metadata-name { font-weight: bold; color: #4CAF50; font-size: 1.1em; margin-bottom: 8px; }");
                html.AppendLine("        .metadata-value { color: #666; white-space: pre-wrap; word-wrap: break-word; }");
                html.AppendLine("        img { max-width: 100%; height: auto; margin: 10px 0; border: 1px solid #ddd; border-radius: 4px; }");
                html.AppendLine("        .image-container { margin: 10px 0; }");
                html.AppendLine("    </style>");
                html.AppendLine("</head>");
                html.AppendLine("<body>");
                html.AppendLine("    <div class=\"container\">");
                html.AppendLine($"        <h1>Removed Metadata from {Path.GetFileName(original3mfPath)}</h1>");
                html.AppendLine($"        <p><strong>Generated:</strong> {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
                html.AppendLine("        <hr>");

                // Separate metadata into categories
                var titleMetadata = metadata.Where(m => m.Name.Equals("Title", StringComparison.OrdinalIgnoreCase)).ToList();
                var descriptionMetadata = metadata.Where(m => m.Name.Equals("Description", StringComparison.OrdinalIgnoreCase)).ToList();
                var copyrightMetadata = metadata.Where(m => m.Name.Equals("Copyright", StringComparison.OrdinalIgnoreCase) || m.Name.Equals("CopyRight", StringComparison.OrdinalIgnoreCase)).ToList();
                var otherMetadata = metadata.Where(m => 
                    !m.Name.Equals("Title", StringComparison.OrdinalIgnoreCase) &&
                    !m.Name.Equals("Description", StringComparison.OrdinalIgnoreCase) &&
                    !m.Name.Equals("Copyright", StringComparison.OrdinalIgnoreCase) &&
                    !m.Name.Equals("CopyRight", StringComparison.OrdinalIgnoreCase)).ToList();

                // Render Title first
                foreach (var (name, value) in titleMetadata)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendMetadataItem(html, name, value, original3mfPath);
                }

                // Render Description second
                foreach (var (name, value) in descriptionMetadata)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendMetadataItem(html, name, value, original3mfPath);
                }

                // Render other metadata
                foreach (var (name, value) in otherMetadata)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendMetadataItem(html, name, value, original3mfPath);
                }

                // Render Copyright at the bottom
                foreach (var (name, value) in copyrightMetadata)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendMetadataItem(html, name, value, original3mfPath);
                }

                html.AppendLine("    </div>");
                html.AppendLine("</body>");
                html.AppendLine("</html>");

                File.WriteAllText(htmlPath, html.ToString(), Encoding.UTF8);
            }, cancellationToken);
        }

        private void AppendMetadataItem(StringBuilder html, string name, string value, string original3mfPath)
        {
            html.AppendLine("        <div class=\"metadata-item\">");
            html.AppendLine($"            <div class=\"metadata-name\">{WebUtility.HtmlEncode(name)}</div>");
            html.AppendLine("            <div class=\"metadata-value\">");
            
            // Process the value for images
            string processedValue = ProcessMetadataValueForHtml(name, value, original3mfPath);
            html.AppendLine(processedValue);
            
            html.AppendLine("            </div>");
            html.AppendLine("        </div>");
        }

        private string ProcessMetadataValueForHtml(string metadataName, string value, string original3mfPath)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            // For Description, render HTML directly (after decoding entities)
            if (metadataName.Equals("Description", StringComparison.OrdinalIgnoreCase))
            {
                // Decode HTML entities (like &#34; for ")
                string decoded = DecodeHtmlEntities(value);
                
                // Process images in the HTML content
                string processedHtml = ProcessImagesInHtml(decoded, original3mfPath);
                return processedHtml;
            }

            // For other metadata, escape HTML and process images
            // Look for image URLs or references
            // Pattern for URLs (http/https)
            var urlPattern = @"(https?://[^\s<>""']+\.(jpg|jpeg|png|gif|webp|bmp|svg))";
            var urlMatches = Regex.Matches(value, urlPattern, RegexOptions.IgnoreCase);

            string processed = WebUtility.HtmlEncode(value);

            // Replace URLs with embedded images
            foreach (Match match in urlMatches)
            {
                string imageUrl = match.Value;
                string? base64Image = GetImageAsBase64FromUrl(imageUrl);
                if (!string.IsNullOrEmpty(base64Image))
                {
                    string imgTag = $"<div class=\"image-container\"><img src=\"data:image/{GetImageExtension(imageUrl)};base64,{base64Image}\" alt=\"Image from {WebUtility.HtmlEncode(imageUrl)}\" /></div>";
                    processed = processed.Replace(WebUtility.HtmlEncode(imageUrl), imgTag);
                }
            }

            // Look for local image references (relative paths or file references)
            var localImagePattern = @"([^\s<>""']+\.(jpg|jpeg|png|gif|webp|bmp|svg))";
            var localMatches = Regex.Matches(value, localImagePattern, RegexOptions.IgnoreCase);

            // Try to find local images in the 3MF archive
            foreach (Match match in localMatches)
            {
                string imagePath = match.Value;
                // Skip if it's already a URL
                if (imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? base64Image = GetImageAsBase64FromArchive(original3mfPath, imagePath);
                if (!string.IsNullOrEmpty(base64Image))
                {
                    string imgTag = $"<div class=\"image-container\"><img src=\"data:image/{GetImageExtension(imagePath)};base64,{base64Image}\" alt=\"Image: {WebUtility.HtmlEncode(imagePath)}\" /></div>";
                    processed = processed.Replace(WebUtility.HtmlEncode(imagePath), imgTag);
                }
            }

            return processed;
        }

        private string ProcessImagesInHtml(string htmlContent, string original3mfPath)
        {
            string processed = htmlContent;

            // Look for image URLs in img src attributes
            var imgSrcUrlPattern = @"<img[^>]+src=[""'](https?://[^""']+\.(jpg|jpeg|png|gif|webp|bmp|svg))[""'][^>]*>";
            var imgUrlMatches = Regex.Matches(htmlContent, imgSrcUrlPattern, RegexOptions.IgnoreCase);

            foreach (Match match in imgUrlMatches)
            {
                string imageUrl = match.Groups[1].Value;
                string? base64Image = GetImageAsBase64FromUrl(imageUrl);
                if (!string.IsNullOrEmpty(base64Image))
                {
                    string dataUri = $"data:image/{GetImageExtension(imageUrl)};base64,{base64Image}";
                    processed = processed.Replace(imageUrl, dataUri);
                }
            }

            // Look for image URLs in the content (not in tags)
            var urlPattern = @"(https?://[^\s<>""']+\.(jpg|jpeg|png|gif|webp|bmp|svg))";
            var urlMatches = Regex.Matches(htmlContent, urlPattern, RegexOptions.IgnoreCase);

            foreach (Match match in urlMatches)
            {
                // Skip if already in an img tag
                if (htmlContent.Substring(Math.Max(0, match.Index - 50), Math.Min(100, htmlContent.Length - Math.Max(0, match.Index - 50)))
                    .Contains("<img", StringComparison.OrdinalIgnoreCase))
                    continue;

                string imageUrl = match.Value;
                string? base64Image = GetImageAsBase64FromUrl(imageUrl);
                if (!string.IsNullOrEmpty(base64Image))
                {
                    string imgTag = $"<div class=\"image-container\"><img src=\"data:image/{GetImageExtension(imageUrl)};base64,{base64Image}\" alt=\"Image from {WebUtility.HtmlEncode(imageUrl)}\" /></div>";
                    processed = processed.Replace(imageUrl, imgTag);
                }
            }

            // Look for local image references
            var localImagePattern = @"([^\s<>""']+\.(jpg|jpeg|png|gif|webp|bmp|svg))";
            var localMatches = Regex.Matches(htmlContent, localImagePattern, RegexOptions.IgnoreCase);

            foreach (Match match in localMatches)
            {
                // Skip if already in an img tag or if it's a URL
                string imagePath = match.Value;
                if (imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    imagePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                int startPos = Math.Max(0, match.Index - 50);
                int length = Math.Min(100, htmlContent.Length - startPos);
                if (htmlContent.Substring(startPos, length).Contains("<img", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? base64Image = GetImageAsBase64FromArchive(original3mfPath, imagePath);
                if (!string.IsNullOrEmpty(base64Image))
                {
                    string imgTag = $"<div class=\"image-container\"><img src=\"data:image/{GetImageExtension(imagePath)};base64,{base64Image}\" alt=\"Image: {WebUtility.HtmlEncode(imagePath)}\" /></div>";
                    processed = processed.Replace(imagePath, imgTag);
                }
            }

            return processed;
        }

        private string DecodeHtmlEntities(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Decode numeric HTML entities (like &#34; for ")
            text = Regex.Replace(text, @"&#(\d+);", m =>
            {
                int code = int.Parse(m.Groups[1].Value);
                return ((char)code).ToString();
            });

            // Decode hex HTML entities (like &#x22; for ")
            text = Regex.Replace(text, @"&#x([0-9A-Fa-f]+);", m =>
            {
                int code = Convert.ToInt32(m.Groups[1].Value, 16);
                return ((char)code).ToString();
            });

            // Decode named HTML entities
            text = WebUtility.HtmlDecode(text);

            return text;
        }

        private string? GetImageAsBase64FromUrl(string url)
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    var imageBytes = httpClient.GetByteArrayAsync(url).Result;
                    return Convert.ToBase64String(imageBytes);
                }
            }
            catch
            {
                return null;
            }
        }

        private string? GetImageAsBase64FromArchive(string archivePath, string imagePath)
        {
            try
            {
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    // Try different path variations
                    string[] pathVariations = {
                        imagePath,
                        imagePath.Replace('\\', '/'),
                        imagePath.Replace('/', '\\'),
                        Path.Combine("Metadata", imagePath),
                        Path.Combine("Auxiliaries", imagePath)
                    };

                    foreach (var path in pathVariations)
                    {
                        var entry = archive.Entries.FirstOrDefault(e => 
                            e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                            e.FullName.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase) ||
                            e.Name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

                        if (entry != null)
                        {
                            using (var stream = entry.Open())
                            using (var memoryStream = new MemoryStream())
                            {
                                stream.CopyTo(memoryStream);
                                return Convert.ToBase64String(memoryStream.ToArray());
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return null;
        }

        private string GetImageExtension(string imagePath)
        {
            string ext = Path.GetExtension(imagePath).TrimStart('.').ToLower();
            if (ext == "jpg") ext = "jpeg";
            return ext;
        }

        private string DecodeXmlEntities(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            // Decode XML entities
            return text
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&#39;", "'")
                .Replace("&#x27;", "'")
                .Replace("&#x2F;", "/");
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            StatusTextBlock.Text = "";
        }

        private void Log(string message)
        {
            StatusTextBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        }
    }
}

