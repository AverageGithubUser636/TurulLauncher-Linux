using TurulMC.Core.Models;
namespace TurulMC.Core.Http;

public class LauncherHttpClient
{
    private readonly HttpClient _httpClient;

    public LauncherHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TurulMC-Launcher/2.0");
    }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<byte[]> GetBytesAsync(string url, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var memoryStream = new MemoryStream();

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;
            progress?.Report(totalRead);
        }

        return memoryStream.ToArray();
    }

    public async Task DownloadFileAsync(string url, string destinationPath, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var tempPath = destinationPath + ".tmp";
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

            var buffer = new byte[65536];
            long totalRead = 0;
            int bytesRead;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var fileName = Path.GetFileName(destinationPath);

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;
                var seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                progress?.Report(new DownloadProgress
                {
                    FileName = fileName,
                    BytesReceived = totalRead,
                    TotalBytes = totalBytes,
                    BytesPerSecond = totalRead / seconds,
                    Status = "downloading"
                });
            }

            fileStream.Close();

            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            File.Move(tempPath, destinationPath);

            progress?.Report(new DownloadProgress
            {
                FileName = fileName,
                BytesReceived = totalRead,
                TotalBytes = totalBytes,
                BytesPerSecond = stopwatch.Elapsed.TotalSeconds > 0 ? totalRead / stopwatch.Elapsed.TotalSeconds : 0,
                Status = "complete"
            });
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    public async Task<bool> CheckUrlExistsAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
