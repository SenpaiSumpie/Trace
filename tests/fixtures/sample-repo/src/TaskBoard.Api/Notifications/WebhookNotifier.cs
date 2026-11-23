using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TaskBoard.Api.Notifications;

/// <summary>Tells an external system that a task was completed by calling its webhook.</summary>
public sealed class WebhookNotifier(HttpClient http, IConfiguration configuration, ILogger<WebhookNotifier> logger)
{
    private const int MaxAttempts = 3;
    private const string SignatureHeader = "X-TaskBoard-Signature";

    public async Task TaskCompletedAsync(int taskId, CancellationToken cancellationToken)
    {
        var url = configuration["Webhooks:Url"];
        if (string.IsNullOrEmpty(url))
        {
            // Webhooks are optional: without a URL nothing is sent.
            return;
        }

        var body = JsonSerializer.Serialize(new { @event = "task.completed", taskId });

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            message.Headers.Add(SignatureHeader, Sign(body));

            try
            {
                using var response = await http.SendAsync(message, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Treated like a failed response: try again below.
            }

            if (attempt < MaxAttempts)
            {
                // Wait 1 second, then 2, doubling after each failed attempt.
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
            }
        }

        // The task stays completed; a lost notification is not worth failing the request.
        logger.LogWarning("Webhook for task {TaskId} was not delivered after {Attempts} attempts", taskId, MaxAttempts);
    }

    private string Sign(string body)
    {
        var secret = configuration["Webhooks:SigningSecret"] ?? string.Empty;
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }
}
